using System.Security.Cryptography;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Per-request Content-Security-Policy nonce. A nonce lets the policy name exactly the inline scripts
/// this application rendered, so <c>'unsafe-inline'</c> can be dropped from <c>script-src</c> — without
/// it, any injected <c>&lt;script&gt;</c> anywhere in a page executes, which is the whole of reflected
/// and stored XSS. The value must be unguessable and fresh for every response, or an attacker can
/// simply include it in the injected markup.
/// </summary>
public static class CspNonce
{
    private const string ItemKey = "csp-nonce";

    /// <summary>
    /// The nonce for this request, generated on first use. Both the header and the rendered script tags
    /// read it through here, so they cannot disagree.
    /// </summary>
    public static string Get(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var existing) && existing is string nonce) return nonce;
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items[ItemKey] = value;
        return value;
    }
}
