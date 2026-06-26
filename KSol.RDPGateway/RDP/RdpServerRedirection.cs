using System.Buffers.Binary;
using System.Text;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Parses an RDP Server Redirection PDU ([MS-RDPBCGR] 2.2.13). A session broker / load balancer — e.g.
/// GNOME Remote Desktop's system "Remote Login" mode — sends this on the (TLS-decrypted) host→client
/// stream to hand the connection off to the real session: it carries a LoadBalanceInfo routing token
/// (and optionally a target address and credentials), then the broker disconnects with
/// <c>ERRINFO_CB_CONNECTION_CANCELLED (0x00010409)</c>. The gateway honors it by reconnecting the host
/// leg with the routing token in the X.224 Connection Request (see <see cref="RdpHostConnection"/>).
/// </summary>
public sealed class RdpServerRedirection
{
    // RedirectionFlags ([MS-RDPBCGR] 2.2.13.1).
    private const uint LB_TARGET_NET_ADDRESS = 0x00000001;
    private const uint LB_LOAD_BALANCE_INFO  = 0x00000002;
    private const uint LB_USERNAME           = 0x00000004;
    private const uint LB_DOMAIN             = 0x00000008;
    private const uint LB_PASSWORD           = 0x00000010;
    private const uint LB_DONTSTOREUSERNAME  = 0x00000020;
    private const uint LB_SMARTCARD_LOGON    = 0x00000040;
    private const uint LB_NOREDIRECT         = 0x00000080;
    private const uint LB_TARGET_FQDN        = 0x00000100;
    private const uint LB_TARGET_NETBIOS_NAME = 0x00000200;
    private const uint LB_TARGET_NET_ADDRESSES = 0x00000800;
    private const uint LB_CLIENT_TSV_URL     = 0x00001000;
    private const uint LB_SERVER_TSV_CAPABLE = 0x00002000;
    private const uint LB_PASSWORD_IS_PK_ENCRYPTED = 0x00004000;
    private const uint LB_REDIRECTION_GUID   = 0x00008000;
    private const uint LB_TARGET_CERTIFICATE = 0x00010000;

    /// <summary>The raw LoadBalanceInfo routing token (e.g. <c>Cookie: msts=...\r\n</c>), or null.</summary>
    public byte[]? LoadBalanceInfo { get; private init; }
    /// <summary>Target host (FQDN or net address) to reconnect to, or null to reuse the original host.</summary>
    public string? TargetHost { get; private init; }
    public string? Username { get; private init; }
    public string? Domain { get; private init; }
    /// <summary>
    /// Session credential the broker handed back for the redirected session (e.g. GNOME Remote Desktop's
    /// "Remote Login" generates a one-time username/password). Used for the reconnect's NLA instead of the
    /// original SSO credentials. Null when the redirect did not carry a (usable, non-PK-encrypted) password.
    /// </summary>
    public string? Password { get; private init; }
    public uint RedirFlags { get; private init; }

    /// <summary>
    /// Builds a redirection from an already-known routing token + credentials (used to re-arm a
    /// GNOME "Remote Login" post-auth handover, where the host disconnects instead of sending a redirect
    /// PDU and the client must reconnect with the same token).
    /// </summary>
    public static RdpServerRedirection FromToken(byte[] loadBalanceInfo, string? username, string? domain, string? password) =>
        new() { LoadBalanceInfo = loadBalanceInfo, Username = username, Domain = domain, Password = password };

    /// <summary>
    /// Scans a decrypted host→client buffer for a Server Redirection PDU and parses it. Returns null if
    /// the buffer does not contain one. The redirection is delivered as an Enhanced-Security PDU: a TPKT,
    /// X.224 Data, MCS Send-Data-Indication, then a security header with <c>SEC_REDIRECTION_PKT</c>
    /// (0x0400) and the <c>RDP_SERVER_REDIRECTION_PACKET</c>.
    /// </summary>
    public static RdpServerRedirection? TryParse(ReadOnlySpan<byte> buffer)
    {
        try { return TryParseCore(buffer); }
        catch { return null; } // never let a parse edge-case break the relay
    }

    private static RdpServerRedirection? TryParseCore(ReadOnlySpan<byte> buffer)
    {
        const ushort SEC_REDIRECTION_PKT = 0x0400;

        int i = 0;
        while (i + 4 <= buffer.Length)
        {
            if (buffer[i] != 0x03) { i++; continue; }                 // TPKT version
            int len = (buffer[i + 2] << 8) | buffer[i + 3];
            if (len < 11 || i + len > buffer.Length) { i++; continue; }
            var pdu = buffer.Slice(i, len);
            i += len;

            // TPKT(4) + X.224 Data(3) + MCS Send-Data-Indication header. The MCS header length varies;
            // locate the security header by scanning the small window after the fixed framing for the
            // SEC_REDIRECTION_PKT flag, which is the simplest robust approach across MCS length encodings.
            for (int o = 7; o + 4 <= pdu.Length && o <= 20; o++)
            {
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(pdu.Slice(o, 2));
                if ((flags & SEC_REDIRECTION_PKT) == 0) continue;
                var parsed = ParsePacket(pdu.Slice(o + 4).ToArray()); // skip flags(2) + flagsHi(2)
                if (parsed != null) return parsed;
            }
        }
        return null;
    }

    // RDP_SERVER_REDIRECTION_PACKET ([MS-RDPBCGR] 2.2.13.1), positioned just after the 4-byte security
    // header. The fixed part is Flags(2) Length(2) SessionID(4) RedirFlags(4), but in practice (Windows
    // and FreeRDP/GNOME-RD) there are 4 extra leading bytes before RedirFlags, so RedirFlags lands at
    // offset 12 and the variable fields start at 16. We don't trust the Length field; instead we try the
    // candidate header sizes and accept the one whose RedirFlags + length-prefixed fields parse cleanly.
    private static RdpServerRedirection? ParsePacket(byte[] s)
    {
        if (s.Length < 16) return null;
        // Try the observed header layout (RedirFlags @ 12) first, then the spec's 12-byte header (@ 8).
        foreach (int rfOff in new[] { 12, 8 })
        {
            var parsed = ParseFrom(s, rfOff);
            if (parsed != null) return parsed;
        }
        return null;
    }

    private static RdpServerRedirection? ParseFrom(byte[] s, int rfOff)
    {
        if (rfOff + 4 > s.Length) return null;
        uint redirFlags = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(rfOff, 4));
        // RedirFlags must have at least one known bit and no high garbage bits set.
        if (redirFlags == 0 || (redirFlags & ~0x0001FFFFu) != 0) return null;

        int o = rfOff + 4;
        byte[]? lbInfo = null;
        string? targetHost = null, username = null, domain = null, password = null;

        byte[]? ReadField()
        {
            if (o + 4 > s.Length) return null;
            uint flen = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(o, 4)); o += 4;
            if (flen > (uint)(s.Length - o) || flen > 0x10000) return null;
            var v = s.AsSpan(o, (int)flen).ToArray(); o += (int)flen;
            return v;
        }
        static string Utf16(byte[] v) => Encoding.Unicode.GetString(v).TrimEnd('\0');

        if ((redirFlags & LB_TARGET_NET_ADDRESS) != 0) { var v = ReadField(); if (v == null) return null; targetHost = Utf16(v); }
        if ((redirFlags & LB_LOAD_BALANCE_INFO) != 0) { var v = ReadField(); if (v == null) return null; lbInfo = v; }
        if ((redirFlags & LB_USERNAME) != 0) { var v = ReadField(); if (v == null) return null; username = Utf16(v); }
        if ((redirFlags & LB_DOMAIN) != 0) { var v = ReadField(); if (v == null) return null; domain = Utf16(v); }
        if ((redirFlags & LB_PASSWORD) != 0)
        {
            var v = ReadField(); if (v == null) return null;
            // GNOME Remote Desktop's "Remote Login" hands back a one-time session password as plaintext
            // UTF-16 even though it sets LB_PASSWORD_IS_PK_ENCRYPTED. Use it as-is when it decodes to a
            // sane printable string; if it's genuinely opaque (e.g. real Windows credential-cookie bytes),
            // leave Password null and the reconnect falls back to the SSO credentials.
            var pw = Utf16(v);
            if (pw.Length > 0 && pw.All(c => c >= 0x20 && c < 0x7f)) password = pw;
        }
        if ((redirFlags & LB_TARGET_FQDN) != 0) { var v = ReadField(); if (v == null) return null; targetHost ??= Utf16(v); }

        // Require at least a routing token or a target — otherwise this isn't an actionable redirect.
        if (lbInfo == null && targetHost == null) return null;

        return new RdpServerRedirection
        {
            LoadBalanceInfo = lbInfo,
            TargetHost = string.IsNullOrEmpty(targetHost) ? null : targetHost,
            Username = username,
            Domain = domain,
            Password = password,
            RedirFlags = redirFlags,
        };
    }
}
