namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// The SPICE main channel (type 1). It is the first channel connected; after auth the server sends
/// MAIN_INIT (session id + mouse modes) and, in response to ATTACH_CHANNELS, MAIN_CHANNELS_LIST — the
/// enumeration of the other channels (display, inputs, …) the client should open. The session id from
/// MAIN_INIT becomes the shared connection id every other channel links with.
/// </summary>
internal sealed class SpiceMainChannel : SpiceChannel
{
    public uint SessionId { get; private set; }
    public int CurrentMouseMode { get; private set; }
    public int SupportedMouseModes { get; private set; }

    private bool _agentConnected;
    private long _agentTokens;

    /// <summary>Raised once with the server's channel list (type, id) pairs after ATTACH_CHANNELS.</summary>
    public event Func<IReadOnlyList<(byte type, byte id)>, Task>? OnChannelsList;

    public SpiceMainChannel(IHostTransport transport, string host, int port, string? password, ILogger logger,
        Func<string?>? passwordProvider = null)
        : base(transport, host, port, password, SpiceConst.CHANNEL_MAIN, 0, 0, logger, passwordProvider) { }

    protected override async Task HandleMessageAsync(int type, byte[] data, CancellationToken ct)
    {
        switch (type)
        {
            case SpiceConst.MSG_MAIN_INIT:
            {
                var r = new SpiceReader(data);
                SessionId = r.U32();
                r.U32();                              // display_channels_hint
                SupportedMouseModes = (int)r.U32();
                CurrentMouseMode = (int)r.U32();
                uint agentConnected = r.U32();
                _agentTokens = r.U32();               // agent_tokens (then multi_media_time, ram_hint — unused)

                // Prefer client mouse mode (absolute positioning) if the server supports it.
                if (CurrentMouseMode != SpiceConst.MOUSE_MODE_CLIENT &&
                    (SupportedMouseModes & SpiceConst.MOUSE_MODE_CLIENT) != 0)
                {
                    var req = new SpiceWriter().U16(SpiceConst.MOUSE_MODE_CLIENT).U16(0);
                    await SendAsync(SpiceConst.MSGC_MAIN_MOUSE_MODE_REQUEST, req.ToArray(), ct);
                }

                // Connect the VD agent if the guest has one — needed to relay resolution changes.
                if (agentConnected != 0) await ConnectAgentAsync(ct);

                // Ask the server to enumerate its channels.
                await SendAsync(SpiceConst.MSGC_MAIN_ATTACH_CHANNELS, Array.Empty<byte>(), ct);
                return;
            }
            case SpiceConst.MSG_MAIN_MOUSE_MODE:
            {
                var r = new SpiceReader(data);
                SupportedMouseModes = r.U16();
                CurrentMouseMode = r.U16();
                return;
            }
            case SpiceConst.MSG_MAIN_AGENT_CONNECTED:
                await ConnectAgentAsync(ct);
                return;
            case SpiceConst.MSG_MAIN_AGENT_CONNECTED_TOKENS:
            {
                var r = new SpiceReader(data);
                _agentTokens = r.U32();
                await ConnectAgentAsync(ct);
                return;
            }
            case SpiceConst.MSG_MAIN_AGENT_TOKEN:
            {
                var r = new SpiceReader(data);
                _agentTokens += r.U32();
                return;
            }
            case SpiceConst.MSG_MAIN_AGENT_DISCONNECTED:
                _agentConnected = false;
                return;
            case SpiceConst.MSG_MAIN_CHANNELS_LIST:
            {
                var r = new SpiceReader(data);
                uint n = r.U32();
                var list = new List<(byte, byte)>((int)n);
                for (uint i = 0; i < n; i++)
                {
                    byte t = r.U8();
                    byte id = r.U8();
                    list.Add((t, id));
                }
                if (OnChannelsList != null) await OnChannelsList(list);
                return;
            }
            default:
                // Migration / name / uuid / other agent-data messages — not needed for the bridge.
                return;
        }
    }

    // ── VD agent ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts the VD agent and announces our capabilities (needed before MONITORS_CONFIG).</summary>
    private async Task ConnectAgentAsync(CancellationToken ct)
    {
        if (_agentConnected) return;
        _agentConnected = true;

        // MSGC_MAIN_AGENT_START: num_tokens u32 (~0 = as many as the server offers).
        var start = new SpiceWriter().U32(0xffffffff);
        await SendAsync(SpiceConst.MSGC_MAIN_AGENT_START, start.ToArray(), ct);

        // Announce capabilities (request=1) — advertise MONITORS_CONFIG so the guest accepts our resizes.
        uint caps = (1u << SpiceConst.VD_AGENT_CAP_MOUSE_STATE)
                  | (1u << SpiceConst.VD_AGENT_CAP_MONITORS_CONFIG)
                  | (1u << SpiceConst.VD_AGENT_CAP_REPLY);
        var announce = new SpiceWriter().U32(1).U32(caps); // request, caps
        await SendAgentDataAsync(SpiceConst.VD_AGENT_ANNOUNCE_CAPABILITIES, announce.ToArray(), ct);
    }

    /// <summary>Sends a VD_AGENT_MONITORS_CONFIG for a single primary monitor at (width, height).</summary>
    public async Task RequestMonitorConfigAsync(int width, int height, CancellationToken ct)
    {
        if (!_agentConnected) { Logger.LogInformation("SPICE: no VD agent — cannot resize guest to {W}x{H}", width, height); return; }
        // VDAgentMonitorsConfig: num_mon u32(=1), flags u32, then per-monitor: height u32, width u32,
        // depth u32, x u32, y u32. (Note: height precedes width on the wire.)
        var cfg = new SpiceWriter()
            .U32(1).U32(0)                       // num_mon, flags (auto position)
            .U32(height).U32(width).U32(32)      // height, width, depth
            .U32(0).U32(0);                      // x, y
        await SendAgentDataAsync(SpiceConst.VD_AGENT_MONITORS_CONFIG, cfg.ToArray(), ct);
        Logger.LogInformation("SPICE: requested guest resize to {W}x{H}", width, height);
    }

    /// <summary>
    /// Wraps a VD-agent message in MSGC_MAIN_AGENT_DATA (protocol u32, type u32, opaque u64, size u32,
    /// payload) and sends it on the main channel. Payloads here are well under one agent chunk.
    /// </summary>
    private Task SendAgentDataAsync(int agentType, byte[] payload, CancellationToken ct)
    {
        var w = new SpiceWriter()
            .U32(SpiceConst.VD_AGENT_PROTOCOL).U32(agentType).U32(0).U32(0) // protocol, type, opaque(u64 lo/hi)
            .U32(payload.Length)
            .Bytes(payload);
        return SendAsync(SpiceConst.MSGC_MAIN_AGENT_DATA, w.ToArray(), ct);
    }
}
