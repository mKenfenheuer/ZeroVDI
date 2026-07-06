using System.Text.Json;

namespace KSol.ZeroVDI.Connector;

/// <summary>
/// Persisted agent identity: the gateway URL and the long-lived auth token obtained at registration.
/// Stored under <c>~/.zerovdi-connector/config.json</c> (override with <c>ZEROVDI_CONNECTOR_HOME</c>).
/// </summary>
public sealed class AgentConfig
{
    public string GatewayUrl { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public string? ConnectorId { get; set; }

    private static string Dir =>
        Environment.GetEnvironmentVariable("ZEROVDI_CONNECTOR_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zerovdi-connector");

    private static string Path_ => Path.Combine(Dir, "config.json");

    public static AgentConfig? Load()
    {
        try
        {
            if (!File.Exists(Path_)) return null;
            return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(Path_));
        }
        catch { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
