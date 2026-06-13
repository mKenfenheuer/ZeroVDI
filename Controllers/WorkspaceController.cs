using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using KSol.RDPGateway.RDP;
using OpenIddict.Abstractions;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// Implements the RemoteApp and Desktop Connections (RDWeb) web feed so the Windows
/// "Remote Desktop" client can subscribe to this gateway by URL and list authorized resources
/// directly, without using a web browser.
///
/// The client subscribes to <c>https://&lt;host&gt;/rdweb/feed/webfeed.aspx</c>. It authenticates
/// with HTTP Basic, receives a <c>ResourceCollection</c> XML document describing each desktop the
/// user may reach, and downloads the per-resource .rdp file referenced from the feed.
/// </summary>
[Route("")]
public class WorkspaceController : Controller
{
    private const string FeedNamespace = "http://schemas.microsoft.com/ts/2007/05/tswf";

    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly RdpFileGenerator _rdpGenerator;
    private readonly ILogger<WorkspaceController> _logger;

    public WorkspaceController(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        RdpFileGenerator rdpGenerator,
        ILogger<WorkspaceController> logger)
    {
        _context = context;
        _userManager = userManager;
        _signInManager = signInManager;
        _rdpGenerator = rdpGenerator;
        _logger = logger;
    }

    /// <summary>
    /// RD Web Access login endpoint. TSWorkspace authenticates here via NTLM/Negotiate (not a form
    /// POST) — it sends a Type1 Negotiate token, we reply with a Type2 challenge, it replies with
    /// Type3, we verify and set the Identity cookie. Browsers get the HTML form fallback.
    /// </summary>
    [HttpGet("RDWeb/Pages/login.aspx")]
    [HttpGet("RDWeb/Pages/{locale}/login.aspx")]
    [HttpPost("RDWeb/Pages/login.aspx")]
    [HttpPost("RDWeb/Pages/{locale}/login.aspx")]
    [Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken]
    public async Task<IActionResult> LoginPage(string? returnUrl = null,
        [FromForm(Name = "DomainUserName")] string? domainUserName = null,
        [FromForm(Name = "UserPass")] string? userPass = null)
    {
        var authHeader = Request.Headers.Authorization.ToString();

        // NTLM/Negotiate flow: TSWorkspace sends Negotiate token in Authorization header.
        if (authHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase) ||
            authHeader.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase))
        {
            var scheme = authHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase) ? "Negotiate" : "NTLM";
            var token = authHeader.Substring(scheme.Length + 1).Trim();
            var result = await HandleNtlmLoginAsync(token);

            if (result == null) // Type1 -> issued Type2 challenge
            {
                // NtlmChallengeB64 was set inside HandleNtlmLoginAsync; read it back from Items
                var challengeB64 = HttpContext.Items["NtlmChallenge"] as string;
                Response.Headers.Append("WWW-Authenticate", $"{scheme} {challengeB64}");
                return StatusCode(StatusCodes.Status401Unauthorized);
            }

            if (result == false) // Type3 failed
            {
                Response.Headers.Append("WWW-Authenticate", scheme);
                return StatusCode(StatusCodes.Status401Unauthorized);
            }

            // Type3 succeeded — sign in and redirect to feed
            var userId = HttpContext.Items["NtlmUserId"] as string;
            var user = userId != null ? await _userManager.FindByIdAsync(userId) : null;
            if (user != null)
            {
                await _signInManager.SignInAsync(user, isPersistent: true);
                _logger.LogInformation("RDWeb NTLM login success for {User}", user.UserName);
            }
            var target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? returnUrl : "/rdweb/feed/webfeed.aspx";
            return Redirect(target);
        }

        // HTML form POST fallback (browser)
        if (Request.Method == "POST" && !string.IsNullOrEmpty(domainUserName))
        {
            var userName = StripDomain(domainUserName.Trim());
            var password = userPass ?? string.Empty;
            var user = string.IsNullOrEmpty(userName) ? null : await _userManager.FindByNameAsync(userName);
            if (user == null || !await _userManager.CheckPasswordAsync(user, password))
            {
                _logger.LogWarning("RDWeb form login failed for {User}", userName);
                Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Content(BuildLoginPage(returnUrl, failed: true), "text/html; charset=utf-8");
            }
            await _signInManager.SignInAsync(user, isPersistent: true);
            _logger.LogInformation("RDWeb form login success for {User}", userName);
            var target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? returnUrl : "/rdweb/feed/webfeed.aspx";
            return Redirect(target);
        }

        // No auth header yet — issue Negotiate challenge (TSWorkspace) or show form (browser).
        var accept = Request.Headers.Accept.ToString();
        var isBrowser = accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
        if (isBrowser)
        {
            Response.Cookies.Append("TSWAClientID", Guid.NewGuid().ToString(), new CookieOptions
            {
                HttpOnly = false, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/"
            });
            return Content(BuildLoginPage(returnUrl, failed: false), "text/html; charset=utf-8");
        }

        Response.Headers.Append("WWW-Authenticate", "Negotiate");
        Response.Headers.Append("WWW-Authenticate", "NTLM");
        return StatusCode(StatusCodes.Status401Unauthorized);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] Challenge, DateTimeOffset Issued)>
        _ntlmWebChallenges = new();

    private async Task<bool?> HandleNtlmLoginAsync(string tokenB64)
    {
        byte[] token;
        try { token = Ntlm.TryUnwrapSpnego(Convert.FromBase64String(tokenB64)); }
        catch { return false; }

        var msgType = Ntlm.GetMessageType(token);
        _logger.LogInformation("NTLM {Type} ({Len}B): {Hex}",
            msgType, token.Length, Convert.ToHexString(token));

        switch (msgType)
        {
            case Ntlm.MessageType.Negotiate:
            {
                var clientFlags = Ntlm.GetNegotiateFlags(token);
                var challenge = Ntlm.NewServerChallenge();
                var type2 = Ntlm.BuildChallenge(challenge, clientFlags);
                var key = Convert.ToBase64String(token[..Math.Min(16, token.Length)]);
                _ntlmWebChallenges[key] = (challenge, DateTimeOffset.UtcNow);
                HttpContext.Items["NtlmChallenge"] = Convert.ToBase64String(type2);
                _logger.LogInformation("NTLM Type1 flags=0x{Flags:X8}; replying Type2 ({Len}B): {Hex}",
                    clientFlags, type2.Length, Convert.ToHexString(type2));
                // Prune old challenges
                foreach (var k in _ntlmWebChallenges.Keys.ToList())
                    if (_ntlmWebChallenges.TryGetValue(k, out var v) && v.Issued < DateTimeOffset.UtcNow.AddMinutes(-5))
                        _ntlmWebChallenges.TryRemove(k, out _);
                return null; // signal: challenge issued
            }
            case Ntlm.MessageType.Authenticate:
            {
                var t3 = Ntlm.ParseType3(token);
                if (t3 == null) { _logger.LogWarning("NTLM Type3 parse failed"); return false; }
                _logger.LogInformation("NTLM Type3 user={Domain}\\{User} ws={Ws} ntLen={NtLen}",
                    t3.Domain, t3.User, t3.Workstation, t3.NtChallengeResponse.Length);

                // Find most recent challenge
                var entry = _ntlmWebChallenges.Values.OrderByDescending(v => v.Issued).FirstOrDefault();
                if (entry.Challenge == null) return false;
                // Remove all old challenges
                foreach (var k in _ntlmWebChallenges.Keys.ToList()) _ntlmWebChallenges.TryRemove(k, out _);

                var userName = StripDomain(t3.User);
                var user = await _userManager.FindByNameAsync(userName);
                if (user?.NtHash == null) return false;

                var ntHash = Convert.FromHexString(user.NtHash);
                if (!Ntlm.VerifyNtlmV2(ntHash, t3.User, t3.Domain, entry.Challenge, t3.NtChallengeResponse))
                    return false;

                HttpContext.Items["NtlmUserId"] = user.Id;
                return true;
            }
            default: return false;
        }
    }

    /// <summary>
    /// Feed discovery endpoint. Some clients probe this before requesting the feed itself; we point
    /// them at the webfeed document.
    /// </summary>
    [HttpGet("rdweb/feed/webfeeddiscovery.aspx")]
    public IActionResult Discovery()
    {
        var feedUrl = $"{Request.Scheme}://{Request.Host}/rdweb/feed/webfeed.aspx";
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Workspaces xmlns="http://schemas.microsoft.com/ts/2008/09/tswcx" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <Workspace Name="KSol.IT RDP Gateway">
                <ID>ksol-rdpgw</ID>
                <FeedUrl>{feedUrl}</FeedUrl>
              </Workspace>
            </Workspaces>
            """;
        return Content(xml, "text/xml; charset=utf-8");
    }

    /// <summary>
    /// The subscription endpoint the user enters in the Remote Desktop / Windows App client.
    ///
    /// The modern client (RdCore) drives a two-step flow: it first treats the subscribed URL as a
    /// <b>discovery</b> endpoint and expects a <c>TenantFeedURLs</c> document (namespace
    /// <c>…/2014/03/tswfdiscovery</c>) listing the real feed URL(s); it then fetches each
    /// <c>FeedURL</c> and parses the <c>ResourceCollection</c>. Returning the ResourceCollection
    /// directly here fails discovery with "No such node (TenantFeedURLs)".
    ///
    /// So: <c>?mode=feed</c> ⇒ the ResourceCollection feed; otherwise ⇒ the TenantFeedURLs discovery
    /// document whose FeedURL points back here with <c>?mode=feed</c>.
    /// </summary>
    [HttpGet("rdweb/feed/webfeed.aspx")]
    [HttpPost("rdweb/feed/webfeed.aspx")]
    public async Task<IActionResult> WebFeed([FromQuery] string? mode = null)
    {
        var (user, challenge) = await AuthenticateFeedAsync();
        if (user == null)
        {
            return challenge!;
        }

        // Discovery phase: hand the client a TenantFeedURLs document pointing at the feed phase.
        if (!string.Equals(mode, "feed", StringComparison.OrdinalIgnoreCase))
        {
            var feedUrl = $"{Request.Scheme}://{Request.Host}/rdweb/feed/webfeed.aspx?mode=feed";
            return Content(BuildDiscovery(feedUrl), "text/xml");
        }

        // Feed phase: the ResourceCollection of the user's authorized desktops.
        var resources = await _context.RDPResourceUserAuthorizations
            .Where(a => a.UserId == user.Id)
            .Include(a => a.RDPResource)
            .Select(a => a.RDPResource!)
            .Where(r => r != null)
            .ToListAsync();

        var xml = BuildFeed(resources);
        return Content(xml, "text/xml");
    }

    /// <summary>
    /// Builds the <c>TenantFeedURLs</c> discovery document the Windows App expects at the subscribed
    /// URL. Declares a classic (non-ARM) resource feed and points the client at <paramref name="feedUrl"/>.
    /// </summary>
    private static string BuildDiscovery(string feedUrl)
    {
        var escaped = System.Security.SecurityElement.Escape(feedUrl);
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TenantFeedURLs UserIdType="OrgId" ClassicResources="true" ARMResources="false" xmlns="http://schemas.microsoft.com/ts/2014/03/tswfdiscovery">
              <TenantFeedURL TenantId="ksol-rdpgw" TenantDisplayName="KSol.IT RDP Gateway" WorkspaceName="KSol.IT RDP Gateway" ContactEmail="" ConsentStatus="Accepted" FeedURL="{escaped}" />
            </TenantFeedURLs>
            """;
    }

    /// <summary>
    /// Per-resource .rdp file referenced by the feed. Uses Basic auth so the desktop client (which
    /// has no browser cookie) can fetch it, and enforces the same per-user authorization check.
    /// </summary>
    [HttpGet("rdweb/feed/rdp/{id}.rdp")]
    public async Task<IActionResult> ResourceRdp(string id)
    {
        var (user, challenge) = await AuthenticateFeedAsync();
        if (user == null)
        {
            return challenge!;
        }

        var authorization = await _context.RDPResourceUserAuthorizations
            .Include(a => a.RDPResource)
            .FirstOrDefaultAsync(a => a.UserId == user.Id && a.RDPResourceId == id);

        if (authorization?.RDPResource == null)
        {
            return NotFound();
        }

        // gatewayhostname must be the public host the client can reach, including a non-default
        // port. Request.Host.Value is host[:port] (resolved from forwarded headers behind a proxy).
        var rdp = _rdpGenerator.Generate(authorization.RDPResource, Request.Host.Value, user.UserName);
        return File(Encoding.UTF8.GetBytes(rdp), "application/x-rdp");
    }

    // A generic 32x32 desktop icon, embedded so the feed's <Icons> can reference a real image. The
    // RD client requires each Resource to carry icons; these are not sensitive, so the endpoint is
    // anonymous. (.ico is a PNG-in-ICO container; .png is the raw PNG.)
    private static readonly byte[] IconIco = Convert.FromBase64String(
        "AAABAAEAICAAAAEAIACGAAAAFgAAAIlQTkcNChoKAAAADUlIRFIAAAAgAAAAIAgGAAAAc3p69AAAAE1JREFUeNpjYBgFo2AwATk5uf/0wHgdYFJxgqZ41AGjDhh1wKgDRh0w6oBRBwx+Bwxog4RYEBAQ8B8bplvTbeQ5AJeFhPDwccAoGNYAAAoLduPJtjYLAAAAAElFTkSuQmCC");
    private static readonly byte[] IconPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAATUlEQVR42mNgGAWjYDABOTm5//TAeB1gUnGCpnjUAaMOGHXAqANGHTDqgFEHDH4HDGiDhFgQEBDwHxumW9Nt5DkAl4WE8PBxwCgY1gAACgt248m2NgsAAAAASUVORK5CYII=");

    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    [HttpGet("rdweb/feed/icon/{id}.ico")]
    public IActionResult ResourceIconIco(string id) => File(IconIco, "image/x-icon");

    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    [HttpGet("rdweb/feed/icon/{id}.png")]
    public IActionResult ResourceIconPng(string id) => File(IconPng, "image/png");

    /// <summary>
    /// Resolves the authenticated user for a feed request, trying every flow MSRDC / RD Web Access
    /// may use, in order:
    /// <list type="number">
    /// <item>The ASP.NET Identity auth cookie (RD Web Access forms flow) — what MSRDC uses for
    ///       non-Azure subscriptions after the user signs in at the login page.</item>
    /// <item>An OAuth/OIDC Bearer token (validated by the configured JWT bearer scheme).</item>
    /// <item>HTTP Basic credentials (legacy clients / scripts).</item>
    /// </list>
    /// Returns the user, or null if none of the flows authenticated the request.
    /// </summary>
    private async Task<ApplicationUser?> ResolveUserAsync()
    {
        // 1) Identity cookie (RD Web Access forms flow) — surfaces on User via the default scheme.
        if (User?.Identity?.IsAuthenticated == true)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user != null)
                return user;
        }

        // 2) OAuth/OIDC Bearer token — validated by the OpenIddict validation scheme.
        var bearer = await HttpContext.AuthenticateAsync(
            OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (bearer.Succeeded && bearer.Principal != null)
        {
            // The OIDC subject ("sub") carries the user id.
            var subject = bearer.Principal.GetClaim(OpenIddict.Abstractions.OpenIddictConstants.Claims.Subject);
            var user = subject != null ? await _userManager.FindByIdAsync(subject) : null;
            if (user != null)
                return user;
        }

        // 3) HTTP Basic fallback.
        return await AuthenticateBasicAsync();
    }

    /// <summary>
    /// Authenticates a feed/.rdp request and, if needed, drives the NTLM handshake on the resource
    /// URL itself (the IIS Windows-Authentication model the RD client expects). TSWorkspace does not
    /// carry the forms cookie to the feed — it re-authenticates with NTLM on each resource URL — so
    /// the feed endpoint must complete the Type1→Type2→Type3 exchange in place and return the body on
    /// the authenticated request.
    ///
    /// Returns a tuple: a non-null <c>User</c> means proceed and emit the resource; otherwise
    /// <c>Challenge</c> is the 401/redirect to return verbatim.
    /// </summary>
    private async Task<(ApplicationUser? User, IActionResult? Challenge)> AuthenticateFeedAsync()
    {
        // Cookie / Bearer / Basic first (browsers, OAuth tooling, scripts, and any client that does
        // carry the cookie).
        var user = await ResolveUserAsync();
        if (user != null)
            return (user, null);

        // NTLM/Negotiate handshake directly on this endpoint.
        var authHeader = Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase) ||
            authHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase))
        {
            var scheme = authHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase) ? "Negotiate" : "NTLM";
            var token = authHeader.Substring(scheme.Length + 1).Trim();
            var result = await HandleNtlmLoginAsync(token);

            if (result == null) // Type1 → reply with Type2 challenge
            {
                var challengeB64 = HttpContext.Items["NtlmChallenge"] as string;
                Response.Headers.Append("WWW-Authenticate", $"{scheme} {challengeB64}");
                return (null, StatusCode(StatusCodes.Status401Unauthorized));
            }

            if (result == false) // Type3 verification failed → restart
            {
                Response.Headers.Append("WWW-Authenticate", scheme);
                return (null, StatusCode(StatusCodes.Status401Unauthorized));
            }

            // Type3 succeeded.
            var userId = HttpContext.Items["NtlmUserId"] as string;
            var authed = userId != null ? await _userManager.FindByIdAsync(userId) : null;
            if (authed != null)
            {
                // Also set the Identity cookie, so any client that does honor it skips the handshake
                // on subsequent requests.
                await _signInManager.SignInAsync(authed, isPersistent: true);
                return (authed, null);
            }
            Response.Headers.Append("WWW-Authenticate", scheme);
            return (null, StatusCode(StatusCodes.Status401Unauthorized));
        }

        // No usable auth on the request — issue the appropriate challenge for the client.
        return (null, ChallengeFeed());
    }

    /// <summary>
    /// Validates the HTTP Basic Authorization header against ASP.NET Identity.
    /// Returns the authenticated user, or null if authentication failed/was absent.
    /// </summary>
    private async Task<ApplicationUser?> AuthenticateBasicAsync()
    {
        string authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string userName;
        string password;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader.Substring("Basic ".Length).Trim()));
            var separator = decoded.IndexOf(':');
            if (separator < 0)
            {
                return null;
            }
            userName = decoded.Substring(0, separator);
            password = decoded.Substring(separator + 1);
        }
        catch (FormatException)
        {
            return null;
        }

        // Some clients send DOMAIN\user or user@domain; Identity stores the bare username/email.
        userName = StripDomain(userName);

        _logger.LogInformation("Workspace feed: Basic attempt for {User}", userName);

        var user = await _userManager.FindByNameAsync(userName);
        if (user == null)
        {
            _logger.LogWarning("Workspace feed: Basic unknown user {User}", userName);
            return null;
        }

        if (!await _userManager.CheckPasswordAsync(user, password))
        {
            _logger.LogWarning("Workspace feed: Basic bad password for {User}", userName);
            return null;
        }

        _logger.LogInformation("Workspace feed: Basic success for {User}", userName);
        return user;
    }

    /// <summary>
    /// Normalizes DOMAIN\user and user@domain forms down to the bare username Identity stores.
    /// </summary>
    private static string StripDomain(string userName)
    {
        var slash = userName.IndexOf('\\');
        if (slash >= 0)
        {
            return userName.Substring(slash + 1);
        }
        return userName;
    }

    /// <summary>
    /// Issues an authentication challenge for the feed, tailored to the requesting client.
    ///
    /// Remote Desktop clients — the macOS app shell (User-Agent <c>com.microsoft.rdc.*</c> /
    /// <c>RdCore</c>, which probes the feed first with no Accept header) and the RADC feed engine
    /// (<c>TSWorkspace/*</c>, Accept <c>application/x-msts-radc+xml</c>) — authenticate with
    /// <b>NTLM/Negotiate on the feed URL itself</b> (the IIS Windows-Authentication model). They do
    /// not carry a forms cookie to the feed, so a 302 to login.aspx just bounces back unauthenticated
    /// (redirect loop) and a <c>Bearer</c> challenge makes them think we're an Azure AVD/ARM endpoint
    /// and bail with "authentication method not supported". So RD clients get <c>401 Negotiate, NTLM</c>
    /// here and the handshake is completed in <see cref="AuthenticateFeedAsync"/>.
    ///
    /// Other non-browser callers (scripts, OAuth tooling) get Basic + Bearer.
    /// </summary>
    private IActionResult ChallengeFeed()
    {
        var accept = Request.Headers.Accept.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        var isRdClient = userAgent.Contains("TSWorkspace", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("com.microsoft.rdc", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("RdCore", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("x-msts-radc", StringComparison.OrdinalIgnoreCase);

        if (isRdClient)
        {
            Response.Headers.Append("WWW-Authenticate", "Negotiate");
            Response.Headers.Append("WWW-Authenticate", "NTLM");
            return StatusCode(StatusCodes.Status401Unauthorized);
        }

        // Other tooling (scripts, OAuth-capable callers): advertise Basic + Bearer.
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        Response.Headers.Append("WWW-Authenticate",
            $"Bearer authorization_uri=\"{baseUrl}/connect/authorize\", realm=\"{AuthCrypto.Realm}\"");
        Response.Headers.Append("WWW-Authenticate", $"Basic realm=\"{AuthCrypto.Realm}\"");
        return StatusCode(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Renders the minimal RD Web Access login form. Field names match the RD Web contract
    /// (<c>DomainUserName</c>, <c>UserPass</c>) so the Remote Desktop client's embedded login view
    /// recognizes and can auto-fill / submit it. Kept deliberately simple.
    /// </summary>
    private string BuildLoginPage(string? returnUrl, bool failed)
    {
        var ru = System.Net.WebUtility.HtmlEncode(returnUrl ?? "/rdweb/feed/webfeed.aspx");
        var error = failed
            ? "<p style=\"color:#c00\">The user name or password that you entered is not valid. Please try again.</p>"
            : string.Empty;

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>KSol.IT RDP Gateway — Sign in</title>
              <style>
                body { font-family: -apple-system, Segoe UI, sans-serif; max-width: 360px; margin: 48px auto; padding: 0 16px; }
                h1 { font-size: 20px; } label { display:block; margin: 12px 0 4px; }
                input[type=text], input[type=password] { width: 100%; padding: 8px; box-sizing: border-box; }
                button { margin-top: 16px; padding: 10px 16px; width: 100%; }
              </style>
            </head>
            <body>
              <h1>KSol.IT RDP Gateway</h1>
              {{error}}
              <form id="FrmLogin" method="post" action="login.aspx">
                <label for="DomainUserName">User name</label>
                <input type="text" id="DomainUserName" name="DomainUserName" autocomplete="username" autocapitalize="off" autocorrect="off" />
                <label for="UserPass">Password</label>
                <input type="password" id="UserPass" name="UserPass" autocomplete="current-password" />
                <input type="hidden" name="ReturnUrl" value="{{ru}}" />
                <input type="hidden" name="WorkSpaceID" value="ksol-rdpgw" />
                <input type="hidden" name="isUtf8" value="1" />
                <input type="hidden" name="flags" value="0" />
                <button type="submit">Sign in</button>
              </form>
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Builds the ResourceCollection feed document for the supplied desktops.
    /// </summary>
    private string BuildFeed(IEnumerable<RDPResource> resources)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false
        };

        // Write through a UTF-8 stream so the XML declaration advertises utf-8 (matching the
        // response Content-Type). Writing to a StringBuilder would force a utf-16 declaration,
        // which some RemoteApp/Desktop clients reject.
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("ResourceCollection", FeedNamespace);
            writer.WriteAttributeString("PubDate", now);
            writer.WriteAttributeString("SchemaVersion", "2.0");

            writer.WriteStartElement("Publisher");
            writer.WriteAttributeString("LastUpdated", now);
            writer.WriteAttributeString("Name", "KSol.IT RDP Gateway");
            writer.WriteAttributeString("ID", "ksol-rdpgw");
            writer.WriteAttributeString("Description", "");

            // Each target server is referenced as a terminal server in the feed.
            writer.WriteStartElement("Resources");
            foreach (var resource in resources)
            {
                WriteResource(writer, resource, now, baseUrl);
            }
            writer.WriteEndElement(); // Resources

            // Declare the terminal servers referenced by the resources above.
            writer.WriteStartElement("TerminalServers");
            foreach (var resource in resources)
            {
                writer.WriteStartElement("TerminalServer");
                writer.WriteAttributeString("ID", ToNcName(resource.Id));
                writer.WriteAttributeString("Name", resource.Id);
                writer.WriteAttributeString("LastUpdated", now);
                writer.WriteEndElement(); // TerminalServer
            }
            writer.WriteEndElement(); // TerminalServers

            writer.WriteEndElement(); // Publisher
            writer.WriteEndElement(); // ResourceCollection
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteResource(XmlWriter writer, RDPResource resource, string now, string baseUrl)
    {
        var realId = resource.Id;
        // The feed's ID/Alias/Ref linkage is typed xs:ID/xs:IDREF in the TSWF schema, so it must be a
        // valid NCName (letter/underscore start, no dots/colons). The resource id is a GUID, which is
        // not a valid NCName on its own, so use an NCName-safe token for the in-document identifiers.
        // The GUID itself stays in the .rdp URL path (and is the .rdp full address).
        var ncId = ToNcName(realId);
        // Surface the backing machine's power state as a secondary cue in the resource title.
        var baseTitle = string.IsNullOrEmpty(resource.Name) ? realId : resource.Name!;
        var title = resource.PowerState switch
        {
            Models.ResourcePowerState.Stopped => $"{baseTitle} (stopped)",
            Models.ResourcePowerState.Suspended => $"{baseTitle} (suspended)",
            Models.ResourcePowerState.Starting => $"{baseTitle} (starting…)",
            _ => baseTitle,
        };
        var rdpUrl = $"{baseUrl}/rdweb/feed/rdp/{Uri.EscapeDataString(realId)}.rdp";

        writer.WriteStartElement("Resource");
        writer.WriteAttributeString("ID", ncId);
        writer.WriteAttributeString("Alias", ncId);
        writer.WriteAttributeString("Title", title);
        writer.WriteAttributeString("LastUpdated", now);
        writer.WriteAttributeString("Type", "Desktop");
        writer.WriteAttributeString("ShowByDefault", "true");

        // Icons are required by the RD client (it rejects/won't parse a resource without them, as a
        // real RDWeb/AVD feed always carries them). Reference the generic icon endpoints. No empty
        // <FileExtensions/> — a working feed omits it for Desktop resources and its presence here
        // tripped the client's parser.
        writer.WriteStartElement("Icons");
        writer.WriteStartElement("IconRaw");
        writer.WriteAttributeString("FileType", "Ico");
        writer.WriteAttributeString("FileURL", $"{baseUrl}/rdweb/feed/icon/{Uri.EscapeDataString(realId)}.ico");
        writer.WriteAttributeString("ETag", "");
        writer.WriteEndElement(); // IconRaw
        writer.WriteStartElement("Icon32");
        writer.WriteAttributeString("Dimensions", "32x32");
        writer.WriteAttributeString("FileType", "Png");
        writer.WriteAttributeString("FileURL", $"{baseUrl}/rdweb/feed/icon/{Uri.EscapeDataString(realId)}.png");
        writer.WriteAttributeString("ETag", "");
        writer.WriteEndElement(); // Icon32
        writer.WriteEndElement(); // Icons

        writer.WriteStartElement("HostingTerminalServers");
        writer.WriteStartElement("HostingTerminalServer");

        // ResourceFile carries only attributes per the TSWF schema — no child elements. The client
        // fetches the .rdp from URL with its own NTLM/cookie auth, where Request.Host resolves to the
        // public gateway host (via forwarded headers behind the proxy).
        writer.WriteStartElement("ResourceFile");
        writer.WriteAttributeString("FileExtension", ".rdp");
        writer.WriteAttributeString("URL", rdpUrl);
        writer.WriteEndElement(); // ResourceFile

        writer.WriteStartElement("TerminalServerRef");
        writer.WriteAttributeString("Ref", ncId);
        writer.WriteEndElement(); // TerminalServerRef

        writer.WriteEndElement(); // HostingTerminalServer
        writer.WriteEndElement(); // HostingTerminalServers

        writer.WriteEndElement(); // Resource
    }

    /// <summary>
    /// Maps an arbitrary identifier (e.g. an IP address) to a valid XML NCName for use in the feed's
    /// xs:ID/xs:IDREF attributes: prefixes a letter and replaces any character that isn't a letter,
    /// digit, hyphen or underscore with an underscore. Deterministic, so the same resource always
    /// gets the same in-document ID (and TerminalServerRef matches its TerminalServer).
    /// </summary>
    private static string ToNcName(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "r_" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder("r_", value.Length + 2);
        foreach (var c in value)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }
}
