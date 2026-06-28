using System.Diagnostics;

namespace KSol.RDPGateway.RDP;

public class IpmiClient
{
    private readonly ILogger<IpmiClient> _logger;

    public IpmiClient(ILogger<IpmiClient> logger) => _logger = logger;

    public async Task<bool> PowerOnAsync(string host, string user, string password)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("ipmitool",
                $"-I lanplus -H {host} -U {user} -P {password} chassis power on")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc == null) return false;
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
            {
                var err = await proc.StandardError.ReadToEndAsync();
                _logger.LogWarning("IPMI power on {Host} failed (exit {Code}): {Err}", host, proc.ExitCode, err);
            }
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IPMI power on {Host} failed", host);
            return false;
        }
    }
}
