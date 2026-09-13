using KSol.ZeroVDI.RDP;
using Microsoft.AspNetCore.Http;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// The nonce is the whole of the script-src policy. If it repeated across responses an attacker could
/// simply include a known value in injected markup; if it differed between the header and the rendered
/// tags, every script in the application would stop running.
/// </summary>
public class CspNonceTests
{
    [Fact]
    public void TheSameRequestAlwaysSeesTheSameNonce()
    {
        var context = new DefaultHttpContext();

        Assert.Equal(CspNonce.Get(context), CspNonce.Get(context));
    }

    [Fact]
    public void EachRequestGetsAFreshNonce()
    {
        var values = Enumerable.Range(0, 50).Select(_ => CspNonce.Get(new DefaultHttpContext())).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Fact]
    public void TheNonceCarriesEnoughEntropyToBeUnguessable()
    {
        var nonce = CspNonce.Get(new DefaultHttpContext());

        Assert.Equal(16, Convert.FromBase64String(nonce).Length);
    }
}
