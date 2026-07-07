using System.Text.Json;
using System.Text.Json.Nodes;
using KSol.ZeroVDI.Models;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Reads and writes the gateway metadata a Proxmox VM carries in its notes/description field.
///
/// The binding between a VM and its <see cref="RDPResource"/> row is the <c>ksol-rdpgw-id</c>
/// property stamped into the notes JSON — not the (node, VMID) tuple — so the binding survives a
/// live-migration to another node and any re-discovery. The same JSON object may carry a
/// <c>description</c> string.
///
/// Notes that are not JSON are preserved: on write they are moved under <c>description</c> rather
/// than discarded.
/// </summary>
public static class ProxmoxNotes
{
    public const string IdProperty = "ksol-rdpgw-id";
    public const string ExcludeProperty = "ksol-rdpgw-exclude";

    /// <summary>The gateway resource id stamped in the notes, or null if not present/parseable.</summary>
    public static string? ReadId(string? notes)
    {
        var obj = TryParseObject(notes);
        if (obj != null && obj.TryGetPropertyValue(IdProperty, out var idNode) && idNode is JsonValue v
            && v.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id))
        {
            return id;
        }
        return null;
    }

    /// <summary>
    /// True if the VM notes mark it as excluded from indexing (<c>"ksol-rdpgw-exclude": true</c>).
    /// Discovery skips excluded VMs entirely and removes any existing resource row for them.
    /// </summary>
    public static bool ReadExcluded(string? notes)
    {
        var obj = TryParseObject(notes);
        if (obj != null && obj.TryGetPropertyValue(ExcludeProperty, out var n) && n is JsonValue v
            && v.TryGetValue<bool>(out var b))
        {
            return b;
        }
        return false;
    }

    /// <summary>
    /// Returns notes JSON identical to <paramref name="existingNotes"/> but with
    /// <c>ksol-rdpgw-exclude</c> set to true, preserving existing content. Free-text notes are moved
    /// under <c>description</c>.
    /// </summary>
    public static string WriteExcluded(string? existingNotes)
    {
        var obj = TryParseObject(existingNotes);
        if (obj == null)
        {
            obj = new JsonObject();
            if (!string.IsNullOrWhiteSpace(existingNotes))
            {
                obj["description"] = existingNotes;
            }
        }
        obj[ExcludeProperty] = true;
        // Drop the binding id: the row is being deleted, so the marker alone keeps it out.
        obj.Remove(IdProperty);
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The "description" string from the notes JSON, if present.</summary>
    public static string? ReadDescription(string? notes)
    {
        var obj = TryParseObject(notes);
        if (obj != null && obj.TryGetPropertyValue("description", out var d) && d is JsonValue v
            && v.TryGetValue<string>(out var s))
        {
            return s;
        }
        return null;
    }

    /// <summary>
    /// Returns notes JSON identical to <paramref name="existingNotes"/> but with
    /// <c>ksol-rdpgw-id</c> set to <paramref name="id"/>, preserving any existing
    /// <c>description</c> content. Free-text notes are moved under <c>description</c>.
    /// </summary>
    public static string WriteId(string? existingNotes, string id)
    {
        var obj = TryParseObject(existingNotes);
        if (obj == null)
        {
            obj = new JsonObject();
            if (!string.IsNullOrWhiteSpace(existingNotes))
            {
                obj["description"] = existingNotes;
            }
        }
        obj[IdProperty] = id;
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject? TryParseObject(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        try { return JsonNode.Parse(notes) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
