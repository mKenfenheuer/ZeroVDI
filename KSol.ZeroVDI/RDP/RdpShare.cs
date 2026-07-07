// RdpShare — slow-path share-layer PDUs carried on the global I/O channel (after the MCS SendData
// header). Covers: the licensing PDU, the Demand Active / Confirm Active capability exchange, the
// connection-finalization data PDUs (synchronize / control / font list / font map), the
// set-error-info PDU, and Deactivate All.
//
// A share-layer slow-path payload may begin with a 4-byte Security Header (flags/flagsHi). We detect it
// the same way the JS client does: peek the ShareControlHeader's pduType nibble; if it isn't a known
// type, a security header precedes it. The Client Info PDU and licensing PDU always carry the security
// header (SEC_INFO_PKT / SEC_LICENSE_PKT). All header bytes are echoed via Node.Raw so the tree
// re-serializes byte-identically; we only decode fields for the log.

using System.Text.Json.Nodes;

namespace KSol.ZeroVDI.RDP;

internal static class RdpShare
{
    private const int PDUTYPE_DEMANDACTIVE = 0x11;
    private const int PDUTYPE_CONFIRMACTIVE = 0x13;
    private const int PDUTYPE_DEACTIVATEALL = 0x16;
    private const int PDUTYPE_DATAPDU = 0x17;

    private const int PDUTYPE2_UPDATE = 0x02;
    private const int PDUTYPE2_CONTROL = 0x14;
    private const int PDUTYPE2_POINTER = 0x1B;
    private const int PDUTYPE2_INPUT = 0x1C;
    private const int PDUTYPE2_SYNCHRONIZE = 0x1F;
    private const int PDUTYPE2_REFRESH_RECT = 0x21;
    private const int PDUTYPE2_SUPPRESS_OUTPUT = 0x23;
    private const int PDUTYPE2_FONTLIST = 0x27;
    private const int PDUTYPE2_FONTMAP = 0x28;
    private const int PDUTYPE2_SET_ERROR_INFO_PDU = 0x2f;

    public static void Decode(RdpSession s, RdpDir dir, Node parent, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) { Raw(parent, "share.empty", payload); return; }

        // Peek for a leading security header. SEC flags low bits: SEC_INFO_PKT 0x40, SEC_LICENSE_PKT 0x80,
        // SEC_ENCRYPT 0x08, etc. If the share-control pduType at +2 isn't known, assume a 4-byte sec hdr.
        var c = new Cur(payload);
        int startO = c.O;
        int totalLength = c.U16le();
        int pduTypeRaw = c.U16le();
        int pduType = pduTypeRaw & 0xf;
        bool hasSec = false;
        if (!IsKnownPdu(pduType) && payload.Length >= 8)
        {
            hasSec = true;
            c = new Cur(payload);
            ushort secFlags = c.U16le();
            // Licensing and Client Info ride here. Branch on the security flag.
            if ((secFlags & 0x0080) != 0) { DecodeLicensing(parent, payload); return; }
            if ((secFlags & 0x0040) != 0) { DecodeClientInfo(parent, payload); return; }
            // Other security-headered share PDU: skip the 4-byte header, re-read the share control header.
            c.U16le(); // flagsHi
            startO = c.O;
            totalLength = c.U16le();
            pduTypeRaw = c.U16le();
            pduType = pduTypeRaw & 0xf;
        }

        if (pduType == (PDUTYPE_DEMANDACTIVE & 0xf)) { DecodeActive(parent, payload, hasSec, isDemand: true); return; }
        if (pduType == (PDUTYPE_CONFIRMACTIVE & 0xf)) { DecodeActive(parent, payload, hasSec, isDemand: false); return; }
        if (pduType == (PDUTYPE_DEACTIVATEALL & 0xf))
        {
            var n = new Node("RDP.DeactivateAll"); n.Field("totalLength", totalLength); n.Raw(payload);
            parent.Child(n); return;
        }
        if (pduType == (PDUTYPE_DATAPDU & 0xf)) { DecodeDataPdu(s, parent, payload, hasSec); return; }

        Raw(parent, "share.unknown", payload, ("pduType", "0x" + pduTypeRaw.ToString("X4")));
    }

    private static bool IsKnownPdu(int t) =>
        t == (PDUTYPE_DEMANDACTIVE & 0xf) || t == (PDUTYPE_CONFIRMACTIVE & 0xf)
        || t == (PDUTYPE_DEACTIVATEALL & 0xf) || t == (PDUTYPE_DATAPDU & 0xf);

    private static void DecodeLicensing(Node parent, ReadOnlySpan<byte> payload)
    {
        var n = new Node("RDP.Licensing");
        n.Raw(payload);
        var c = new Cur(payload);
        n.Field("secFlags", "0x" + c.U16le().ToString("X4")); c.U16le();
        if (c.Remaining >= 1)
        {
            byte msgType = c.U8();
            n.Field("msgType", "0x" + msgType.ToString("X2") + " " + msgType switch
            {
                0x01 => "LICENSE_REQUEST", 0x02 => "PLATFORM_CHALLENGE", 0x03 => "NEW_LICENSE",
                0x04 => "UPGRADE_LICENSE", 0x12 => "LICENSE_INFO", 0x13 => "NEW_LICENSE_REQUEST",
                0x15 => "PLATFORM_CHALLENGE_RESPONSE", 0xFF => "ERROR_ALERT", _ => "?",
            });
        }
        parent.Child(n);
    }

    // Client Info PDU (auto-logon credentials). We decode the structural shape (lengths/flags) but mask
    // the credential bytes in the log (they still round-trip via Raw).
    private static void DecodeClientInfo(Node parent, ReadOnlySpan<byte> payload)
    {
        var n = new Node("RDP.ClientInfo");
        n.Raw(payload);
        var c = new Cur(payload);
        n.Field("secFlags", "0x" + c.U16le().ToString("X4")); c.U16le();
        if (c.Remaining >= 18)
        {
            n.Field("codePage", c.U32le());
            n.Field("flags", "0x" + c.U32le().ToString("X8"));
            int cbDomain = c.U16le(), cbUser = c.U16le(), cbPassword = c.U16le();
            int cbShell = c.U16le(), cbWorkDir = c.U16le();
            n.Field("cbDomain", cbDomain).Field("cbUserName", cbUser)
             .Field("cbPassword", cbPassword == 0 ? "0" : "<" + cbPassword + "B masked>")
             .Field("cbAlternateShell", cbShell).Field("cbWorkingDir", cbWorkDir);
        }
        parent.Child(n);
    }

    private static void DecodeActive(Node parent, ReadOnlySpan<byte> payload, bool hasSec, bool isDemand)
    {
        var n = new Node(isDemand ? "RDP.DemandActive" : "RDP.ConfirmActive");
        n.Raw(payload);
        var c = new Cur(payload);
        if (hasSec) { c.U16le(); c.U16le(); }
        n.Field("totalLength", c.U16le());
        n.Field("pduType", "0x" + c.U16le().ToString("X4"));
        n.Field("pduSource", c.U16le());
        if (c.Remaining >= 4) n.Field("shareId", c.U32le());
        // capability sets follow (lengthSourceDescriptor + lengthCombinedCapabilities + numberCapabilities…).
        // We enumerate capset {type,len} for the log; bodies stay opaque (echoed by Raw).
        try
        {
            if (isDemand)
            {
                int lenSrc = c.U16le(); int lenCaps = c.U16le(); c.O += lenSrc; // sourceDescriptor
                int numCaps = c.U16le(); c.U16le(); // pad
                n.Field("numberCapabilities", numCaps);
                n.Field("capabilitySets", EnumCapsets(ref c, numCaps));
            }
            else
            {
                c.U16le(); /*originatorId*/ int lenSrc = c.U16le(); int lenCaps = c.U16le(); c.O += lenSrc;
                int numCaps = c.U16le(); c.U16le();
                n.Field("numberCapabilities", numCaps);
                n.Field("capabilitySets", EnumCapsets(ref c, numCaps));
            }
        }
        catch { /* best-effort capset enumeration */ }
        parent.Child(n);
    }

    private static JsonArray EnumCapsets(ref Cur c, int numCaps)
    {
        var arr = new JsonArray();
        for (int i = 0; i < numCaps && c.Remaining >= 4; i++)
        {
            int type = c.U16le(); int len = c.U16le();
            arr.Add((JsonNode?)(CapName(type) + ":" + len));
            int skip = len - 4;
            if (skip < 0 || skip > c.Remaining) break;
            c.O += skip;
        }
        return arr;
    }

    private static string CapName(int t) => t switch
    {
        0x01 => "GENERAL", 0x02 => "BITMAP", 0x03 => "ORDER", 0x04 => "BITMAPCACHE_REV1", 0x05 => "CONTROL",
        0x07 => "WINDOWACTIVATION", 0x08 => "POINTER", 0x09 => "SHARE", 0x0A => "COLORCACHE",
        0x0C => "SOUND", 0x0D => "INPUT", 0x0E => "FONT", 0x0F => "BRUSH", 0x10 => "GLYPHCACHE",
        0x11 => "OFFSCREEN", 0x13 => "BITMAPCACHE_REV2", 0x14 => "VIRTUALCHANNEL", 0x18 => "WINDOW",
        0x1A => "MULTIFRAGMENTUPDATE", 0x1B => "LARGE_POINTER", 0x1C => "SURFACE_COMMANDS",
        0x1D => "BITMAP_CODECS", 0x1E => "FRAME_ACKNOWLEDGE",
        _ => "0x" + t.ToString("X2"),
    };

    private static void DecodeDataPdu(RdpSession s, Node parent, ReadOnlySpan<byte> payload, bool hasSec)
    {
        var c = new Cur(payload);
        if (hasSec) { c.U16le(); c.U16le(); }
        int totalLength = c.U16le();
        int pduTypeRaw = c.U16le();
        int pduSource = c.U16le();
        uint shareId = c.U32le();
        c.U8();   // padding
        byte streamId = c.U8();
        int uncompressedLength = c.U16le();
        byte pduType2 = c.U8();
        byte compressedType = c.U8();
        int compressedLength = c.U16le();

        var n = new Node("RDP.DataPdu");
        n.Raw(payload);
        n.Field("pduType2", "0x" + pduType2.ToString("X2") + " " + DataPduName(pduType2));
        n.Field("shareId", shareId).Field("streamId", streamId).Field("uncompressedLength", uncompressedLength);
        if (compressedType != 0) n.Field("compressedType", "0x" + compressedType.ToString("X2"));

        // A few small bodies decoded inline.
        switch (pduType2)
        {
            case PDUTYPE2_CONTROL:
                if (c.Remaining >= 2) n.Field("action", ControlAction(c.U16le()));
                break;
            case PDUTYPE2_SET_ERROR_INFO_PDU:
                if (c.Remaining >= 4) n.Field("errorInfo", "0x" + c.U32le().ToString("X"));
                break;
            case PDUTYPE2_SYNCHRONIZE:
                if (c.Remaining >= 4) { n.Field("messageType", c.U16le()); n.Field("targetUser", c.U16le()); }
                break;
        }
        parent.Child(n);
    }

    private static string ControlAction(int a) => a switch
    { 1 => "REQUEST_CONTROL", 2 => "GRANTED_CONTROL", 3 => "DETACH", 4 => "COOPERATE", _ => a.ToString() };

    private static string DataPduName(int t) => t switch
    {
        PDUTYPE2_UPDATE => "UPDATE", PDUTYPE2_CONTROL => "CONTROL", PDUTYPE2_POINTER => "POINTER",
        PDUTYPE2_INPUT => "INPUT", PDUTYPE2_SYNCHRONIZE => "SYNCHRONIZE", PDUTYPE2_REFRESH_RECT => "REFRESH_RECT",
        PDUTYPE2_SUPPRESS_OUTPUT => "SUPPRESS_OUTPUT", PDUTYPE2_FONTLIST => "FONTLIST", PDUTYPE2_FONTMAP => "FONTMAP",
        PDUTYPE2_SET_ERROR_INFO_PDU => "SET_ERROR_INFO", _ => "0x" + t.ToString("X2"),
    };

    private static void Raw(Node parent, string name, ReadOnlySpan<byte> b, params (string, string)[] extra)
    {
        var n = new Node(name);
        foreach (var (k, v) in extra) n.Field(k, v);
        n.FieldHex("bytes", b);
        n.Raw(b);
        parent.Child(n);
    }
}
