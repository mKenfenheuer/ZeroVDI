using System.Net.Http.Headers;
using System.Text.Json;
using KSol.ZeroVDI.Models;

namespace KSol.ZeroVDI.RDP;

/// <summary>A VM as listed by the Proxmox cluster resources endpoint.</summary>
public record ProxmoxVm(int VmId, string Node, string Name, string Status);

/// <summary>
/// The SPICE connection parameters returned by Proxmox's <c>spiceproxy</c> endpoint (the same fields a
/// virt-viewer <c>.vv</c> file carries). The browser cannot reach the VM's SPICE server directly:
/// Proxmox routes SPICE through the <c>spiceproxy</c> daemon (an HTTP <c>CONNECT</c> proxy, normally on
/// port 3128). To reach the SPICE server the gateway must: TCP-connect to <see cref="ProxyHost"/>:<see
/// cref="ProxyPort"/>, send <c>CONNECT <see cref="RoutingHost"/>:<see cref="TlsPort"/></c>, start TLS,
/// then speak SPICE using <see cref="Password"/> as the single-use ticket. See
/// <see cref="SpiceProxyTransport"/>.
/// </summary>
public record SpiceConnection(
    string ProxyHost,
    int ProxyPort,
    string RoutingHost,
    int TlsPort,
    string Password,
    string? Ca,
    string? HostSubject);

/// <summary>
/// Minimal Proxmox VE REST client. Each call targets a specific <see cref="ProxmoxBackend"/> and
/// authenticates with that backend's API token
/// (<c>Authorization: PVEAPIToken=&lt;id&gt;=&lt;secret&gt;</c>). Only the calls the gateway needs
/// are implemented: VM inventory, status, power actions, guest-agent IP discovery, and reading the
/// VM notes/description.
/// </summary>
public class ProxmoxClient
{
    private readonly ConnectorHub _connectors;
    private readonly ILogger<ProxmoxClient> _logger;

    public ProxmoxClient(ConnectorHub connectors, ILogger<ProxmoxClient> logger)
    {
        _connectors = connectors;
        _logger = logger;
    }

    private HttpClient? CreateClient(ProxmoxBackend backend)
    {
        if (!backend.IsConfigured) return null;

        var handler = new SocketsHttpHandler();
        if (!backend.VerifyTls)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        // Tunnel the API TCP socket through a connector when the backend is reached via one. HttpClient
        // still terminates TLS on top of the returned stream, so cert handling above is unaffected.
        if (!string.IsNullOrEmpty(backend.ConnectorId))
        {
            var connectorId = backend.ConnectorId;
            handler.ConnectCallback = async (ctx, ct) =>
                await _connectors.OpenTcpAsync(connectorId, ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct);
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

    /// <summary>
    /// Lists QEMU <em>template</em> VMs (the ones <see cref="ListVmsAsync"/> deliberately skips). Used
    /// by the VDI pool editor to pick a clone source. Optionally filtered to a single node.
    /// </summary>
    public async Task<IReadOnlyList<ProxmoxVm>> ListTemplatesAsync(ProxmoxBackend backend, CancellationToken ct = default)
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
                if (item.TryGetProperty("type", out var type) && type.GetString() != "qemu") continue;
                // Keep ONLY templates (the inverse of ListVmsAsync).
                if (!(item.TryGetProperty("template", out var tmpl) && tmpl.ValueKind == JsonValueKind.Number
                      && tmpl.GetInt32() == 1)) continue;
                var vmid = item.TryGetProperty("vmid", out var v) ? v.GetInt32() : 0;
                var node = item.TryGetProperty("node", out var n) ? n.GetString() ?? "" : "";
                var name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                if (vmid != 0) list.Add(new ProxmoxVm(vmid, node, name, "template"));
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: failed to list templates", backend.Name);
            return Array.Empty<ProxmoxVm>();
        }
    }

    /// <summary>
    /// Allocates a free VMID from the cluster (<c>cluster/nextid</c>). If a <paramref name="rangeStart"/>/
    /// <paramref name="rangeEnd"/> window is given, returns the first free id in that window instead (so a
    /// pool can keep its clones in a dedicated VMID band). Returns null if none is available.
    /// </summary>
    public async Task<int?> GetNextVmIdAsync(ProxmoxBackend backend, int? rangeStart = null, int? rangeEnd = null, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return null;
        try
        {
            if (rangeStart is int start && rangeEnd is int end && end >= start)
            {
                // Walk the requested band, asking Proxmox whether each id is free via cluster/nextid?vmid=.
                var taken = (await ListAllVmIdsAsync(client, ct));
                for (var id = start; id <= end; id++)
                    if (!taken.Contains(id)) return id;
                return null;
            }

            using var doc = await GetJsonAsync(client, "cluster/nextid", ct);
            if (doc == null) return null;
            var data = doc.RootElement.GetProperty("data");
            return data.ValueKind switch
            {
                JsonValueKind.Number => data.GetInt32(),
                JsonValueKind.String when int.TryParse(data.GetString(), out var n) => n,
                _ => (int?)null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: failed to get next VMID", backend.Name);
            return null;
        }
    }

    private static async Task<HashSet<int>> ListAllVmIdsAsync(HttpClient client, CancellationToken ct)
    {
        var ids = new HashSet<int>();
        using var doc = await GetJsonAsync(client, "cluster/resources?type=vm", ct);
        if (doc == null) return ids;
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            if (item.TryGetProperty("vmid", out var v) && v.ValueKind == JsonValueKind.Number)
                ids.Add(v.GetInt32());
        return ids;
    }

    /// <summary>
    /// Clones a template into a new VM. Returns the Proxmox task UPID (the clone runs asynchronously —
    /// poll it with <see cref="WaitForTaskAsync"/> before the clone is bootable), or null on failure.
    /// </summary>
    public async Task<string?> CloneAsync(
        ProxmoxBackend backend, string templateNode, int templateVmId, int newVmId, string name,
        bool full, string? targetNode = null, string? targetStorage = null, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return null;

        var form = new List<KeyValuePair<string, string>>
        {
            new("newid", newVmId.ToString()),
            new("name", name),
            new("full", full ? "1" : "0"),
        };
        if (!string.IsNullOrWhiteSpace(targetNode)) form.Add(new("target", targetNode));
        if (!string.IsNullOrWhiteSpace(targetStorage)) form.Add(new("storage", targetStorage));

        try
        {
            var resp = await client.PostAsync($"nodes/{templateNode}/qemu/{templateVmId}/clone",
                new FormUrlEncodedContent(form), ct);
            var body = await SafeReadBodyAsync(resp, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Proxmox[{Backend}]: clone {Tpl}->{New} failed {Status}: {Body}",
                    backend.Name, templateVmId, newVmId, (int)resp.StatusCode, body);
                return null;
            }
            var upid = ReadDataString(body);
            _logger.LogInformation("Proxmox[{Backend}]: clone {Tpl}->{New} started (upid {Upid})",
                backend.Name, templateVmId, newVmId, upid);
            return upid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: clone {Tpl}->{New} failed", backend.Name, templateVmId, newVmId);
            return null;
        }
    }

    /// <summary>
    /// Polls a Proxmox task (UPID) until it stops, then reports success (<c>exitstatus == "OK"</c>).
    /// Returns false on timeout, a non-OK exit, or any error. Tasks run on the node that started them.
    /// </summary>
    public async Task<bool> WaitForTaskAsync(
        ProxmoxBackend backend, string node, string upid, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return false;

        var deadline = DateTime.UtcNow.Add(timeout);
        var encoded = Uri.EscapeDataString(upid);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var doc = await GetJsonAsync(client, $"nodes/{node}/tasks/{encoded}/status", ct);
                if (doc != null)
                {
                    var data = doc.RootElement.GetProperty("data");
                    var status = data.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        var exit = data.TryGetProperty("exitstatus", out var e) ? e.GetString() : null;
                        var ok = string.Equals(exit, "OK", StringComparison.OrdinalIgnoreCase);
                        if (!ok)
                            _logger.LogError("Proxmox[{Backend}]: task {Upid} finished with exitstatus '{Exit}'",
                                backend.Name, upid, exit);
                        return ok;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Proxmox[{Backend}]: polling task {Upid}", backend.Name, upid);
            }
            await Task.Delay(2000, ct);
        }
        _logger.LogWarning("Proxmox[{Backend}]: task {Upid} did not finish within {Timeout}", backend.Name, upid, timeout);
        return false;
    }

    /// <summary>Deletes (destroys) a VM and its disks. Returns the task UPID, or null on failure.</summary>
    public async Task<string?> DeleteVmAsync(ProxmoxBackend backend, string node, int vmid, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return null;
        try
        {
            // purge=1 also removes the VM from any HA/backup jobs and its disks.
            var resp = await client.DeleteAsync($"nodes/{node}/qemu/{vmid}?purge=1&destroy-unreferenced-disks=1", ct);
            var body = await SafeReadBodyAsync(resp, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Proxmox[{Backend}]: delete {Node}/{VmId} failed {Status}: {Body}",
                    backend.Name, node, vmid, (int)resp.StatusCode, body);
                return null;
            }
            return ReadDataString(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: delete {Node}/{VmId} failed", backend.Name, node, vmid);
            return null;
        }
    }

    /// <summary>
    /// Destroys a VM safely: checks its current status, stops it first if it is running (Proxmox refuses
    /// to delete a running VM), waits for the stop to take effect, then deletes it and waits for the
    /// destroy task to finish. Returns true once the VM is gone. Best-effort — logs and returns false on
    /// any step failing so callers can surface the problem; a VM that was already absent counts as success.
    /// </summary>
    public async Task<bool> DestroyVmAsync(ProxmoxBackend backend, string node, int vmid, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(backend, node, vmid, ct);
        // Null status: the VM is gone or unreachable. Treat a vanished VM as already-destroyed.
        if (status == null) return true;

        if (!string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase))
        {
            if (!await StopAsync(backend, node, vmid, ct))
            {
                _logger.LogWarning("Proxmox[{Backend}]: could not stop {Node}/{VmId} before destroy", backend.Name, node, vmid);
                return false;
            }
            // status/stop returns immediately; poll until the VM actually reports stopped before deleting.
            if (!await WaitForStatusAsync(backend, node, vmid, "stopped", TimeSpan.FromMinutes(2), ct))
            {
                _logger.LogWarning("Proxmox[{Backend}]: {Node}/{VmId} did not stop in time before destroy", backend.Name, node, vmid);
                return false;
            }
        }

        var upid = await DeleteVmAsync(backend, node, vmid, ct);
        if (upid == null) return false;
        return await WaitForTaskAsync(backend, node, upid, TimeSpan.FromMinutes(5), ct);
    }

    /// <summary>Polls a VM's status until it matches <paramref name="target"/> (case-insensitive) or the timeout elapses.</summary>
    private async Task<bool> WaitForStatusAsync(
        ProxmoxBackend backend, string node, int vmid, string target, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var status = await GetStatusAsync(backend, node, vmid, ct);
            if (string.Equals(status, target, StringComparison.OrdinalIgnoreCase)) return true;
            await Task.Delay(2000, ct);
        }
        return false;
    }

    /// <summary>
    /// Sets cloud-init customization fields on a (stopped) clone before first boot: ciuser, cipassword,
    /// sshkeys, and the guest hostname (Proxmox stores the hostname via the searchdomain/ipconfig path;
    /// here we use the dedicated <c>name</c> plus cloud-init <c>ciuser</c>/etc.). Returns true on success.
    /// </summary>
    public async Task<bool> SetCloudInitAsync(
        ProxmoxBackend backend, string node, int vmid,
        string? ciUser, string? ciPassword, string? sshKeys, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return false;

        var form = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(ciUser)) form.Add(new("ciuser", ciUser));
        if (!string.IsNullOrWhiteSpace(ciPassword)) form.Add(new("cipassword", ciPassword));
        if (!string.IsNullOrWhiteSpace(sshKeys)) form.Add(new("sshkeys", Uri.EscapeDataString(sshKeys)));
        if (form.Count == 0) return true;

        try
        {
            var resp = await client.PutAsync($"nodes/{node}/qemu/{vmid}/config", new FormUrlEncodedContent(form), ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, ct);
                _logger.LogError("Proxmox[{Backend}]: set cloud-init for {Node}/{VmId} failed {Status}: {Body}",
                    backend.Name, node, vmid, (int)resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: set cloud-init for {Node}/{VmId} failed", backend.Name, node, vmid);
            return false;
        }
    }

    /// <summary>
    /// Runs a command in the guest via the QEMU guest agent (<c>agent/exec</c>). Returns the agent's
    /// PID handle for <see cref="GetGuestExecStatusAsync"/>, or null on failure. Used by the GuestAgent
    /// identity path (rename / domain-join on Windows clones without cloud-init).
    /// </summary>
    public async Task<int?> GuestExecAsync(
        ProxmoxBackend backend, string node, int vmid, string command, IEnumerable<string>? args = null, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return null;

        var form = new List<KeyValuePair<string, string>> { new("command", command) };
        if (args != null)
            foreach (var a in args) form.Add(new("command", a)); // repeated 'command' = argv array

        try
        {
            var resp = await client.PostAsync($"nodes/{node}/qemu/{vmid}/agent/exec",
                new FormUrlEncodedContent(form), ct);
            var body = await SafeReadBodyAsync(resp, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Proxmox[{Backend}]: guest exec on {Node}/{VmId} failed {Status}: {Body}",
                    backend.Name, node, vmid, (int)resp.StatusCode, body);
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            return data.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number
                ? pid.GetInt32() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: guest exec on {Node}/{VmId} failed", backend.Name, node, vmid);
            return null;
        }
    }

    /// <summary>The result of a guest-agent exec: whether it exited and, if so, its exit code.</summary>
    public record GuestExecStatus(bool Exited, int? ExitCode);

    /// <summary>Reads the status of a previously started guest-agent exec (<c>agent/exec-status</c>).</summary>
    public async Task<GuestExecStatus?> GetGuestExecStatusAsync(
        ProxmoxBackend backend, string node, int vmid, int pid, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return null;
        try
        {
            using var doc = await GetJsonAsync(client, $"nodes/{node}/qemu/{vmid}/agent/exec-status?pid={pid}", ct);
            if (doc == null) return null;
            var data = doc.RootElement.GetProperty("data");
            var exited = data.TryGetProperty("exited", out var ex) && ex.ValueKind == JsonValueKind.Number && ex.GetInt32() == 1;
            int? code = data.TryGetProperty("exitcode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
            return new GuestExecStatus(exited, code);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Proxmox[{Backend}]: guest exec-status on {Node}/{VmId} pid {Pid}", backend.Name, node, vmid, pid);
            return null;
        }
    }

    /// <summary>Pulls the <c>data</c> string (e.g. a UPID) out of a Proxmox JSON response body.</summary>
    private static string? ReadDataString(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
        }
        catch { return null; }
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

    public record RrdDataPoint(double Time, double? Cpu, double? Maxcpu, double? Mem, double? Maxmem,
        double? Netin, double? Netout, double? Diskread, double? Diskwrite);

    public async Task<IReadOnlyList<RrdDataPoint>> GetRrdDataAsync(
        ProxmoxBackend backend, string node, int vmid, string timeframe = "hour", CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return Array.Empty<RrdDataPoint>();

        try
        {
            using var doc = await GetJsonAsync(client, $"nodes/{node}/qemu/{vmid}/rrddata?timeframe={timeframe}", ct);
            if (doc == null) return Array.Empty<RrdDataPoint>();

            var list = new List<RrdDataPoint>();
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                double GetD(string prop) => item.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                double? GetN(string prop) => item.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

                list.Add(new RrdDataPoint(
                    GetD("time"), GetN("cpu"), GetN("maxcpu"),
                    GetN("mem"), GetN("maxmem"),
                    GetN("netin"), GetN("netout"),
                    GetN("diskread"), GetN("diskwrite")));
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: failed to get RRD data for {Node}/{VmId}", backend.Name, node, vmid);
            return Array.Empty<RrdDataPoint>();
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
    /// Reads the VM's display adapter (<c>vga</c>) and guest OS type (<c>ostype</c>) from its config, used
    /// to pick a sensible default protocol at first discovery. Both may be null if absent/unreadable —
    /// Proxmox omits <c>vga</c> when it defaults to <c>std</c>, and <c>ostype</c> for unspecified guests.
    /// </summary>
    public async Task<(string? Vga, string? OsType)> GetDisplayInfoAsync(ProxmoxBackend backend, string node, int vmid, CancellationToken ct = default)
    {
        using var client = CreateClient(backend);
        if (client == null) return (null, null);
        try
        {
            using var doc = await GetJsonAsync(client, $"nodes/{node}/qemu/{vmid}/config", ct);
            if (doc == null) return (null, null);
            var data = doc.RootElement.GetProperty("data");
            // vga may be "qxl" or "qxl,memory=64" etc.; take the bare type before any comma.
            string? vga = data.TryGetProperty("vga", out var v) && !string.IsNullOrEmpty(v.GetString())
                ? v.GetString()!.Split(',')[0].Trim() : null;
            string? os = data.TryGetProperty("ostype", out var o) ? o.GetString() : null;
            return (vga, os);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: failed to read display info for {Node}/{VmId}", backend.Name, node, vmid);
            return (null, null);
        }
    }

    /// <summary>Whether a Proxmox <c>ostype</c> value denotes a Windows guest (win*/wxp/w2k*).</summary>
    public static bool IsWindowsOsType(string? ostype)
    {
        if (string.IsNullOrWhiteSpace(ostype)) return false;
        var o = ostype.Trim().ToLowerInvariant();
        return o.StartsWith("win") || o.StartsWith("wxp") || o.StartsWith("w2k");
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

    /// <summary>
    /// Whether a Proxmox <c>vga</c> value denotes a SPICE-capable display. Proxmox serves SPICE for the
    /// QXL adapters (<c>qxl</c>, <c>qxl2</c>…) and for <c>virtio</c>/<c>virtio-gl</c>; <c>std</c>,
    /// <c>vmware</c>, <c>cirrus</c>, <c>serial*</c> and <c>none</c> are not SPICE.
    /// </summary>
    public static bool IsSpiceVga(string? vga)
    {
        if (string.IsNullOrWhiteSpace(vga)) return false;
        var v = vga.Trim().ToLowerInvariant();
        return v.StartsWith("qxl") || v.StartsWith("virtio");
    }

    /// <summary>
    /// Obtains the SPICE connection parameters (single-use ticket + spiceproxy address) for a VM.
    ///
    /// Unlike the in-browser SPICE console, our SPICE→RDP bridge only needs the ticket + proxy address;
    /// we relay the SPICE stream ourselves. The <c>spiceproxy</c> endpoint works with the backend's
    /// <b>API token</b> (verified against a live PVE host) — no login ticket/CSRF is required — but it
    /// lives under the <c>/api2/spiceconfig/</c> path prefix (NOT <c>/api2/json/</c>) and returns a
    /// virt-viewer <c>.vv</c> INI file rather than JSON. The ticket is single-use (~30s TTL), so this
    /// must be called lazily right before connecting. Returns null on any failure.
    /// </summary>
    public async Task<SpiceConnection?> GetSpiceProxyAsync(ProxmoxBackend backend, string node, int vmid,
        CancellationToken ct = default)
    {
        if (!backend.IsConfigured) return null;

        var handler = new SocketsHttpHandler();
        if (!backend.VerifyTls)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        if (!string.IsNullOrEmpty(backend.ConnectorId))
        {
            var connectorId = backend.ConnectorId;
            handler.ConnectCallback = async (ctx, ict) =>
                await _connectors.OpenTcpAsync(connectorId, ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ict);
        }
        // spiceconfig path prefix — the .vv config lives here, not under /api2/json/.
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(backend.Host!.TrimEnd('/') + "/api2/spiceconfig/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("PVEAPIToken", $"{backend.ApiTokenId}={backend.ApiTokenSecret}");

        try
        {
            // proxy=<host> tells Proxmox which spiceproxy address to embed; we relay regardless, so the
            // API host works. The value is echoed back in the `proxy=` field we dial.
            var apiHost = new Uri(backend.Host!).Host;
            var resp = await client.PostAsync($"nodes/{node}/qemu/{vmid}/spiceproxy",
                new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("proxy", apiHost) }), ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Proxmox[{Backend}]: spiceproxy for {Node}/{VmId} failed {Status}: {Body}",
                    backend.Name, node, vmid, (int)resp.StatusCode, await SafeReadBodyAsync(resp, ct));
                return null;
            }

            var vv = ParseVv(await resp.Content.ReadAsStringAsync(ct));
            vv.TryGetValue("host", out var routingHost);
            vv.TryGetValue("password", out var password);
            vv.TryGetValue("proxy", out var proxyRaw);
            vv.TryGetValue("ca", out var ca);
            vv.TryGetValue("host-subject", out var subject);
            int tlsPort = vv.TryGetValue("tls-port", out var tp) && int.TryParse(tp, out var tpv) ? tpv
                : (vv.TryGetValue("port", out var p) && int.TryParse(p, out var pv) ? pv : 0);

            if (string.IsNullOrEmpty(routingHost) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(proxyRaw))
            {
                _logger.LogError("Proxmox[{Backend}]: spiceproxy response missing host/password/proxy for {Node}/{VmId}",
                    backend.Name, node, vmid);
                return null;
            }

            var proxyUri = new Uri(proxyRaw);
            var proxyPort = proxyUri.IsDefaultPort ? 3128 : proxyUri.Port;
            // Prefer the host Proxmox put in proxy=; fall back to the API host if it won't resolve.
            var proxyHost = await Resolvable(proxyUri.Host) ? proxyUri.Host : apiHost;
            _logger.LogInformation("Proxmox[{Backend}]: spiceproxy {Node}/{VmId} -> dial {Host}:{Port} route {Route}:{Tls}",
                backend.Name, node, vmid, proxyHost, proxyPort, routingHost, tlsPort);
            return new SpiceConnection(proxyHost, proxyPort, routingHost!, tlsPort, password!,
                UnescapeVv(ca), subject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxmox[{Backend}]: spiceproxy failed for {Node}/{VmId}", backend.Name, node, vmid);
            return null;
        }
    }

    /// <summary>Parses a virt-viewer <c>.vv</c> INI body into a flat key→value map (last section wins).</summary>
    private static Dictionary<string, string> ParseVv(string body)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in body.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length == 0 || l[0] == '[') continue;
            int eq = l.IndexOf('=');
            if (eq <= 0) continue;
            map[l[..eq].Trim()] = l[(eq + 1)..].Trim();
        }
        return map;
    }

    /// <summary>The <c>ca=</c> field carries literal <c>\n</c> escapes; turn them into real newlines.</summary>
    private static string? UnescapeVv(string? s) => s?.Replace("\\n", "\n");

    /// <summary>Whether a hostname resolves (or is already an IP literal) from the gateway.</summary>
    private static async Task<bool> Resolvable(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out _)) return true;
        try { return (await System.Net.Dns.GetHostAddressesAsync(host)).Length > 0; }
        catch { return false; }
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
