using KSol.ZeroVDI.Connector;

// ZeroVDI Connector agent.
//   connector register --url <gateway-url> --token <registration-token>   → enroll, persist auth token
//   connector run                                                          → hold the control channel open
//   connector                                                              → run (default)
//
// Config lives at ~/.zerovdi-connector/config.json (override with ZEROVDI_CONNECTOR_HOME). Environment
// configuration (used by the Docker image, which persists no state):
//   ZEROVDI_URL                 gateway base URL (https://…)
//   ZEROVDI_AUTH_TOKEN          long-lived auth token → run stateless, no config file needed
//   ZEROVDI_REGISTRATION_TOKEN  one-time token → self-enroll on first boot (persists to config)
//   ZEROVDI_CONNECTOR_ID        optional, informational when injecting an auth token
//   ZEROVDI_CONNECTOR_HOME      override config directory

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
        // Prefer an explicit auth token from the environment (ideal for containers running with no persisted
        // state); otherwise fall back to the on-disk config written at registration. If a URL is supplied
        // via env but only a registration token is present, self-enroll on first boot.
        var envUrl = Environment.GetEnvironmentVariable("ZEROVDI_URL");
        var envAuth = Environment.GetEnvironmentVariable("ZEROVDI_AUTH_TOKEN");
        var envRegToken = Environment.GetEnvironmentVariable("ZEROVDI_REGISTRATION_TOKEN");

        var cfg = AgentConfig.Load();
        if (!string.IsNullOrWhiteSpace(envAuth) && !string.IsNullOrWhiteSpace(envUrl))
        {
            cfg = new AgentConfig
            {
                GatewayUrl = envUrl.TrimEnd('/'),
                AuthToken = envAuth,
                ConnectorId = Environment.GetEnvironmentVariable("ZEROVDI_CONNECTOR_ID"),
            };
        }
        else if ((cfg == null || string.IsNullOrEmpty(cfg.AuthToken))
                 && !string.IsNullOrWhiteSpace(envUrl) && !string.IsNullOrWhiteSpace(envRegToken))
        {
            try { cfg = await Agent.RegisterAsync(envUrl, envRegToken); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"self-registration failed: {ex.Message}");
                return 1;
            }
        }

        if (cfg == null || string.IsNullOrEmpty(cfg.AuthToken))
        {
            Console.Error.WriteLine("not registered. Run: connector register --url <gateway-url> --token <registration-token>");
            Console.Error.WriteLine("  (or set ZEROVDI_URL + ZEROVDI_AUTH_TOKEN, or ZEROVDI_URL + ZEROVDI_REGISTRATION_TOKEN)");
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