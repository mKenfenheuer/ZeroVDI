using KSol.ZeroVDI.Models;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Issues a graceful shutdown to a manual resource using its configured <see cref="ShutdownMethod"/>.
/// Shared by the idle reaper (automatic shutdown) and the admin UI (on-demand "Stop").
/// </summary>
public class ResourceShutdownService
{
    private readonly SshCommandService _ssh;
    private readonly IpmiClient _ipmi;
    private readonly CredentialProtector _credentials;
    private readonly ILogger<ResourceShutdownService> _logger;

    public ResourceShutdownService(
        SshCommandService ssh,
        IpmiClient ipmi,
        CredentialProtector credentials,
        ILogger<ResourceShutdownService> logger)
    {
        _ssh = ssh;
        _ipmi = ipmi;
        _credentials = credentials;
        _logger = logger;
    }

    /// <summary>Returns true if the shutdown command was issued successfully.</summary>
    public async Task<bool> ShutDownAsync(RDPResource res, CancellationToken ct = default)
    {
        var method = ResolveShutdownMethod(res);

        switch (method)
        {
            case ShutdownMethod.Ssh:
            {
                var key = _credentials.Unprotect(res.ProtectedSshKey);
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(res.SshUser) || string.IsNullOrEmpty(res.IpAddress))
                    return false;
                var cmd = string.IsNullOrEmpty(res.ShutdownCommand) ? "shutdown -h now" : res.ShutdownCommand;
                var (success, _) = await _ssh.ExecuteAsync(res.IpAddress!, res.SshUser!, key, cmd);
                return success;
            }

            case ShutdownMethod.Ipmi:
            {
                if (string.IsNullOrEmpty(res.IpmiHost)) return false;
                var pass = _credentials.Unprotect(res.ProtectedIpmiPassword);
                return await _ipmi.PowerOffAsync(res.IpmiHost!, res.IpmiUser ?? "", pass ?? "");
            }

            case ShutdownMethod.Windows:
            {
                if (string.IsNullOrEmpty(res.IpAddress) || string.IsNullOrEmpty(res.WindowsUser))
                    return false;
                var pass = _credentials.Unprotect(res.ProtectedWindowsPassword) ?? "";
                return await NetRpcShutdownAsync(res.IpAddress!, res.WindowsUser!, pass, ct);
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Resolves the effective shutdown method. Resources configured before <see cref="ShutdownMethod"/>
    /// existed have <see cref="ShutdownMethod.None"/> stored; for those the method is inferred from the
    /// OS / wake method so existing configurations keep working.
    /// </summary>
    public static ShutdownMethod ResolveShutdownMethod(RDPResource res)
    {
        if (res.ShutdownMethod != ShutdownMethod.None) return res.ShutdownMethod;

        return res.OsType switch
        {
            OsType.Linux or OsType.MacOS => ShutdownMethod.Ssh,
            OsType.Windows or OsType.WindowsServer => ShutdownMethod.Windows,
            _ => res.WakeMethod == WakeMethod.Ipmi ? ShutdownMethod.Ipmi : ShutdownMethod.None,
        };
    }

    /// <summary>
    /// Remote Windows shutdown via Samba's <c>net rpc shutdown</c>. Used instead of <c>cmd.exe</c>'s
    /// <c>shutdown</c> because the gateway runs in a Linux container where <c>cmd.exe</c> does not exist.
    /// </summary>
    private async Task<bool> NetRpcShutdownAsync(string host, string user, string password, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("net")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            // net rpc shutdown -f (force) -t 0 (no delay) -I <host> -U user%password
            psi.ArgumentList.Add("rpc");
            psi.ArgumentList.Add("shutdown");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add(host);
            psi.ArgumentList.Add("-U");
            psi.ArgumentList.Add($"{user}%{password}");

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                _logger.LogWarning("net rpc shutdown {Host} failed (exit {Code}): {Err}", host, proc.ExitCode, stderr);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "net rpc shutdown {Host} failed", host);
            return false;
        }
    }
}
