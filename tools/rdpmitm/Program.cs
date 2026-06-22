// RDP MITM capture proxy.
//
// mstsc connects to this proxy (plain TLS, no NLA). The proxy connects to the REAL host with the
// gateway's existing X224 + TLS + CredSSP(NLA) client code, using stored credentials. It then pumps
// decrypted RDP bytes both ways and TEES every byte to dump files so we can extract exactly what a
// working client (mstsc) exchanges on the GFX channel — in particular the FRAME_ACKNOWLEDGE cadence.
//
// Usage:
//   dotnet run --project tools/rdpmitm -- <listenPort> <host> <hostPort> <user> <password> [domain] [dumpDir]
// Example (test host from memory):
//   dotnet run --project tools/rdpmitm -- 3390 10.1.250.112 3389 max Start1234
// Then in Microsoft Remote Desktop, connect to  127.0.0.1:3390  (it will prompt for credentials in
// session; any creds work for the proxy<->mstsc leg — the REAL auth to the host uses the args above).
//
// Decrypted dumps land in <dumpDir>/c2s.bin (mstsc->host) and s2c.bin (host->mstsc), plus a combined
// framed log meta.txt with per-chunk direction + timestamp + length for quick inspection.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KSol.RDPGateway.RDP;
using Microsoft.Extensions.Logging.Abstractions;

const uint PROTOCOL_SSL = 0x00000001;
const uint PROTOCOL_HYBRID = 0x00000002;
const byte TYPE_RDP_NEG_REQ = 0x01;
const byte TYPE_RDP_NEG_RSP = 0x02;

if (args.Length < 5)
{
    Console.Error.WriteLine("usage: rdpmitm <listenPort> <host> <hostPort> <user> <password> [domain] [dumpDir]");
    return 1;
}

int listenPort = int.Parse(args[0]);
string host = args[1];
int hostPort = int.Parse(args[2]);
string user = args[3];
string password = args[4];
string domain = args.Length > 5 ? args[5] : string.Empty;
string dumpDir = args.Length > 6 ? args[6] : "/tmp/rdpmitm";
Directory.CreateDirectory(dumpDir);

// Self-signed server cert presented to mstsc for the proxy<->mstsc TLS leg.
using var rsa = RSA.Create(2048);
var req = new CertificateRequest("CN=rdpmitm", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var proxyCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
// On macOS the SslStream server needs the private key attached via a PFX round-trip.
proxyCert = X509CertificateLoader.LoadPkcs12(proxyCert.Export(X509ContentType.Pfx), null,
    X509KeyStorageFlags.Exportable);

var listener = new TcpListener(IPAddress.Loopback, listenPort);
listener.Start();
Console.WriteLine($"rdpmitm: listening on 127.0.0.1:{listenPort} -> {host}:{hostPort} (user={user})");
Console.WriteLine($"rdpmitm: dumps -> {dumpDir}/c2s.bin, s2c.bin, meta.txt");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = HandleAsync(client);
}

async Task HandleAsync(TcpClient mstscTcp)
{
    var id = DateTime.Now.ToString("HHmmss");
    Console.WriteLine($"[{id}] mstsc connected from {mstscTcp.Client.RemoteEndPoint}");
    var c2s = new FileStream(Path.Combine(dumpDir, "c2s.bin"), FileMode.Create, FileAccess.Write, FileShare.Read);
    var s2c = new FileStream(Path.Combine(dumpDir, "s2c.bin"), FileMode.Create, FileAccess.Write, FileShare.Read);
    var meta = new StreamWriter(Path.Combine(dumpDir, "meta.txt")) { AutoFlush = true };
    var swStart = System.Diagnostics.Stopwatch.StartNew();

    try
    {
        // ---- proxy <-> mstsc: X224 (offer SSL only) + TLS server ----
        var mstscNet = mstscTcp.GetStream();
        uint mstscRequested = await ServerNegotiateX224Async(mstscNet);
        var mstscSsl = new SslStream(mstscNet, leaveInnerStreamOpen: false);
        await mstscSsl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = proxyCert,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        });
        Console.WriteLine($"[{id}] proxy<->mstsc TLS up");

        // ---- proxy <-> host: X224 (HYBRID) + TLS client + CredSSP ----
        var hostTcp = new TcpClient();
        await hostTcp.ConnectAsync(host, hostPort);
        var hostNet = hostTcp.GetStream();
        uint selected = await ClientNegotiateX224Async(hostNet);

        X509Certificate2? hostCert = null;
        var hostSsl = new SslStream(hostNet, leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, cert, _, _) =>
            {
                if (cert != null) hostCert = new X509Certificate2(cert);
                return true;
            });
        await hostSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        });

        if ((selected & PROTOCOL_HYBRID) != 0)
        {
            var pubKey = hostCert!.PublicKey.EncodedKeyValue.RawData;
            var credssp = new CredSspClient(hostSsl, pubKey, user, password, domain, host, null,
                NullLogger.Instance);
            var r = await credssp.AuthenticateAsync(CancellationToken.None);
            if (!r.Success) { Console.WriteLine($"[{id}] CredSSP FAILED: {r.Error}"); return; }
        }
        Console.WriteLine($"[{id}] proxy<->host TLS+NLA up (proto=0x{selected:X}); bridging + dumping");

        // ---- bridge + tee ----
        // CRITICAL: mstsc did plain SSL to us, so its MCS Connect Initial echoes serverSelectedProtocol
        // = PROTOCOL_SSL (1) in CS_CORE. But we authenticated to the host with HYBRID, so the host
        // expects that field to equal what IT confirmed (selected). A mismatch makes the host silently
        // drop the connection (mstsc then shows error 0x204). So C2S patches CS_CORE.serverSelectedProtocol
        // to `selected` on the first Connect Initial.
        var t1 = Pump(mstscSsl, hostSsl, c2s, meta, "C2S", swStart, selected, false);        // mstsc->host: patch CS_CORE.serverSelectedProtocol
        var t2 = Pump(hostSsl, mstscSsl, s2c, meta, "S2C", swStart, mstscRequested, true);   // host->mstsc: patch SC_CORE.clientRequestedProtocols
        await Task.WhenAny(t1, t2);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{id}] error: {ex.Message}");
    }
    finally
    {
        await c2s.FlushAsync(); await s2c.FlushAsync();
        c2s.Dispose(); s2c.Dispose(); meta.Dispose();
        mstscTcp.Dispose();
        Console.WriteLine($"[{id}] session closed (c2s/s2c dumps written)");
    }
}

async Task Pump(Stream from, Stream to, FileStream dump, StreamWriter meta, string dir,
    System.Diagnostics.Stopwatch sw, uint? patchProtocol, bool isServerData)
{
    var buf = new byte[32 * 1024];
    bool patched = false;
    try
    {
        while (true)
        {
            int n = await from.ReadAsync(buf);
            if (n <= 0) break;
            // First C2S: patch CS_CORE(0xC001).serverSelectedProtocol so it matches what the host
            // confirmed. First S2C: patch SC_CORE(0x0C01).clientRequestedProtocols so it matches what
            // mstsc requested on its (SSL-only) leg. Both prevent a protocol-echo mismatch drop.
            if (patchProtocol is uint val && !patched)
            {
                bool ok = isServerData
                    ? TryPatchServerCoreRequested(buf, n, val)
                    : TryPatchSelectedProtocol(buf, n, val);
                if (ok)
                {
                    patched = true;
                    lock (meta) { meta.WriteLine($"{sw.ElapsedMilliseconds,8} PATCH {(isServerData ? "clientRequestedProtocols" : "serverSelectedProtocol")}->0x{val:X}"); }
                }
            }
            await dump.WriteAsync(buf.AsMemory(0, n));
            await dump.FlushAsync();
            lock (meta) { meta.WriteLine($"{sw.ElapsedMilliseconds,8} {dir} {n}"); }
            await to.WriteAsync(buf.AsMemory(0, n));
            await to.FlushAsync();
        }
    }
    catch { /* peer closed */ }
}

// Find the GCC CS_CORE block (type 0xC001) in an MCS Connect Initial and rewrite its
// serverSelectedProtocol (the 4-byte field after connectionType(1)+pad(1), which itself follows
// highColorDepth(2)+supportedColorDepths(2)+earlyCapabilityFlags(2)+clientDigProductId(64), which
// follow the 128-byte fixed CS_CORE prefix). We locate CS_CORE by its 0xC001 type + plausible length,
// then compute serverSelectedProtocol's offset from the block start.
static bool TryPatchSelectedProtocol(byte[] buf, int len, uint selected)
{
    for (int i = 0; i + 4 <= len; i++)
    {
        ushort type = (ushort)(buf[i] | (buf[i + 1] << 8));
        if (type != 0xC001) continue;
        ushort blkLen = (ushort)(buf[i + 2] | (buf[i + 3] << 8));
        if (blkLen < 200 || i + blkLen > len) continue;
        // Within CS_CORE data (starts at i+4): fixed prefix is 136 bytes (version(4) desktopWidth(2)
        // desktopHeight(2) colorDepth(2) SASSequence(2) keyboardLayout(4) clientBuild(4) clientName(32)
        // keyboardType(4) keyboardSubType(4) keyboardFunctionKey(4) imeFileName(64) postBeta2ColorDepth(2)
        // clientProductId(2) serialNumber(4) = 136). Then:
        //   highColorDepth(2) supportedColorDepths(2) earlyCapabilityFlags(2) clientDigProductId(64)
        //   connectionType(1) pad(1) serverSelectedProtocol(4)
        // (Verified against a real mstsc Connect Initial: CS_CORE@132 -> serverSelectedProtocol@344.)
        int sspOff = i + 4 + 136 + 2 + 2 + 2 + 64 + 1 + 1;
        if (sspOff + 4 > len) continue;
        // Only patch if it currently reads a known protocol value (1/2/3) to avoid corrupting on a
        // mis-located block.
        uint cur = (uint)(buf[sspOff] | (buf[sspOff + 1] << 8) | (buf[sspOff + 2] << 16) | (buf[sspOff + 3] << 24));
        if (cur > 0x0f) continue;
        buf[sspOff] = (byte)(selected & 0xff);
        buf[sspOff + 1] = (byte)((selected >> 8) & 0xff);
        buf[sspOff + 2] = (byte)((selected >> 16) & 0xff);
        buf[sspOff + 3] = (byte)((selected >> 24) & 0xff);
        Console.WriteLine($"  patched serverSelectedProtocol 0x{cur:X}->0x{selected:X} at packet offset {sspOff}");
        return true;
    }
    return false;
}

// Find the GCC SC_CORE block (type 0x0C01) in an MCS Connect Response and rewrite its
// clientRequestedProtocols echo (the 4-byte field right after version(4)). The host echoes the
// protocols WE requested (HYBLID|SSL = 3); mstsc requested only SSL on its leg, so without this patch
// mstsc sees a mismatch and aborts (error 0x609). Verified: SC_CORE@68 -> field@72 in a real response.
static bool TryPatchServerCoreRequested(byte[] buf, int len, uint requested)
{
    for (int i = 0; i + 8 <= len; i++)
    {
        ushort type = (ushort)(buf[i] | (buf[i + 1] << 8));
        if (type != 0x0C01) continue;
        ushort blkLen = (ushort)(buf[i + 2] | (buf[i + 3] << 8));
        if (blkLen < 12 || i + blkLen > len) continue;
        int off = i + 4 + 4; // skip block header(4) + version(4)
        if (off + 4 > len) continue;
        uint cur = (uint)(buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24));
        if (cur > 0x0f) continue; // only a protocol-flags value
        buf[off] = (byte)(requested & 0xff);
        buf[off + 1] = (byte)((requested >> 8) & 0xff);
        buf[off + 2] = (byte)((requested >> 16) & 0xff);
        buf[off + 3] = (byte)((requested >> 24) & 0xff);
        Console.WriteLine($"  patched clientRequestedProtocols 0x{cur:X}->0x{requested:X} at packet offset {off}");
        return true;
    }
    return false;
}

// Proxy acting as RDP server toward mstsc: read its X224 CR, reply CC selecting PLAIN SSL (no NLA).
// Returns mstsc's requestedProtocols (from its RDP_NEG_REQ) so we can later patch the host's SC_CORE
// clientRequestedProtocols echo to match — otherwise mstsc rejects the Connect Response (error 0x609).
async Task<uint> ServerNegotiateX224Async(NetworkStream net)
{
    var tpkt = await ReadExactAsync(net, 4);
    int len = (tpkt[2] << 8) | tpkt[3];
    var body = await ReadExactAsync(net, len - 4); // X224 CR + optional nego req

    // body: LI(1) CR(1)=0xE0 dst(2) src(2) class(1) [RDP_NEG_REQ: type(1)=1 flags(1) len(2)=8 protocols(4)]
    uint requested = PROTOCOL_SSL;
    if (body.Length >= 15 && body[7] == TYPE_RDP_NEG_REQ)
        requested = BitConverter.ToUInt32(body, 11);

    // Reply: TPKT + X224 CC (0xD0) + RDP_NEG_RSP selecting PROTOCOL_SSL.
    var negRsp = new byte[8];
    negRsp[0] = TYPE_RDP_NEG_RSP; negRsp[1] = 0; negRsp[2] = 8; negRsp[3] = 0;
    BitConverter.GetBytes(PROTOCOL_SSL).CopyTo(negRsp, 4);
    var x224 = new byte[7 + negRsp.Length];
    x224[0] = (byte)(x224.Length - 1);
    x224[1] = 0xD0; // CC
    negRsp.CopyTo(x224, 7);
    int total = 4 + x224.Length;
    var pdu = new byte[total];
    pdu[0] = 3; pdu[1] = 0; pdu[2] = (byte)(total >> 8); pdu[3] = (byte)(total & 0xff);
    x224.CopyTo(pdu, 4);
    await net.WriteAsync(pdu);
    await net.FlushAsync();
    return requested;
}

// Proxy acting as RDP client toward the host: send CR offering HYBRID|SSL, read CC's selected protocol.
async Task<uint> ClientNegotiateX224Async(NetworkStream net)
{
    var neg = new byte[8];
    neg[0] = TYPE_RDP_NEG_REQ; neg[1] = 0; neg[2] = 8; neg[3] = 0;
    BitConverter.GetBytes(PROTOCOL_HYBRID | PROTOCOL_SSL).CopyTo(neg, 4);
    var x224 = new byte[7 + neg.Length];
    x224[0] = (byte)(x224.Length - 1);
    x224[1] = 0xE0; // CR
    neg.CopyTo(x224, 7);
    int total = 4 + x224.Length;
    var pdu = new byte[total];
    pdu[0] = 3; pdu[1] = 0; pdu[2] = (byte)(total >> 8); pdu[3] = (byte)(total & 0xff);
    x224.CopyTo(pdu, 4);
    await net.WriteAsync(pdu);
    await net.FlushAsync();

    var tpkt = await ReadExactAsync(net, 4);
    int respLen = (tpkt[2] << 8) | tpkt[3];
    var body = await ReadExactAsync(net, respLen - 4);
    if (body.Length >= 15 && body[7] == TYPE_RDP_NEG_RSP)
        return BitConverter.ToUInt32(body, 11);
    return PROTOCOL_SSL;
}

async Task<byte[]> ReadExactAsync(Stream s, int count)
{
    var buf = new byte[count];
    int read = 0;
    while (read < count)
    {
        int n = await s.ReadAsync(buf.AsMemory(read, count - read));
        if (n <= 0) throw new IOException("eof");
        read += n;
    }
    return buf;
}
