namespace KSol.ZeroVDI.RDP;

/// <summary>
/// The address users reach ZeroVDI at, for links that leave the browser (password-reset and e-mail
/// change links, connector enrolment commands). Read from <c>App:PublicBaseUrl</c>
/// (e.g. <c>https://vdi.example.com</c>). Until 0.6.36 those links were built from the incoming
/// request's <c>Host</c> header; with <c>AllowedHosts</c> at <c>*</c> an attacker could request a
/// password reset for a victim with a forged Host header and the victim's e-mail then carried a link
/// to the attacker's domain (host-header poisoning). When the setting is absent the request host is
/// still used — but only after the forwarded-headers middleware has applied the trusted proxy's
/// values — and a startup warning asks for it to be set in production.
/// </summary>
public sealed class PublicUrl
{
    public Uri? BaseUrl { get; }

    public PublicUrl(IConfiguration config)
    {
        var raw = config["App:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(raw.TrimEnd('/'), UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            BaseUrl = u;
    }

    public bool IsConfigured => BaseUrl != null;

    /// <summary>Absolute form of an app-relative path (as returned by <c>Url.Page</c>/<c>Url.Action</c> without a protocol).</summary>
    public string Absolute(string? relativePath, HttpRequest request)
    {
        var path = relativePath ?? "/";
        if (!path.StartsWith('/')) path = "/" + path;
        if (BaseUrl != null) return BaseUrl.GetLeftPart(UriPartial.Authority) + BaseUrl.AbsolutePath.TrimEnd('/') + path;
        return $"{request.Scheme}://{request.Host}{request.PathBase}{path}";
    }

    /// <summary>The origin (scheme://host[:port]) to show users, e.g. in the connector enrolment command.</summary>
    public string Origin(HttpRequest request)
        => BaseUrl != null ? BaseUrl.GetLeftPart(UriPartial.Authority) : $"{request.Scheme}://{request.Host}";
}
