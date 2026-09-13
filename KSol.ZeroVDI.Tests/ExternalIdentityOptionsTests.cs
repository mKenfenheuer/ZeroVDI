using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// Federation is opt-in, and a half-configured section must fail visibly at startup rather than
/// registering a provider that throws on the first sign-in attempt.
/// </summary>
public class ExternalIdentityOptionsTests
{
    [Fact]
    public void DisabledByDefault()
    {
        var options = new ExternalIdentityOptions();

        Assert.False(options.Enabled);
        Assert.False(options.IsConfigured);
        Assert.Null(options.ConfigurationProblem);   // not enabled is not a problem
    }

    [Fact]
    public void EnabledWithoutAnAuthority_NamesTheMissingKey()
    {
        var options = new ExternalIdentityOptions { Enabled = true, ClientId = "zerovdi" };

        Assert.False(options.IsConfigured);
        Assert.Contains("Authority", options.ConfigurationProblem);
    }

    [Fact]
    public void EnabledWithoutAClientId_NamesTheMissingKey()
    {
        var options = new ExternalIdentityOptions { Enabled = true, Authority = "https://idp.example.com" };

        Assert.False(options.IsConfigured);
        Assert.Contains("ClientId", options.ConfigurationProblem);
    }

    [Fact]
    public void FullyConfigured_IsUsableAndHasNoProblem()
    {
        var options = new ExternalIdentityOptions
        {
            Enabled = true,
            Authority = "https://idp.example.com",
            ClientId = "zerovdi",
        };

        Assert.True(options.IsConfigured);
        Assert.Null(options.ConfigurationProblem);
    }

    [Fact]
    public void FederatedSignInsCountAsMultiFactorUnlessTurnedOff()
    {
        // Enforcing MFA at the provider is the usual reason to federate; demanding a second ZeroVDI
        // factor on top must be a deliberate choice, not the default.
        Assert.True(new ExternalIdentityOptions().SatisfiesMfa);
    }
}
