// RdpLogSink — writes decoded PDUs to a human-readable text log and a JSON Lines file.
//
// Text log (pdus.log): one indented block per top-level PDU, e.g.
//   12345  C2S  ✓  TPKT > X224.Data > MCS.SendDataRequest [I/O(1003)]
//          RDP.DataPdu pduType2=0x1c INPUT shareId=...
//          input.MOUSE pointerFlags=0x800 x=512 y=384 decode=MOVE
//
// A leading ✓ means the PDU round-tripped byte-identically (decode+encode verified); ✗ means MISMATCH —
// the first differing byte offset and lengths are shown, and the original bytes were forwarded instead of
// our re-encoded bytes.
//
// JSONL (pdus.jsonl): one object per top-level PDU: {t, dir, rt:{match,...}, ...node tree...}.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KSol.RDPGateway.RDP;

public sealed class RdpLogSink : IRdpEventSink, IDisposable
{
    private readonly StreamWriter _text;
    private readonly StreamWriter _jsonl;
    private readonly object _lock = new();
    private readonly bool _echoConsole;
    private long _count;
    private long _mismatches;

    public RdpLogSink(string dir, bool echoConsole = true)
    {
        Directory.CreateDirectory(dir);
        _text = new StreamWriter(Path.Combine(dir, "pdus.log")) { AutoFlush = true };
        _jsonl = new StreamWriter(Path.Combine(dir, "pdus.jsonl")) { AutoFlush = true };
        _echoConsole = echoConsole;
    }

    public long Count => _count;
    public long Mismatches => _mismatches;

    public void Emit(RdpDir dir, long elapsedMs, Node node, RoundTrip rt)
    {
        lock (_lock)
        {
            _count++;
            if (!rt.Match) _mismatches++;

            string d = dir == RdpDir.ClientToServer ? "C2S" : "S2C";
            string mark = rt.Match ? "✓" : "✗";

            var sb = new StringBuilder();
            // Header line: timestamp, direction, round-trip mark, top-level path.
            sb.Append($"{elapsedMs,8}  {d}  {mark}  ");
            AppendNodeSummary(sb, node);
            if (!rt.Match)
                sb.Append($"   [MISMATCH orig={rt.OriginalLen}B re={rt.ReencodedLen}B firstDiff@{rt.FirstDiffOffset}]");
            sb.Append('\n');

            // Child detail lines (indented), skipping pure framing wrappers for readability.
            AppendChildren(sb, node, depth: 1);

            _text.Write(sb.ToString());

            // JSONL.
            var obj = new JsonObject
            {
                ["t"] = elapsedMs,
                ["dir"] = d,
                ["rt"] = new JsonObject
                {
                    ["match"] = rt.Match,
                    ["origLen"] = rt.OriginalLen,
                    ["reLen"] = rt.ReencodedLen,
                    ["firstDiff"] = rt.FirstDiffOffset,
                },
            };
            foreach (var kv in node.ToJson()) obj[kv.Key] = kv.Value?.DeepClone();
            _jsonl.WriteLine(obj.ToJsonString(JsonOpts));

            if (_echoConsole && (!rt.Match || _count <= 200 || _count % 500 == 0))
            {
                var line = new StringBuilder();
                line.Append($"{d} {mark} ");
                AppendNodeSummary(line, node);
                if (!rt.Match) line.Append($"  [MISMATCH @{rt.FirstDiffOffset} {rt.OriginalLen}/{rt.ReencodedLen}B]");
                Console.WriteLine("  " + line);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // "TPKT > X224.Data > MCS.SendDataRequest [I/O(1003)]: f1=v1 f2=v2"
    private static void AppendNodeSummary(StringBuilder sb, Node node)
    {
        // Follow the single-child framing chain to the most specific node for the header path.
        var path = new List<Node> { node };
        var cur = node;
        while (cur.Children.Count == 1) { cur = cur.Children[0]; path.Add(cur); }

        sb.Append(string.Join(" > ", path.ConvertAll(p => p.Name)));
        var deepest = path[^1];
        if (deepest.Channel != null) sb.Append($" [{deepest.Channel}]");
        if (deepest.Fields.Count > 0)
        {
            sb.Append(": ");
            sb.Append(FieldsInline(deepest));
        }
    }

    private static void AppendChildren(StringBuilder sb, Node node, int depth)
    {
        // If we followed a single-child chain in the summary, descend to where it branches.
        var cur = node;
        while (cur.Children.Count == 1) cur = cur.Children[0];

        foreach (var ch in cur.Children)
            AppendNodeRecursive(sb, ch, depth);
    }

    private static void AppendNodeRecursive(StringBuilder sb, Node n, int depth)
    {
        sb.Append(new string(' ', 8 + depth * 2 + 2));
        sb.Append(n.Name);
        if (n.Channel != null) sb.Append($" [{n.Channel}]");
        if (n.Fields.Count > 0) { sb.Append(": "); sb.Append(FieldsInline(n)); }
        sb.Append('\n');
        foreach (var ch in n.Children) AppendNodeRecursive(sb, ch, depth + 1);
    }

    private static string FieldsInline(Node n)
    {
        var parts = new List<string>(n.Fields.Count);
        foreach (var kv in n.Fields)
        {
            var v = kv.Value;
            string vs = v is JsonArray ja ? "[" + string.Join(",", ja.Select(x => x?.ToString())) + "]"
                       : v?.ToString() ?? "null";
            parts.Add($"{kv.Key}={vs}");
        }
        return string.Join(" ", parts);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _text.WriteLine($"\n# {_count} PDUs, {_mismatches} round-trip mismatches");
            _text.Flush(); _text.Dispose();
            _jsonl.Flush(); _jsonl.Dispose();
        }
    }
}
