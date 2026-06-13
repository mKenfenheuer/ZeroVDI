using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using KSol.RDPGateway.RDP;

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
    /// The RemoteApp and Desktop Connections web feed. Authenticated with HTTP Basic.
    /// </summary>
    [HttpGet("rdweb/feed/webfeed.aspx")]
    [HttpPost("rdweb/feed/webfeed.aspx")]
    public async Task<IActionResult> WebFeed()
    {
        var user = await AuthenticateBasicAsync();
        if (user == null)
        {
            return ChallengeBasic();
        }

        var resources = await _context.RDPResourceUserAuthorizations
            .Where(a => a.UserId == user.Id)
            .Include(a => a.RDPResource)
            .Select(a => a.RDPResource!)
            .Where(r => r != null)
            .ToListAsync();

        var xml = BuildFeed(resources, user.UserName);
        return Content(xml, "text/xml; charset=utf-8");
    }

    /// <summary>
    /// Per-resource .rdp file referenced by the feed. Uses Basic auth so the desktop client (which
    /// has no browser cookie) can fetch it, and enforces the same per-user authorization check.
    /// </summary>
    [HttpGet("rdweb/feed/rdp/{id}.rdp")]
    public async Task<IActionResult> ResourceRdp(string id)
    {
        var user = await AuthenticateBasicAsync();
        if (user == null)
        {
            return ChallengeBasic();
        }

        var authorization = await _context.RDPResourceUserAuthorizations
            .Include(a => a.RDPResource)
            .FirstOrDefaultAsync(a => a.UserId == user.Id && a.RDPResourceId == id);

        if (authorization?.RDPResource == null)
        {
            return NotFound();
        }

        var rdp = _rdpGenerator.Generate(authorization.RDPResource, Request.Host.Host, user.UserName);
        return File(Encoding.UTF8.GetBytes(rdp), "application/x-rdp");
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

        var user = await _userManager.FindByNameAsync(userName);
        if (user == null)
        {
            _logger.LogWarning("Workspace feed: unknown user {User}", userName);
            return null;
        }

        if (!await _userManager.CheckPasswordAsync(user, password))
        {
            _logger.LogWarning("Workspace feed: bad password for {User}", userName);
            return null;
        }

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

    private IActionResult ChallengeBasic()
    {
        Response.Headers.WWWAuthenticate = "Basic realm=\"KSol.IT RDP Gateway\"";
        return StatusCode(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Builds the ResourceCollection feed document for the supplied desktops.
    /// </summary>
    private string BuildFeed(IEnumerable<RDPResource> resources, string? publisherUser)
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
            writer.WriteAttributeString("SchemaVersion", "1.1");

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
                writer.WriteAttributeString("ID", resource.ResourceIdentifier ?? "");
                writer.WriteAttributeString("Name", resource.ResourceIdentifier ?? "");
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

    private void WriteResource(XmlWriter writer, RDPResource resource, string now, string baseUrl)
    {
        var id = resource.ResourceIdentifier ?? Guid.NewGuid().ToString();
        var title = string.IsNullOrEmpty(resource.Name) ? id : resource.Name!;
        var rdpUrl = $"{baseUrl}/rdweb/feed/rdp/{Uri.EscapeDataString(id)}.rdp";

        writer.WriteStartElement("Resource");
        writer.WriteAttributeString("ID", id);
        writer.WriteAttributeString("Alias", id);
        writer.WriteAttributeString("Title", title);
        writer.WriteAttributeString("LastUpdated", now);
        writer.WriteAttributeString("Type", "Desktop");

        // No custom icons published; clients fall back to the default desktop icon.
        writer.WriteStartElement("Icons");
        writer.WriteEndElement();

        writer.WriteStartElement("FileExtensions");
        writer.WriteEndElement();

        writer.WriteStartElement("HostingTerminalServers");
        writer.WriteStartElement("HostingTerminalServer");

        writer.WriteStartElement("ResourceFile");
        writer.WriteAttributeString("FileExtension", ".rdp");
        writer.WriteAttributeString("URL", rdpUrl);
        writer.WriteEndElement(); // ResourceFile

        writer.WriteStartElement("TerminalServerRef");
        writer.WriteAttributeString("Ref", id);
        writer.WriteEndElement(); // TerminalServerRef

        writer.WriteEndElement(); // HostingTerminalServer
        writer.WriteEndElement(); // HostingTerminalServers

        writer.WriteEndElement(); // Resource
    }
}
