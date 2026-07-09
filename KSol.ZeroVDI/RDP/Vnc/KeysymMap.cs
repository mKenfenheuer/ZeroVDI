using KSol.ZeroVDI.Models;

namespace KSol.ZeroVDI.RDP.Vnc;

/// <summary>
/// Maps browser RDP keyboard events (PC/AT set-1 scancodes, which are layout-INDEPENDENT physical key
/// positions) to X11 keysyms for RFB <c>KeyEvent</c>s, per the host's configured keyboard layout. The
/// browser sends only scancodes (no Unicode), so we resolve the actual character from
/// <see cref="KeyboardLayout"/>: a physical key at the US-'/' position (0x35) is '/' on US but '-' on a
/// German (QWERTZ) keyboard, so the tables differ. Non-character keys (Esc/arrows/F-keys/modifiers) are
/// layout-invariant and shared.
/// </summary>
internal sealed class KeysymMap
{
    private readonly IReadOnlyDictionary<byte, uint> _baseChars;   // unshifted character keys
    private readonly IReadOnlyDictionary<byte, uint> _shiftChars;  // shifted character keys

    private KeysymMap(IReadOnlyDictionary<byte, uint> baseChars, IReadOnlyDictionary<byte, uint> shiftChars)
    {
        _baseChars = baseChars;
        _shiftChars = shiftChars;
    }

    public static KeysymMap For(KeyboardLayout layout) => layout switch
    {
        KeyboardLayout.German => German,
        _ => Us,
    };

    /// <summary>
    /// Keysym for a scancode under this layout, honoring Shift for character keys (we send the exact
    /// shifted keysym so the host doesn't re-derive it). Returns 0 if unmapped.
    /// </summary>
    public uint ScancodeToKeysym(byte scancode, bool extended, bool shift)
    {
        if (extended) return Extended.GetValueOrDefault(scancode);
        if (SpecialBase.TryGetValue(scancode, out uint special)) return special;
        if (shift && _shiftChars.TryGetValue(scancode, out uint s)) return s;
        if (_baseChars.TryGetValue(scancode, out uint b))
        {
            // Letters: uppercase = base − 0x20 when Shift is down.
            if (shift && b is >= (uint)'a' and <= (uint)'z') return b - 0x20;
            return b;
        }
        return 0;
    }

    // ── layout-invariant keys ─────────────────────────────────────────────────────────────────────
    // Non-character keys (same for every layout): control, whitespace, modifiers, function, keypad.
    private static readonly Dictionary<byte, uint> SpecialBase = new()
    {
        [0x01] = 0xFF1B, // Esc
        [0x0E] = 0xFF08, // Backspace
        [0x0F] = 0xFF09, // Tab
        [0x1C] = 0xFF0D, // Enter
        [0x1D] = 0xFFE3, // Left Ctrl
        [0x2A] = 0xFFE1, // Left Shift
        [0x36] = 0xFFE2, // Right Shift
        [0x37] = 0xFFAA, // Keypad *
        [0x38] = 0xFFE9, // Left Alt
        [0x39] = 0x0020, // Space
        [0x3A] = 0xFFE5, // Caps Lock
        [0x3B] = 0xFFBE, [0x3C] = 0xFFBF, [0x3D] = 0xFFC0, [0x3E] = 0xFFC1, // F1-F4
        [0x3F] = 0xFFC2, [0x40] = 0xFFC3, [0x41] = 0xFFC4, [0x42] = 0xFFC5, // F5-F8
        [0x43] = 0xFFC6, [0x44] = 0xFFC7, [0x57] = 0xFFC8, [0x58] = 0xFFC9, // F9-F12
        [0x45] = 0xFF7F, // Num Lock
        [0x46] = 0xFF14, // Scroll Lock
        [0x47] = 0xFFB7, [0x48] = 0xFFB8, [0x49] = 0xFFB9, [0x4A] = 0xFFAD, // KP 7 8 9 -
        [0x4B] = 0xFFB4, [0x4C] = 0xFFB5, [0x4D] = 0xFFB6, [0x4E] = 0xFFAB, // KP 4 5 6 +
        [0x4F] = 0xFFB1, [0x50] = 0xFFB2, [0x51] = 0xFFB3, [0x52] = 0xFFB0, // KP 1 2 3 0
        [0x53] = 0xFFAE, // Keypad .
    };

    private static readonly Dictionary<byte, uint> Extended = new()
    {
        [0x1C] = 0xFF8D, // Keypad Enter
        [0x1D] = 0xFFE4, // Right Ctrl
        [0x35] = 0xFFAF, // Keypad /
        [0x38] = 0xFFEA, // Right Alt / AltGr
        [0x47] = 0xFF50, [0x48] = 0xFF52, [0x49] = 0xFF55, // Home, Up, PgUp
        [0x4B] = 0xFF51, [0x4D] = 0xFF53,                   // Left, Right
        [0x4F] = 0xFF57, [0x50] = 0xFF54, [0x51] = 0xFF56, // End, Down, PgDn
        [0x52] = 0xFF63, [0x53] = 0xFFFF,                   // Insert, Delete
        [0x5B] = 0xFFE7, [0x5C] = 0xFFE8,                   // Left/Right GUI (Meta)
    };

    // Letters (same physical positions on US & DE except Y/Z swap on German).
    private static Dictionary<byte, uint> Letters(bool german)
    {
        var d = new Dictionary<byte, uint>
        {
            [0x10] = 'q', [0x11] = 'w', [0x12] = 'e', [0x13] = 'r', [0x14] = 't',
            [0x16] = 'u', [0x17] = 'i', [0x18] = 'o', [0x19] = 'p',
            [0x1E] = 'a', [0x1F] = 's', [0x20] = 'd', [0x21] = 'f', [0x22] = 'g',
            [0x23] = 'h', [0x24] = 'j', [0x25] = 'k', [0x26] = 'l',
            [0x2C] = 'z', [0x2D] = 'x', [0x2E] = 'c', [0x2F] = 'v', [0x30] = 'b',
            [0x31] = 'n', [0x32] = 'm',
        };
        // German QWERTZ: Y and Z are swapped versus US QWERTY.
        d[0x15] = german ? 'z' : 'y'; // US-'Y' position
        d[0x2C] = german ? 'y' : 'z'; // US-'Z' position
        return d;
    }

    // ── US layout ─────────────────────────────────────────────────────────────────────────────────
    private static readonly KeysymMap Us = BuildUs();
    private static KeysymMap BuildUs()
    {
        var b = Letters(german: false);
        foreach (var (k, v) in new Dictionary<byte, uint>
        {
            [0x02] = '1', [0x03] = '2', [0x04] = '3', [0x05] = '4', [0x06] = '5',
            [0x07] = '6', [0x08] = '7', [0x09] = '8', [0x0A] = '9', [0x0B] = '0',
            [0x0C] = '-', [0x0D] = '=', [0x1A] = '[', [0x1B] = ']',
            [0x27] = ';', [0x28] = '\'', [0x29] = '`', [0x2B] = '\\',
            [0x33] = ',', [0x34] = '.', [0x35] = '/',
        }) b[k] = v;

        var s = new Dictionary<byte, uint>
        {
            [0x02] = '!', [0x03] = '@', [0x04] = '#', [0x05] = '$', [0x06] = '%',
            [0x07] = '^', [0x08] = '&', [0x09] = '*', [0x0A] = '(', [0x0B] = ')',
            [0x0C] = '_', [0x0D] = '+', [0x1A] = '{', [0x1B] = '}',
            [0x27] = ':', [0x28] = '"', [0x29] = '~', [0x2B] = '|',
            [0x33] = '<', [0x34] = '>', [0x35] = '?',
        };
        return new KeysymMap(b, s);
    }

    // ── German (QWERTZ) layout ────────────────────────────────────────────────────────────────────
    // Umlauts/eszett use Latin-1 keysyms: ä=0xE4 ö=0xF6 ü=0xFC ß=0xDF; § = 0xA7. Their shifted forms are
    // uppercase umlauts (0xC4/0xD6/0xDC) and, on the number row, the German symbols.
    private static readonly KeysymMap German = BuildGerman();
    private static KeysymMap BuildGerman()
    {
        var b = Letters(german: true);
        foreach (var (k, v) in new Dictionary<byte, uint>
        {
            [0x02] = '1', [0x03] = '2', [0x04] = '3', [0x05] = '4', [0x06] = '5',
            [0x07] = '6', [0x08] = '7', [0x09] = '8', [0x0A] = '9', [0x0B] = '0',
            [0x0C] = 0x00DF, // ß (eszett) — US-'-' position
            [0x0D] = 0x00B4, // ´ (acute accent) — US-'=' position
            [0x1A] = 0x00FC, // ü — US-'[' position
            [0x1B] = '+',    // US-']' position
            [0x27] = 0x00F6, // ö — US-';' position
            [0x28] = 0x00E4, // ä — US-'\'' position
            [0x29] = '^',    // US-'`' position (circumflex/dead key)
            [0x2B] = '#',    // US-'\\' position
            [0x33] = ',', [0x34] = '.',
            [0x35] = '-',    // US-'/' position → German minus
            [0x56] = '<',    // IntlBackslash (the extra key next to Left Shift on ISO keyboards)
        }) b[k] = v;

        var s = new Dictionary<byte, uint>
        {
            [0x02] = '!', [0x03] = '"', [0x04] = 0x00A7 /* § */, [0x05] = '$', [0x06] = '%',
            [0x07] = '&', [0x08] = '/', [0x09] = '(', [0x0A] = ')', [0x0B] = '=',
            [0x0C] = '?',           // Shift+ß → ?
            [0x0D] = '`',           // Shift+´ → `
            [0x1A] = 0x00DC,        // Ü
            [0x1B] = '*',           // Shift++ → *
            [0x27] = 0x00D6,        // Ö
            [0x28] = 0x00C4,        // Ä
            [0x29] = 0x00B0,        // ° (degree)
            [0x2B] = '\'',          // Shift+# → '
            [0x33] = ';', [0x34] = ':',
            [0x35] = '_',           // Shift+German-minus → _
            [0x56] = '>',           // Shift+IntlBackslash → >
        };
        return new KeysymMap(b, s);
    }
}
