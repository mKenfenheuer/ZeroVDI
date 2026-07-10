namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// The SPICE inputs channel (type 3): forwards mouse and keyboard from the browser into the host.
/// The browser's RDP scancodes are AT set-1 — the exact set SPICE KEY_DOWN/KEY_UP expect — so key events
/// pass through without a keysym round-trip (see <see cref="IProtocolSource.PrefersScancodes"/>).
///
/// Mouse uses client (absolute) mode: MOUSE_POSITION carries absolute host coordinates; buttons are
/// PRESS/RELEASE with a running button-state mask. Coordinates arrive already mapped into the source's
/// native space by the encoder.
/// </summary>
internal sealed class SpiceInputsChannel : SpiceChannel
{
    private int _buttonsState;   // SPICE button mask (bit0 L, bit1 M, bit2 R)
    private int _lastX, _lastY;

    public SpiceInputsChannel(IHostTransport transport, string host, int port, string? password,
        uint connectionId, byte channelId, ILogger logger, Func<string?>? passwordProvider = null)
        : base(transport, host, port, password, SpiceConst.CHANNEL_INPUTS, channelId, connectionId, logger, passwordProvider) { }

    protected override Task HandleMessageAsync(int type, byte[] data, CancellationToken ct)
    {
        // INPUTS_INIT / KEY_MODIFIERS / MOUSE_MOTION_ACK — nothing the bridge must act on.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Absolute pointer update. <paramref name="rdpButtonMask"/> uses the IProtocolSource convention
    /// (bit0=L, bit1=M, bit2=R, bit3=wheel-up, bit4=wheel-down). Wheel bits are turned into momentary
    /// PRESS/RELEASE of the SPICE wheel buttons; L/M/R changes emit PRESS/RELEASE and update the mask.
    /// </summary>
    public async Task PointerAsync(int x, int y, int rdpButtonMask, CancellationToken ct)
    {
        _lastX = x; _lastY = y;

        // Left/Middle/Right transitions.
        await ApplyButtonAsync(rdpButtonMask, 0, SpiceConst.MOUSE_BUTTON_LEFT, SpiceConst.MOUSE_BUTTON_MASK_LEFT, ct);
        await ApplyButtonAsync(rdpButtonMask, 1, SpiceConst.MOUSE_BUTTON_MIDDLE, SpiceConst.MOUSE_BUTTON_MASK_MIDDLE, ct);
        await ApplyButtonAsync(rdpButtonMask, 2, SpiceConst.MOUSE_BUTTON_RIGHT, SpiceConst.MOUSE_BUTTON_MASK_RIGHT, ct);

        // Position (client/absolute mode): x u32, y u32, buttons_state u16, display_id u8.
        var w = new SpiceWriter().U32(x).U32(y).U16(_buttonsState).U8(0);
        await SendAsync(SpiceConst.MSGC_INPUTS_MOUSE_POSITION, w.ToArray(), ct);

        // Wheel: momentary press+release of the up/down buttons.
        if ((rdpButtonMask & (1 << 3)) != 0) await WheelAsync(SpiceConst.MOUSE_BUTTON_UP, ct);
        if ((rdpButtonMask & (1 << 4)) != 0) await WheelAsync(SpiceConst.MOUSE_BUTTON_DOWN, ct);
    }

    private async Task ApplyButtonAsync(int mask, int rdpBit, int spiceButton, int spiceMaskBit, CancellationToken ct)
    {
        bool wantDown = (mask & (1 << rdpBit)) != 0;
        bool isDown = (_buttonsState & spiceMaskBit) != 0;
        if (wantDown == isDown) return;

        if (wantDown)
        {
            _buttonsState |= spiceMaskBit;
            var w = new SpiceWriter().U8(spiceButton).U16(_buttonsState);
            await SendAsync(SpiceConst.MSGC_INPUTS_MOUSE_PRESS, w.ToArray(), ct);
        }
        else
        {
            _buttonsState &= ~spiceMaskBit;
            var w = new SpiceWriter().U8(spiceButton).U16(_buttonsState);
            await SendAsync(SpiceConst.MSGC_INPUTS_MOUSE_RELEASE, w.ToArray(), ct);
        }
    }

    private async Task WheelAsync(int button, CancellationToken ct)
    {
        var press = new SpiceWriter().U8(button).U16(_buttonsState);
        await SendAsync(SpiceConst.MSGC_INPUTS_MOUSE_PRESS, press.ToArray(), ct);
        var release = new SpiceWriter().U8(button).U16(_buttonsState);
        await SendAsync(SpiceConst.MSGC_INPUTS_MOUSE_RELEASE, release.ToArray(), ct);
    }

    /// <summary>
    /// Sends an AT set-1 key event. <paramref name="scancode"/> is the RDP scancode; <paramref name="extended"/>
    /// marks the 0xE0-prefixed keys. SPICE packs the scancode into a u32: single-byte down = sc, up = sc|0x80;
    /// extended down = 0xe000|sc, up = 0xe080|sc.
    /// </summary>
    public async Task KeyScancodeAsync(byte scancode, bool extended, bool down, CancellationToken ct)
    {
        uint code;
        if (extended)
            code = (uint)(0xe000 | scancode | (down ? 0 : 0x80));
        else
            code = (uint)(scancode | (down ? 0 : 0x80));

        var w = new SpiceWriter().U32(code);
        await SendAsync(down ? SpiceConst.MSGC_INPUTS_KEY_DOWN : SpiceConst.MSGC_INPUTS_KEY_UP, w.ToArray(), ct);
    }
}
