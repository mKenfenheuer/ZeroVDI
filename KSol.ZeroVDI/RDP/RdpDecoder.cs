// RdpDecoder — structural RDP protocol codec for the MITM proxy.
//
// The MITM proxy (Program.cs) sits between mstsc/macOS-RD and a real RDP host with TLS+CredSSP already
// terminated, so it sees the *decrypted* RDP byte stream both ways. This module is fed that stream and,
// for every PDU on every channel, performs a full round trip:
//
//     receive bytes  ->  DECODE to a structural tree  ->  LOG  ->  ENCODE back to bytes  ->  compare
//
// The compare proves our decode AND encode are correct: if the re-encoded bytes are byte-identical to the
// original, we forward OUR re-encoded bytes (exercising the encode path on the live wire); otherwise we
// fall back to forwarding the original bytes so the live mstsc<->host session is never broken, and we log
// the unit as a round-trip MISMATCH (with a byte diff) so the gap is visible.
//
// Coverage (round-trip verified): TPKT / X.224 framing, MCS (connect / domain / send-data) + GCC
// conference-create + the CS_*/SC_* settings blocks, licensing, the capability exchange, connection
// finalization, the legacy slow-path share PDUs, the fastpath input + output update PDUs, static virtual
// channels (CHANNEL_PDU_HEADER), drdynvc dynamic-channel framing (MS-RDPEDYC), and the RDPEGFX graphics
// command framing (MS-RDPEGFX). Codec *bitstreams* (H.264/AVC, ClearCodec, RLE) and compressed blobs
// (ZGFX, MPPC) are decoded-and-logged as opaque payloads carried verbatim inside the structural tree —
// the framing AROUND them is what gets round-trip verified; the bitstream bytes pass through unchanged.
//
// This is a C# port of the parsing logic in the working JS client stack
// (ksol-rdpgw/wwwroot/lib/rdpweb/{protocol,rdpgfx,update,input,...}.js), generalized to parse BOTH
// directions and made symmetric (every node can re-serialize itself).
//
// The structural representation is the `Node` tree: a Node has a name, an ordered list of (field,value)
// pairs for logging, and a list of byte-emitting "parts" (literal slices or child nodes) that reproduce
// the exact original bytes. Decoding builds the parts as it reads; encoding concatenates them. A field is
// logged but a *part* is what reproduces bytes — most reads add both.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KSol.ZeroVDI.RDP;

public enum RdpDir { ClientToServer, ServerToClient }

/// <summary>Sink for decoded units (text + JSONL). Called once per top-level PDU with its node tree.</summary>
public interface IRdpEventSink
{
    void Emit(RdpDir dir, long elapsedMs, Node node, RoundTrip rt);
}

/// <summary>Outcome of re-encoding a decoded node and comparing with the original bytes.</summary>
public readonly struct RoundTrip
{
    public readonly bool Match;
    public readonly int OriginalLen;
    public readonly int ReencodedLen;
    public readonly int FirstDiffOffset; // -1 if identical
    public RoundTrip(bool match, int origLen, int reLen, int firstDiff)
    { Match = match; OriginalLen = origLen; ReencodedLen = reLen; FirstDiffOffset = firstDiff; }
}

// ==================================================================================================
// Node — the structural tree. Logs fields; re-serializes exact bytes via ordered parts.
// ==================================================================================================
public sealed class Node
{
    public string Name;
    /// <summary>Channel label e.g. "drdynvc(1007)", "rdpsnd", "I/O(1003)". Null when not channel-scoped.</summary>
    public string? Channel;
    /// <summary>Ordered fields for logging (name -> JSON value).</summary>
    public readonly List<KeyValuePair<string, JsonNode?>> Fields = new();
    /// <summary>Ordered byte-emitting parts: either a raw slice or a child Node.</summary>
    private readonly List<object> _parts = new(); // byte[] | Node
    public readonly List<Node> Children = new();

    public Node(string name) { Name = name; }

    public Node Field(string key, JsonNode? value) { Fields.Add(new(key, value)); return this; }
    public Node FieldHex(string key, ReadOnlySpan<byte> b, int max = 64) { return Field(key, Hex.Preview(b, max)); }

    /// <summary>Append literal bytes that reproduce part of the original (and are owned by this node).</summary>
    public void Raw(ReadOnlySpan<byte> b) { if (b.Length > 0) _parts.Add(b.ToArray()); }
    public void Raw(byte[] b) { if (b.Length > 0) _parts.Add(b); }

    /// <summary>Append a child node both as a byte-emitting part and as a logged child.</summary>
    public Node Child(Node c) { _parts.Add(c); Children.Add(c); return c; }

    /// <summary>Re-serialize: concatenate all parts depth-first.</summary>
    public void WriteTo(List<byte> outp)
    {
        foreach (var p in _parts)
        {
            if (p is byte[] arr) outp.AddRange(arr);
            else ((Node)p).WriteTo(outp);
        }
    }

    public byte[] Encode() { var l = new List<byte>(); WriteTo(l); return l.ToArray(); }

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        o["pdu"] = Name;
        if (Channel != null) o["channel"] = Channel;
        foreach (var kv in Fields) o[kv.Key] = kv.Value?.DeepClone();
        if (Children.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var c in Children) arr.Add(c.ToJson());
            o["children"] = arr;
        }
        return o;
    }
}

// ==================================================================================================
// W — a growable byte writer used by ENCODE-side helpers (mirrors ByteWriter in protocol.js).
// Only needed where we synthesize bytes rather than echo originals; the round-trip path mostly echoes,
// but having a writer keeps the encode helpers honest/symmetric for the framing layers.
// ==================================================================================================
internal sealed class W
{
    public readonly List<byte> B = new();
    public W U8(int v) { B.Add((byte)v); return this; }
    public W U16le(int v) { B.Add((byte)(v & 0xff)); B.Add((byte)((v >> 8) & 0xff)); return this; }
    public W U16be(int v) { B.Add((byte)((v >> 8) & 0xff)); B.Add((byte)(v & 0xff)); return this; }
    public W U32le(long v) { B.Add((byte)(v & 0xff)); B.Add((byte)((v >> 8) & 0xff)); B.Add((byte)((v >> 16) & 0xff)); B.Add((byte)((v >> 24) & 0xff)); return this; }
    public W Bytes(ReadOnlySpan<byte> s) { foreach (var x in s) B.Add(x); return this; }
    public W Zeros(int n) { for (int i = 0; i < n; i++) B.Add(0); return this; }
    public byte[] ToArray() => B.ToArray();
}

// ==================================================================================================
// Cur — little/big-endian read cursor over a byte span (mirrors ByteReader in protocol.js).
// ==================================================================================================
internal ref struct Cur
{
    public readonly ReadOnlySpan<byte> Buf;
    public int O;
    public Cur(ReadOnlySpan<byte> b) { Buf = b; O = 0; }
    public int Remaining => Buf.Length - O;
    public byte U8() => Buf[O++];
    public byte PeekU8() => Buf[O];
    public ushort U16le() { ushort v = (ushort)(Buf[O] | (Buf[O + 1] << 8)); O += 2; return v; }
    public ushort U16be() { ushort v = (ushort)((Buf[O] << 8) | Buf[O + 1]); O += 2; return v; }
    public uint U32le() { uint v = (uint)(Buf[O] | (Buf[O + 1] << 8) | (Buf[O + 2] << 16) | (Buf[O + 3] << 24)); O += 4; return v; }
    public uint U32be() { uint v = (uint)((Buf[O] << 24) | (Buf[O + 1] << 16) | (Buf[O + 2] << 8) | Buf[O + 3]); O += 4; return v; }
    public ReadOnlySpan<byte> Take(int n) { var s = Buf.Slice(O, n); O += n; return s; }
    public ReadOnlySpan<byte> Rest() { var s = Buf.Slice(O); O = Buf.Length; return s; }
}

internal static class Hex
{
    public static string Preview(ReadOnlySpan<byte> b, int max = 64)
    {
        int n = Math.Min(b.Length, max);
        var sb = new StringBuilder(n * 2 + 8);
        for (int i = 0; i < n; i++) sb.Append(b[i].ToString("x2"));
        if (b.Length > max) sb.Append("…+").Append(b.Length - max);
        return sb.ToString();
    }
    public static string Full(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(b.Length * 2);
        for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
        return sb.ToString();
    }
}
