using System.Diagnostics;

namespace KSol.RDPGateway.RDP;

public class IpmiClient
{
    private readonly ILogger<IpmiClient> _logger;

    public IpmiClient(ILogger<IpmiClient> logger) => _logger = logger;

    public Task<bool> PowerOnAsync(string host, string user, string password) =>
        RunCommandAsync(host, user, password, "chassis power on");

    public Task<bool> PowerOffAsync(string host, string user, string password) =>
        RunCommandAsync(host, user, password, "chassis power soft");

    public Task<bool> ForceOffAsync(string host, string user, string password) =>
        RunCommandAsync(host, user, password, "chassis power off");

    public async Task<bool?> IsOnAsync(string host, string user, string password)
    {
        try
        {
            var (exitCode, stdout, _) = await RunAsync(host, user, password, "chassis power status");
            if (exitCode != 0) return null;
            return stdout.Contains("on", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IPMI power status {Host} failed", host);
            return null;
        }
    }

    private async Task<bool> RunCommandAsync(string host, string user, string password, string command)
    {
        try
        {
            var (exitCode, _, stderr) = await RunAsync(host, user, password, command);
            if (exitCode != 0)
                _logger.LogWarning("IPMI {Command} {Host} failed (exit {Code}): {Err}", command, host, exitCode, stderr);
            return exitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IPMI {Command} {Host} failed", command, host);
            return false;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string host, string user, string password, string command)
    {
        using var proc = Process.Start(new ProcessStartInfo("ipmitool",
            $"-I lanplus -H {host} -U {user} -P {password} {command}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (proc == null) return (-1, "", "process not started");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }
}
