// RdpFastPath — fastpath PDUs ([MS-RDPBCGR] 2.2.8 input, 2.2.9 output).
//
// The fastpath header byte's low 2 bits are the action: 0 = FASTPATH_OUTPUT (server->client updates),
// other values appear on the input PDU header (client->server). We disambiguate by direction.
//
// Output: header(1) [+ optional fipsInformation/dataSignature when encrypted — not present here since
// TLS terminates encryption] length(PER 1-2B), then a sequence of update PDUs, each:
//   updateHeader(1): updateCode(4 low) | fragmentation(2) | compression(2)
//   [compressionFlags(1) if compressed]   <-- note: [MS-RDPBCGR] uses 1 byte here; the JS reads 2,
//   size(2)                                    we follow the spec's 1-byte compressionFlags + size(2).
//   data(size)
// We enumerate update types (bitmap, palette, pointer*, surfcmds, synchronize, orders) and decode the
// bitmap update rectangles structurally; codec/order bitstreams stay opaque (echoed).
//
// Input: header(1): action(2) | numEvents(4) | flags(2); [+ length already consumed by framing], then
// numEvents events, each eventHeader(1): eventFlags(5) | eventCode(3). We decode mouse/scancode/sync/
// unicode events structurally.
//
// All header bytes are echoed via Node.Raw, so re-encoding reproduces the original exactly.

using System.Text.Json.Nodes;

namespace KSol.RDPGateway.RDP;

internal static class RdpFastPath
{
    private const int FP_UPDATETYPE_ORDERS = 0x0;
    private const int FP_UPDATETYPE_BITMAP = 0x1;
    private const int FP_UPDATETYPE_PALETTE = 0x2;
    private const int FP_UPDATETYPE_SYNCHRONIZE = 0x3;
    private const int FP_UPDATETYPE_SURFCMDS = 0x4;
    private const int FP_UPDATETYPE_PTR_NULL = 0x5;
    private const int FP_UPDATETYPE_PTR_DEFAULT = 0x6;
    private const int FP_UPDATETYPE_PTR_POSITION = 0x8;
    private const int FP_UPDATETYPE_PTR_COLOR = 0x9;
    private const int FP_UPDATETYPE_PTR_CACHED = 0xa;
    private const int FP_UPDATETYPE_PTR_NEW = 0xb;
    private const int FP_UPDATETYPE_LARGE_POINTER = 0xc;

    private const int FP_OUTPUT_COMPRESSION_USED = 0x2;

    public static Node Decode(RdpSession s, RdpDir dir, byte[] unit)
    {
        var c = new Cur(unit);
        byte fpHeader = c.U8();
        int action = fpHeader & 0x3;
        // PER-style length (1 or 2 bytes).
        int lenByte = c.U8();
        int headerLen;
        if ((lenByte & 0x80) != 0) { c.U8(); headerLen = 3; } else { headerLen = 2; }

        // Header (fpHeader + length) echoed verbatim.
        var root = new Node(dir == RdpDir.ClientToServer ? "FastPath.Input" : "FastPath.Output");
        root.Field("header", "0x" + fpHeader.ToString("X2"));
        root.Raw(unit.AsSpan(0, headerLen));

        var body = unit.AsSpan(headerLen);
        if (dir == RdpDir.ClientToServer) DecodeInput(s, root, fpHeader, body);
        else DecodeOutput(s, root, body);
        return root;
    }

    // ---- output: sequence of update PDUs ----
    private static void DecodeOutput(RdpSession s, Node root, ReadOnlySpan<byte> body)
    {
        var c = new Cur(body);
        int idx = 0;
        while (c.Remaining >= 3)
        {
            int hdrStart = c.O;
            byte updateHeader = c.U8();
            int updateCode = updateHeader & 0xf;
            int fragmentation = (updateHeader & 0x30) >> 4;
            int compression = (updateHeader & 0xc0) >> 6;
            int compFlags = -1;
            if (compression == FP_OUTPUT_COMPRESSION_USED) compFlags = c.U8();
            int size = c.U16le();
            if (size < 0 || size > c.Remaining) break;
            var data = c.Take(size);

            var u = new Node("update." + UpdateName(updateCode));
            u.Raw(body.Slice(hdrStart, c.O - hdrStart)); // updateHeader + [compFlags] + size + data
            u.Field("updateCode", "0x" + updateCode.ToString("X") + " " + UpdateName(updateCode));
            if (fragmentation != 0) u.Field("fragmentation", FragName(fragmentation));
            if (compFlags >= 0) u.Field("compressionFlags", "0x" + compFlags.ToString("X2"));
            u.Field("size", size);

            if (compFlags < 0) // only structurally decode uncompressed, unfragmented bodies
            {
                if (fragmentation is 0 or 1) // SINGLE or LAST (complete)
                {
                    switch (updateCode)
                    {
                        case FP_UPDATETYPE_BITMAP: DecodeBitmapUpdate(u, data); break;
                        case FP_UPDATETYPE_PTR_POSITION: DecodePtrPosition(s, u, data); break;
                        case FP_UPDATETYPE_PTR_CACHED: DecodePtrCached(s, u, data); break;
                        case FP_UPDATETYPE_PTR_NULL: s.Pointer.OnPtrNull(); break;
                        case FP_UPDATETYPE_PTR_DEFAULT: s.Pointer.OnPtrDefault(); break;
                        case FP_UPDATETYPE_PTR_COLOR: s.Pointer.OnPtrShape(data, hasXorBpp: false); break;
                        case FP_UPDATETYPE_PTR_NEW: s.Pointer.OnPtrShape(data, hasXorBpp: true); break;
                        case FP_UPDATETYPE_SURFCMDS: DecodeSurfCmds(u, data); break;
                        case FP_UPDATETYPE_SYNCHRONIZE: break; // empty body
                        default: if (data.Length > 0) u.FieldHex("data", data); break;
                    }
                }
                else if (data.Length > 0) u.FieldHex("dataFragment", data);
            }
            else u.FieldHex("compressedData", data);

            root.Child(u);
            idx++;
        }
        if (c.Remaining > 0) { var tail = new Node("update.tail"); tail.FieldHex("bytes", c.Rest()); tail.Raw(body.Slice(c.O > body.Length ? body.Length : c.O)); /* unreachable normally */ }
    }

    private static void DecodeBitmapUpdate(Node u, ReadOnlySpan<byte> data)
    {
        var c = new Cur(data);
        c.U16le(); // updateType (BITMAP)
        int numRects = c.U16le();
        u.Field("numberRectangles", numRects);
        var arr = new JsonArray();
        for (int i = 0; i < numRects && c.Remaining >= 18; i++)
        {
            int left = c.U16le(), top = c.U16le(), right = c.U16le(), bottom = c.U16le();
            int w = c.U16le(), h = c.U16le(), bpp = c.U16le(), flags = c.U16le(), bmLen = c.U16le();
            bool compressed = (flags & 0x0001) != 0;
            bool noHdr = (flags & 0x0400) != 0;
            int dataLen = bmLen;
            if (compressed && !noHdr) { c.O += 8; dataLen -= 8; } // skip TS_CD_HEADER
            if (dataLen < 0 || dataLen > c.Remaining) break;
            c.O += dataLen; // bitmap data stream (opaque)
            arr.Add((JsonNode?)$"[{left},{top},{right},{bottom}] {w}x{h} {bpp}bpp {(compressed ? "RLE" : "raw")} {bmLen}B");
        }
        u.Field("rectangles", arr);
    }

    private static void DecodePtrPosition(RdpSession s, Node u, ReadOnlySpan<byte> data)
    {
        var c = new Cur(data);
        if (c.Remaining >= 4)
        {
            int x = c.U16le(), y = c.U16le();
            u.Field("x", x).Field("y", y);
            s.Pointer.OnPtrPosition(x, y);
        }
    }
    private static void DecodePtrCached(RdpSession s, Node u, ReadOnlySpan<byte> data)
    {
        var c = new Cur(data);
        if (c.Remaining >= 2)
        {
            int idx = c.U16le();
            u.Field("cacheIndex", idx);
            s.Pointer.OnPtrCached(idx);
        }
    }

    // SURFCMDS: a sequence of TS_SURFCMD; cmdType(2) then a body. Most relevant is SET_SURFACE_BITS (0x01)
    // and STREAM_SURFACE_BITS (0x06). We enumerate the cmd types; bitstreams stay opaque.
    private static void DecodeSurfCmds(Node u, ReadOnlySpan<byte> data)
    {
        var c = new Cur(data);
        var arr = new JsonArray();
        while (c.Remaining >= 2)
        {
            int cmdType = c.U16le();
            string name = cmdType switch { 0x01 => "SET_SURFACE_BITS", 0x06 => "STREAM_SURFACE_BITS", 0x04 => "FRAME_MARKER", _ => "0x" + cmdType.ToString("X4") };
            if (cmdType == 0x04) { if (c.Remaining >= 6) { int act = c.U16le(); uint fid = c.U32le(); arr.Add((JsonNode?)$"{name}({(act == 0 ? "BEGIN" : "END")} frame {fid})"); } else break; }
            else
            {
                // SET/STREAM_SURFACE_BITS: destLeft/Top/Right/Bottom(2 each) + TS_BITMAP_DATA_EX. The ex
                // header has bpp(1) flags(1) reserved(1) codecId(1) width(2) height(2) bitmapDataLength(4)
                // then the bitstream. We read enough to advance correctly.
                if (c.Remaining < 8 + 12) { arr.Add((JsonNode?)$"{name}(truncated)"); break; }
                int dl = c.U16le(), dt = c.U16le(), dr = c.U16le(), db = c.U16le();
                byte bpp = c.U8(); byte exFlags = c.U8(); c.U8(); byte codecId = c.U8();
                int w = c.U16le(), h = c.U16le(); long bdLen = c.U32le();
                // exFlags bit 0 = EX_COMPRESSED_BITMAP_HEADER_PRESENT -> 24-byte header precedes data.
                if ((exFlags & 0x01) != 0) { if (c.Remaining >= 24) c.O += 24; }
                if (bdLen < 0 || bdLen > c.Remaining) { arr.Add((JsonNode?)$"{name}([{dl},{dt},{dr},{db}] codec=0x{codecId:X2} {w}x{h} {bdLen}B overflow)"); break; }
                c.O += (int)bdLen;
                arr.Add((JsonNode?)$"{name}([{dl},{dt},{dr},{db}] codec=0x{codecId:X2} {w}x{h} {bdLen}B)");
            }
        }
        u.Field("commands", arr);
    }

    private static string UpdateName(int code) => code switch
    {
        FP_UPDATETYPE_ORDERS => "ORDERS", FP_UPDATETYPE_BITMAP => "BITMAP", FP_UPDATETYPE_PALETTE => "PALETTE",
        FP_UPDATETYPE_SYNCHRONIZE => "SYNCHRONIZE", FP_UPDATETYPE_SURFCMDS => "SURFCMDS",
        FP_UPDATETYPE_PTR_NULL => "PTR_NULL", FP_UPDATETYPE_PTR_DEFAULT => "PTR_DEFAULT",
        FP_UPDATETYPE_PTR_POSITION => "PTR_POSITION", FP_UPDATETYPE_PTR_COLOR => "PTR_COLOR",
        FP_UPDATETYPE_PTR_CACHED => "PTR_CACHED", FP_UPDATETYPE_PTR_NEW => "PTR_NEW",
        FP_UPDATETYPE_LARGE_POINTER => "LARGE_POINTER", _ => "0x" + code.ToString("X"),
    };
    private static string FragName(int f) => f switch { 0 => "SINGLE", 1 => "LAST", 2 => "FIRST", 3 => "NEXT", _ => f.ToString() };

    // ---- input: numEvents events ----
    private static void DecodeInput(RdpSession s, Node root, byte fpHeader, ReadOnlySpan<byte> body)
    {
        int numEvents = (fpHeader >> 2) & 0xf;
        root.Field("numEvents", numEvents == 0 ? "(in body)" : (JsonNode?)numEvents);
        var c = new Cur(body);
        // numEvents == 0 means the count is a 1-byte field at the start of the body.
        if (numEvents == 0 && c.Remaining >= 1)
        {
            int hdrStart = c.O; numEvents = c.U8();
            var nn = new Node("input.numEvents"); nn.Field("numEvents", numEvents); nn.Raw(body.Slice(hdrStart, 1));
            root.Child(nn);
        }

        for (int i = 0; i < numEvents && c.Remaining >= 1; i++)
        {
            int evStart = c.O;
            byte eventHeader = c.U8();
            int eventFlags = eventHeader & 0x1f;
            int eventCode = (eventHeader >> 5) & 0x7;

            var ev = new Node("input." + InputName(eventCode));
            switch (eventCode)
            {
                case 0x0: // SCANCODE: flags(5) then keyCode(2)
                    if (c.Remaining >= 2)
                    {
                        int code = c.U16le();
                        ev.Field("scancode", "0x" + code.ToString("X")).Field("release", (eventFlags & 0x01) != 0);
                    }
                    break;
                case 0x1: // MOUSE: pointerFlags(2) x(2) y(2)
                case 0x2: // MOUSEX
                    if (c.Remaining >= 6)
                    {
                        int pf = c.U16le(); int x = c.U16le(); int y = c.U16le();
                        ev.Field("pointerFlags", "0x" + pf.ToString("X")).Field("x", x).Field("y", y)
                          .Field("decode", MouseFlags(pf));
                        s.Pointer.OnMouseInput(pf, x, y);
                    }
                    break;
                case 0x3: // SYNC (toggle keys) — eventFlags carry the toggle state, no extra bytes
                    ev.Field("toggleFlags", "0x" + eventFlags.ToString("X"));
                    break;
                case 0x4: // UNICODE: unicodeCode(2)
                    if (c.Remaining >= 2) ev.Field("unicode", "0x" + c.U16le().ToString("X"));
                    break;
                case 0x6: // QOE_TIMESTAMP: timestamp(4)
                    if (c.Remaining >= 4) ev.Field("timestamp", c.U32le());
                    break;
            }
            ev.Raw(body.Slice(evStart, c.O - evStart));
            root.Child(ev);
        }
        if (c.Remaining > 0) { var t = new Node("input.tail"); t.FieldHex("bytes", c.Rest()); t.Raw(body.Slice(body.Length - 0)); }
    }

    private static string InputName(int code) => code switch
    { 0 => "SCANCODE", 1 => "MOUSE", 2 => "MOUSEX", 3 => "SYNC", 4 => "UNICODE", 6 => "QOE_TIMESTAMP", _ => "0x" + code.ToString("X") };

    private static string MouseFlags(int pf)
    {
        var parts = new List<string>();
        if ((pf & 0x8000) != 0) parts.Add("DOWN");
        if ((pf & 0x1000) != 0) parts.Add("BTN1");
        if ((pf & 0x2000) != 0) parts.Add("BTN2");
        if ((pf & 0x4000) != 0) parts.Add("BTN3");
        if ((pf & 0x0800) != 0) parts.Add("MOVE");
        if ((pf & 0x0200) != 0) parts.Add("WHEEL");
        if ((pf & 0x0400) != 0) parts.Add("HWHEEL");
        return parts.Count == 0 ? "-" : string.Join("|", parts);
    }
}
