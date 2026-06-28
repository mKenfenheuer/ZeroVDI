using System.Net.Http.Headers;
using System.Text.Json;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway.RDP;

/// <summary>A VM as listed by the Proxmox cluster resources endpoint.</summary>
public record ProxmoxVm(int VmId, string Node, string Name, string Status);

/// <summary>
/// Minimal Proxmox VE REST client. Each call targets a specific <see cref="ProxmoxBackend"/> and
/// authenticates with that backend's API token
/// (<c>Authorization: PVEAPIToken=&lt;id&gt;=&lt;secret&gt;</c>). Only the calls the gateway needs
/// are implemented: VM inventory, status, power actions, guest-agent IP discovery, and reading the
/// VM notes/description.
/// </summary>
public class ProxmoxClient
{
    private readonly ILogger<ProxmoxClient> _logger;

    public ProxmoxClient(ILogger<ProxmoxClient> logger)
    {
        _logger = logger;
    }

    private HttpClient? CreateClient(ProxmoxBackend backend)
    {
        if (!backend.IsConfigured) return null;

        var handler = new HttpClientHandler();
        if (!backend.VerifyTls)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(backend.Host!.TrimEnd('/') + "/api2/json/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("PVEAPIToken", $"{backend.ApiTokenId}={backend.ApiTokenSecret}");
        return client;
    }

    /// <summary>Lists all QEMU VMs across the cluster. Empty if the backend is not configured/reachable.</summary>
    public async Task<IReadOnlyList<ProxmoxVm>> ListVmsAsync(ProxmoxBackend backend, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return Array.Empty<ProxmoxVm>();

        try
        {
            using var doc = await GetJsonAsync(client, "cluster/resources?type=vm", ct);
            if (doc == null) return Array.Empty<ProxmoxVm>();

            var list = new List<ProxmoxVm>();
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                // Skip LXC containers; we only drive QEMU VMs.
                if (item.TryGetProperty("type", out var type) && type.GetString() != "qemu") continue;
                // Skip template VMs — they aren't bootable desktops. cluster/resources reports
                // template as 1 for templates (absent/0 otherwise).
                if (item.TryGetProperty("template", out var tmpl) && tmpl.ValueKind == JsonValueKind.Number
                    && tmpl.GetInt32() == 1) continue;
                var vmid = item.TryGetProperty("vmid", out var v) ? v.GetInt32() : 0;
                var node = item.TryGetProperty("node", out var n) ? n.GetString() ?? "" : "";
                var name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                var status = item.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                if (vmid != 0) list.Add(new ProxmoxVm(vmid, node, name, status));
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: failed to list VMs", backend.Name);
            return Array.Empty<ProxmoxVm>();
        }
    }

    /// <summary>Returns the qmpstatus ("running", "stopped", "suspended", ...) for a VM, or null.</summary>
    public async Task<string?> GetStatusAsync(ProxmoxBackend backend, string node, int vmid, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return null;
        try
        {
            using var doc = await GetJsonAsync(client, $"nodes/{node}/qemu/{vmid}/status/current", ct);
            if (doc == null) return null;
            var data = doc.RootElement.GetProperty("data");
            if (data.TryGetProperty("qmpstatus", out var s)) return s.GetString();
            return data.TryGetProperty("status", out var st) ? st.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: failed to get status for {Node}/{VmId}", backend.Name, node, vmid);
            return null;
        }
    }

    public Task<bool> StartAsync(ProxmoxBackend backend, string node, int vmid, CancellationToken ct = default)
        => PostActionAsync(backend, node, vmid, "status/start", ct);

    public Task<bool> StopAsync(ProxmoxBackend backend, string node, int vmid, CancellationToken ct = default)
        => PostActionAsync(backend, node, vmid, "status/stop", ct);

    public Task<bool> ResumeAsync(ProxmoxBackend backend, string node, int vmid, CancellationToken ct = default)
        => PostActionAsync(backend, node, vmid, "status/resume", ct);

    /// <summary>Suspends to RAM, or to disk (hibernate) when <paramref name="toDisk"/> is set.</summary>
    public Task<bool> SuspendAsync(ProxmoxBackend backend, string node, int vmid, bool toDisk, CancellationToken ct = default)
        => PostActionAsync(backend, node, vmid, toDisk ? "status/suspend?todisk=1" : "status/suspend", ct);

    /// <summary>Applies the configured <see cref="PauseAction"/> to a VM.</summary>
    public Task<bool> PauseAsync(ProxmoxBackend backend, string node, int vmid, PauseAction action, CancellationToken ct = default)
        => action switch
        {
            PauseAction.Stop => StopAsync(backend, node, vmid, ct),
            PauseAction.Hibernate => SuspendAsync(backend, node, vmid, toDisk: true, ct),
            _ => SuspendAsync(backend, node, vmid, toDisk: false, ct),
        };

    /// <summary>
    /// Brings a VM to running: resumes a suspended VM, otherwise starts it. Returns true if the
    /// action was accepted (not that the guest is fully up — poll the IP for that).
    /// </summary>
    public async Task<bool> EnsureRunningAsync(ProxmoxBackend backend, string node, int vmid, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(backend, node, vmid, ct);
        if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(status, "suspended", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "paused", StringComparison.OrdinalIgnoreCase))
            return await ResumeAsync(backend, node, vmid, ct);
        return await StartAsync(backend, node, vmid, ct);
    }

    /// <summary>
    /// Reads the first non-loopback IPv4 address the QEMU guest agent reports, or null if the agent
    /// is not yet responding (VM still booting) or no address is available.
    /// </summary>
    public async Task<string?> GetGuestIpAsync(ProxmoxBackend backend, string node, int vmid, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return null;
        try
        {
            using var doc = await GetJsonAsync(client, $"nodes/{node}/qemu/{vmid}/agent/network-get-interfaces", ct);
            if (doc == null) return null;

            // data.result[].ip-addresses[] with ip-address-type "ipv4".
            var result = doc.RootElement.GetProperty("data").GetProperty("result");
            foreach (var iface in result.EnumerateArray())
            {
                var name = iface.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                if (name != null && name.StartsWith("lo", StringComparison.OrdinalIgnoreCase)) continue;
                if (!iface.TryGetProperty("ip-addresses", out var addrs)) continue;
                foreach (var addr in addrs.EnumerateArray())
                {
                    if (addr.TryGetProperty("ip-address-type", out var t) && t.GetString() == "ipv4"
                        && addr.TryGetProperty("ip-address", out var ip))
                    {
                        var s = ip.GetString();
                        if (!string.IsNullOrEmpty(s) && !s.StartsWith("127.") && !s.StartsWith("169.")) return s;
                    }
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            // The agent endpoint 500s while the VM is still booting; that's expected, log at debug.
            _logger.LogDebug(ex, "Proxmox[{Backend}]: guest agent IP not available yet for {Node}/{VmId}", backend.Name, node, vmid);
            return null;
        }
    }

    /// <summary>
    /// Reads the first non-loopback IPv4 address the QEMU guest agent reports, or null if the agent
    /// is not yet responding (VM still booting) or no address is available.
    /// </summary>
    public async Task<bool?> GetGuestAgentStatusAsync(ProxmoxBackend backend, string node, int vmid, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return null;
        try
        {
            using var doc = await GetJsonAsync(client, $"nodes/{node}/qemu/{vmid}/agent/get-host-name", ct);
            if (doc == null)
                return null;
            else
                return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the VM's notes/description field (used to carry per-resource config JSON).</summary>
    public async Task<string?> GetNotesAsync(ProxmoxBackend backend, string node, int vmid, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return null;
        try
        {
            using var doc = await GetJsonAsync(client, $"nodes/{node}/qemu/{vmid}/config", ct);
            if (doc == null) return null;
            var data = doc.RootElement.GetProperty("data");
            return data.TryGetProperty("description", out var desc) ? desc.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: failed to read notes for {Node}/{VmId}", backend.Name, node, vmid);
            return null;
        }
    }

    /// <summary>
    /// Writes the VM's notes/description field. Used to stamp the gateway resource id into the VM so
    /// the VM↔resource binding survives node migration and re-discovery.
    /// </summary>
    public async Task<bool> SetNotesAsync(ProxmoxBackend backend, string node, int vmid, string notes, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return false;
        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("description", notes),
            });
            var resp = await client.PutAsync($"nodes/{node}/qemu/{vmid}/config", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Surface the Proxmox error body — a failed notes write is almost always a token
                // permission issue (the token needs VM.Config.Options on the VM), which Proxmox
                // returns as 403 with an explanatory message we must not swallow.
                var body = await SafeReadBodyAsync(resp, ct);
                _logger.LogError("Proxmox[{Backend}]: set notes for {Node}/{VmId} failed {Status}: {Body}",
                    backend.Name, node, vmid, (int)resp.StatusCode, body);
                return false;
            }
            _logger.LogInformation("Proxmox[{Backend}]: stamped notes on {Node}/{VmId}", backend.Name, node, vmid);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: failed to set notes for {Node}/{VmId}", backend.Name, node, vmid);
            return false;
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return "<no body>"; }
    }

    private async Task<bool> PostActionAsync(ProxmoxBackend backend, string node, int vmid, string action, CancellationToken ct)
    {
        using var client = CreateClient(backend);
        if (client == null) return false;
        try
        {
            var resp = await client.PostAsync($"nodes/{node}/qemu/{vmid}/{action}", content: null, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Proxmox[{Backend}]: {Action} for {Node}/{VmId} returned {Status}",
                    backend.Name, action, node, vmid, (int)resp.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: {Action} failed for {Node}/{VmId}", backend.Name, action, node, vmid);
            return false;
        }
    }

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient client, string path, CancellationToken ct)
    {
        var resp = await client.GetAsync(path, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
