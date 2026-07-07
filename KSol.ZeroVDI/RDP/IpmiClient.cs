using System.Diagnostics;

namespace KSol.ZeroVDI.RDP;

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
        // Build the argument vector explicitly (never a single interpolated string): this prevents
        // argument-splitting / injection if host/user contain whitespace or metacharacters. The password
        // is passed via the IPMITOOL_PASSWORD environment variable with -E rather than -P, so it never
        // appears in the process command line (visible to any local user via `ps` / /proc/<pid>/cmdline).
        var psi = new ProcessStartInfo("ipmitool")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add("lanplus");
        psi.ArgumentList.Add("-H");
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add("-U");
        psi.ArgumentList.Add(user);
        psi.ArgumentList.Add("-E"); // read password from $IPMITOOL_PASSWORD
        // The command (e.g. "chassis power on") is a fixed, code-defined string — split into tokens.
        foreach (var token in command.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(token);
        psi.Environment["IPMITOOL_PASSWORD"] = password;

        using var proc = Process.Start(psi);
        if (proc == null) return (-1, "", "process not started");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }
}
