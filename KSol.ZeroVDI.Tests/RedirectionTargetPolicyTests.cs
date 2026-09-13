using KSol.ZeroVDI.RDP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// The redirect target arrives from the RDP host, so these rules are what stops a compromised desktop
/// from aiming the gateway — which connects from inside the datacentre with the user's credentials —
/// at a machine of its choosing.
/// </summary>
public class RedirectionTargetPolicyTests
{
    private static RedirectionTargetPolicy Policy(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new RedirectionTargetPolicy(config, NullLogger<RedirectionTargetPolicy>.Instance);
    }

    [Fact]
    public void NoTarget_StaysOnTheOriginalHost()
    {
        var decision = Policy().Resolve(null, "10.1.2.3");

        Assert.Equal("10.1.2.3", decision.Host);
        Assert.False(decision.Redirected);
        Assert.Null(decision.Refusal);
    }

    [Fact]
    public void SameHost_IsNotTreatedAsARedirect()
    {
        // GNOME Remote Desktop's "Remote Login" hands the session off on the same machine.
        var decision = Policy().Resolve("10.1.2.3", "10.1.2.3");

        Assert.False(decision.Redirected);
        Assert.Null(decision.Refusal);
    }

    [Fact]
    public void DifferentRoutableHost_IsFollowed()
    {
        var decision = Policy().Resolve("10.9.9.9", "10.1.2.3");

        Assert.True(decision.Redirected);
        Assert.Equal("10.9.9.9", decision.Host);
    }

    [Theory]
    [InlineData("127.0.0.1")]        // the gateway's own loopback services
    [InlineData("169.254.169.254")]  // cloud instance metadata
    [InlineData("169.254.1.1")]      // link-local generally
    [InlineData("224.0.0.1")]        // multicast
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    public void NonRoutableTargets_AreRefused(string target)
    {
        var decision = Policy().Resolve(target, "10.1.2.3");

        Assert.False(decision.Redirected);
        Assert.Equal("10.1.2.3", decision.Host);
        Assert.NotNull(decision.Refusal);
    }

    [Fact]
    public void FollowDisabled_RefusesEvenARoutableTarget()
    {
        var decision = Policy(("Redirection:FollowTargetHost", "false")).Resolve("10.9.9.9", "10.1.2.3");

        Assert.False(decision.Redirected);
        Assert.Contains("FollowTargetHost", decision.Refusal);
    }

    [Fact]
    public void AllowList_AdmitsAListedAddressAndRefusesAnythingElse()
    {
        var policy = Policy(("Redirection:AllowedTargetHosts:0", "10.9.9.9"));

        Assert.True(policy.Resolve("10.9.9.9", "10.1.2.3").Redirected);
        Assert.False(policy.Resolve("10.9.9.10", "10.1.2.3").Redirected);
    }

    [Fact]
    public void AllowList_SupportsADomainSuffix()
    {
        var policy = Policy(("Redirection:AllowedTargetHosts:0", ".rds.example.com"));

        // The suffix entry must not admit a look-alike domain that merely ends in the same letters.
        Assert.False(policy.Resolve("evil-rds.example.com.attacker.test", "10.1.2.3").Redirected);
    }
}
