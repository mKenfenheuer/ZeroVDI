using KSol.ZeroVDI.RDP.Bridge;

namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// A SPICE <see cref="IProtocolSource"/>: connects the SPICE main channel, opens the display + inputs
/// channels the server enumerates, and presents the desktop as BGRX rectangles to the shared
/// <see cref="RdpEncoderSession"/> — exactly the seam the VNC bridge uses, so no RDP knowledge is needed.
///
///   SpiceClient (IProtocolSource) → RdpEncoderSession (shared) → DuplexPipeStream → RdpRelaySession → web
///
/// SPICE opens one TCP connection per channel and authenticates each with an RSA-OAEP ticket; the main
/// channel's session id ties them together. Display draws are composited into a primary-surface
/// framebuffer inside <see cref="SpiceDisplayChannel"/>, which raises <see cref="OnRectangle"/>.
/// </summary>
internal sealed class SpiceClient : IProtocolSource
{
    private readonly IHostTransport _transport;
    private readonly string _host;
    private readonly int _port;
    private readonly string? _password;
    private readonly ILogger _logger;

    private SpiceMainChannel? _main;
    private SpiceDisplayChannel? _display;
    private SpiceInputsChannel? _inputs;
    private readonly List<Task> _channelLoops = new();
    private readonly TaskCompletionSource _geometryReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _runCts;

    private int _width, _height;

    public int Width => _width;
    public int Height => _height;

    public event Action<int, int, int, int, byte[]>? OnRectangle;

    /// <summary>Raised when the guest re-creates its primary surface at a new size (post-resize).</summary>
    public event Action<int, int>? OnGeometryChanged;

    // SPICE takes AT set-1 scancodes directly — no keysym round-trip (see IProtocolSource).
    public bool PrefersScancodes => true;

    // For the Proxmox spiceproxy path each channel rides a freshly-fetched single-use ticket, so the SPICE
    // auth password is read from the transport AFTER each channel dials (see SpiceProxyTransport). Null for
    // direct SPICE, where the static _password is used for every channel.
    private readonly Func<string?>? _passwordProvider;

    public SpiceClient(IHostTransport transport, string host, int port, string? user, string? password,
        ILogger logger, Func<string?>? passwordProvider = null)
    {
        _transport = transport; _host = host; _port = port; _password = password; _logger = logger;
        _passwordProvider = passwordProvider;
        _ = user; // SPICE ticket auth is password-only
    }

    /// <summary>
    /// Connects the main channel, drives it until the primary display surface geometry is known, and opens
    /// the display + inputs channels. Returns once <see cref="Width"/>/<see cref="Height"/> are valid so the
    /// encoder can size the session (mirrors RfbClient.ConnectAsync surfacing failures up front).
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        _logger.LogInformation("SPICE: connecting main channel to {Host}:{Port}", _host, _port);
        _main = new SpiceMainChannel(_transport, _host, _port, _password, _logger, _passwordProvider);
        await _main.ConnectAsync(ct);

        _main.OnChannelsList += OnChannelsListAsync;

        // Start the main channel loop; it fires MAIN_INIT → ATTACH_CHANNELS → MAIN_CHANNELS_LIST, which
        // opens the display/inputs channels. We wait until the display reports geometry.
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _channelLoops.Add(RunChannelAsync(_main, _runCts.Token));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _geometryReady.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RdpHostConnection.ConnectException("SPICE: timed out waiting for display surface");
        }
    }

    private async Task OnChannelsListAsync(IReadOnlyList<(byte type, byte id)> channels)
    {
        var ct = _runCts?.Token ?? CancellationToken.None;
        foreach (var (type, id) in channels)
        {
            if (type == SpiceConst.CHANNEL_DISPLAY && _display == null)
            {
                _display = new SpiceDisplayChannel(_transport, _host, _port, _password, _main!.SessionId, id, _logger, _passwordProvider);
                _display.OnGeometry += (w, h) =>
                {
                    bool first = !_geometryReady.Task.IsCompleted;
                    _width = w; _height = h;
                    _geometryReady.TrySetResult();
                    // A surface re-create AFTER the initial one is a guest resize — tell the encoder so it
                    // rebuilds its framebuffer + output surface to the new size (1:1).
                    if (!first) OnGeometryChanged?.Invoke(w, h);
                };
                _display.OnRectangle += (x, y, w, h, px) => OnRectangle?.Invoke(x, y, w, h, px);
                await _display.ConnectAsync(ct);
                await _display.SendDisplayInitAsync(ct);
                _channelLoops.Add(RunChannelAsync(_display, ct));
            }
            else if (type == SpiceConst.CHANNEL_INPUTS && _inputs == null)
            {
                _inputs = new SpiceInputsChannel(_transport, _host, _port, _password, _main!.SessionId, id, _logger, _passwordProvider);
                await _inputs.ConnectAsync(ct);
                _channelLoops.Add(RunChannelAsync(_inputs, ct));
            }
            // cursor / playback channels are not bridged
        }
    }

    private async Task RunChannelAsync(SpiceChannel ch, CancellationToken ct)
    {
        try { await ch.RunAsync(ct); }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { _logger.LogInformation("SPICE: channel {Type} closed", ch.ChannelType); }
        catch (Exception ex) { _logger.LogWarning(ex, "SPICE: channel {Type} loop error", ch.ChannelType); }
    }

    /// <summary>The channel loops already run from ConnectAsync; just await them until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { _runCts?.Cancel(); } catch { } });
        try { await Task.WhenAll(_channelLoops); }
        catch (OperationCanceledException) { }
    }

    // SPICE streams changes proactively (no explicit full/incremental refresh request like RFB).
    public Task RequestFullFrameAsync(CancellationToken ct) => Task.CompletedTask;
    public Task RequestIncrementalAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Relay the RDP client's desired size to the guest via the SPICE VD agent (MONITORS_CONFIG). If the
    /// guest agent is present it resizes and re-creates its primary surface (→ OnGeometryChanged); if not,
    /// this is a no-op and the encoder keeps letterbox-scaling. Skips a redundant request at the current size.
    /// </summary>
    public Task RequestResizeAsync(int width, int height, CancellationToken ct)
    {
        if (_main == null || width <= 0 || height <= 0) return Task.CompletedTask;
        if (width == _width && height == _height) return Task.CompletedTask;
        return _main.RequestMonitorConfigAsync(width, height, ct);
    }

    public Task PointerAsync(int x, int y, int buttonMask, CancellationToken ct)
        => _inputs?.PointerAsync(x, y, buttonMask, ct) ?? Task.CompletedTask;

    // Keysym path is unused (PrefersScancodes = true), but satisfy the interface.
    public Task KeyAsync(uint keysym, bool down, CancellationToken ct) => Task.CompletedTask;

    public Task KeyScancodeAsync(byte scancode, bool extended, bool down, CancellationToken ct)
        => _inputs?.KeyScancodeAsync(scancode, extended, down, ct) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try { _runCts?.Cancel(); } catch { }
        if (_display != null) await _display.DisposeAsync();
        if (_inputs != null) await _inputs.DisposeAsync();
        if (_main != null) await _main.DisposeAsync();
        _runCts?.Dispose();
    }
}
