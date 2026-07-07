using Renci.SshNet;

namespace KSol.ZeroVDI.RDP;

public class SshCommandService
{
    private readonly ILogger<SshCommandService> _logger;

    public SshCommandService(ILogger<SshCommandService> logger) => _logger = logger;

    public async Task<(bool Success, string Output)> ExecuteAsync(
        string host, string user, string privateKeyContent, string command)
    {
        try
        {
            using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(privateKeyContent));
            var keyFile = new PrivateKeyFile(keyStream);
            using var client = new SshClient(host, user, keyFile);
            await client.ConnectAsync(CancellationToken.None);
            var result = client.RunCommand(command);
            client.Disconnect();
            var output = (result.Result ?? "") + (result.Error ?? "");
            if (result.ExitStatus != 0)
                _logger.LogWarning("SSH command on {Host} exited {Code}: {Output}", host, result.ExitStatus, output);
            return (result.ExitStatus == 0, output);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH command on {Host} failed", host);
            return (false, ex.Message);
        }
    }
}
