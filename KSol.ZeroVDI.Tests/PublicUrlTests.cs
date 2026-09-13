using KSol.ZeroVDI.RDP;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// Password-reset and e-mail-change links are built here. Deriving them from the request's Host header
/// is how a forged request makes a victim's reset e-mail point at the attacker's site, so the
/// configured public address must win whenever it is set.
/// </summary>
public class PublicUrlTests
{
    private static PublicUrl Build(string? baseUrl)
    {
        var settings = new List<KeyValuePair<string, string?>>();
        if (baseUrl != null) settings.Add(new("App:PublicBaseUrl", baseUrl));
        return new PublicUrl(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static HttpRequest RequestFrom(string host, string scheme = "https")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        return context.Request;
    }

    [Fact]
    public void ConfiguredBaseUrl_WinsOverTheRequestHost()
    {
        var url = Build("https://vdi.example.com").Absolute("/Identity/Account/ResetPassword?code=abc",
            RequestFrom("attacker.test"));

        Assert.StartsWith("https://vdi.example.com/", url);
        Assert.DoesNotContain("attacker.test", url);
    }

    [Fact]
    public void TrailingSlashesDoNotProduceADoubleSlash()
    {
        var url = Build("https://vdi.example.com/").Absolute("/reset", RequestFrom("vdi.example.com"));

        Assert.Equal("https://vdi.example.com/reset", url);
    }

    [Fact]
    public void WithoutConfiguration_ItFallsBackToTheRequest()
    {
        var publicUrl = Build(null);

        Assert.False(publicUrl.IsConfigured);
        Assert.Equal("https://vdi.example.com/reset", publicUrl.Absolute("/reset", RequestFrom("vdi.example.com")));
    }
}
