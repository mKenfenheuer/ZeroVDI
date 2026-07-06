using KSol.ZeroVDI.Connector;

// ZeroVDI Connector agent.
//   connector register --url <gateway-url> --token <registration-token>   → enroll, persist auth token
//   connector run                                                          → hold the control channel open
//   connector                                                              → run (default)
//
// Config lives at ~/.zerovdi-connector/config.json (override with ZEROVDI_CONNECTOR_HOME). Environment
// fallbacks: ZEROVDI_URL, ZEROVDI_REGISTRATION_TOKEN.

var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "run";
var opts = ParseOptions(args);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (command)
{
    case "register":
    {
        var url = opts.GetValueOrDefault("url") ?? Environment.GetEnvironmentVariable("ZEROVDI_URL");
        var token = opts.GetValueOrDefault("token") ?? Environment.GetEnvironmentVariable("ZEROVDI_REGISTRATION_TOKEN");
        // Prompt for anything not supplied on the command line (or via env).
        if (string.IsNullOrWhiteSpace(url)) url = Prompt("Please enter the url: ");
        if (string.IsNullOrWhiteSpace(token)) token = Prompt("Please enter the registration token: ");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("a gateway URL and registration token are required.");
            return 2;
        }
        try
        {
            var cfg = await Agent.RegisterAsync(url, token);
            Console.WriteLine($"Registered as connector {cfg.ConnectorId}. Auth token saved. Starting…");
            await new Agent(cfg).RunAsync(cts.Token);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"registration failed: {ex.Message}");
            return 1;
        }
    }

    case "run":
    {
        var cfg = AgentConfig.Load();
        if (cfg == null || string.IsNullOrEmpty(cfg.AuthToken))
        {
            Console.Error.WriteLine("not registered. Run: connector register --url <gateway-url> --token <registration-token>");
            return 2;
        }
        await new Agent(cfg).RunAsync(cts.Token);
        return 0;
    }

    default:
        Console.Error.WriteLine($"unknown command '{command}'. Commands: register, run");
        return 2;
}

static string Prompt(string label)
{
    Console.Write(label);
    return Console.ReadLine()?.Trim() ?? "";
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var key = args[i][2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { opts[key] = args[++i]; }
        else opts[key] = "true";
    }
    return opts;
}
