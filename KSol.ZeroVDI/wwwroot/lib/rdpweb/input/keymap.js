// Maps a DOM KeyboardEvent.code (a PHYSICAL key position, layout-independent) to an IBM PC
// "scancode set 1" make code — exactly what an RDP host expects in a TS_FP_KEYBOARD_EVENT
// ([MS-RDPBCGR] 2.2.8.1.2.2.1). The host then runs the scancode through the keyboard layout we
// advertised at connect (see client.js _detectKeyboardLayout), which is why we must send POSITIONS
// and never characters: on a German layout the physical "KeyY" key must arrive as 0x15 so the host
// produces "z".
//
// Keys in the grey clusters (arrows, navigation block, right Ctrl/Alt, the keypad's Enter and "/")
// share their make code with a keypad key and are distinguished on the wire by an E0 prefix, which
// fast-path carries as the FASTPATH_INPUT_KBDFLAGS_EXTENDED (0x02) event flag. KeyExtended lists
// them; sending an arrow key without it is what made the host read Numpad5/Numpad2 instead (the
// remote cursor then moved only while Num Lock happened to be off).
const KeyMap = {
    // ---- main alphanumeric block -----------------------------------------------------------------
    "Escape": 0x01,
    "Digit1": 0x02, "Digit2": 0x03, "Digit3": 0x04, "Digit4": 0x05, "Digit5": 0x06,
    "Digit6": 0x07, "Digit7": 0x08, "Digit8": 0x09, "Digit9": 0x0A, "Digit0": 0x0B,
    "Minus": 0x0C, "Equal": 0x0D, "Backspace": 0x0E, "Tab": 0x0F,
    "KeyQ": 0x10, "KeyW": 0x11, "KeyE": 0x12, "KeyR": 0x13, "KeyT": 0x14,
    "KeyY": 0x15, "KeyU": 0x16, "KeyI": 0x17, "KeyO": 0x18, "KeyP": 0x19,
    "BracketLeft": 0x1A, "BracketRight": 0x1B, "Enter": 0x1C, "ControlLeft": 0x1D,
    "KeyA": 0x1E, "KeyS": 0x1F, "KeyD": 0x20, "KeyF": 0x21, "KeyG": 0x22,
    "KeyH": 0x23, "KeyJ": 0x24, "KeyK": 0x25, "KeyL": 0x26,
    "Semicolon": 0x27, "Quote": 0x28, "Backquote": 0x29, "ShiftLeft": 0x2A, "Backslash": 0x2B,
    "KeyZ": 0x2C, "KeyX": 0x2D, "KeyC": 0x2E, "KeyV": 0x2F, "KeyB": 0x30,
    "KeyN": 0x31, "KeyM": 0x32,
    "Comma": 0x33, "Period": 0x34, "Slash": 0x35, "ShiftRight": 0x36,
    "AltLeft": 0x38, "Space": 0x39, "CapsLock": 0x3A,

    // ---- function keys ---------------------------------------------------------------------------
    "F1": 0x3B, "F2": 0x3C, "F3": 0x3D, "F4": 0x3E, "F5": 0x3F, "F6": 0x40,
    "F7": 0x41, "F8": 0x42, "F9": 0x43, "F10": 0x44, "F11": 0x57, "F12": 0x58,
    // Extra function row on full-size / Sun / Apple keyboards.
    "F13": 0x64, "F14": 0x65, "F15": 0x66, "F16": 0x67, "F17": 0x68, "F18": 0x69,
    "F19": 0x6A, "F20": 0x6B, "F21": 0x6C, "F22": 0x6D, "F23": 0x6E, "F24": 0x76,

    // ---- lock / system keys ----------------------------------------------------------------------
    "NumLock": 0x45,
    "ScrollLock": 0x46,
    // PrintScreen is the extended 0x37 (a bare 0x37 is the keypad "*").
    "PrintScreen": 0x37,
    // The 102-key "<>|" key next to the left Shift (absent on ANSI keyboards).
    "IntlBackslash": 0x56,

    // ---- numeric keypad --------------------------------------------------------------------------
    "NumpadMultiply": 0x37, "NumpadSubtract": 0x4A, "NumpadAdd": 0x4E,
    "Numpad7": 0x47, "Numpad8": 0x48, "Numpad9": 0x49,
    "Numpad4": 0x4B, "Numpad5": 0x4C, "Numpad6": 0x4D,
    "Numpad1": 0x4F, "Numpad2": 0x50, "Numpad3": 0x51,
    "Numpad0": 0x52, "NumpadDecimal": 0x53, "NumpadEqual": 0x59,
    "NumpadComma": 0x7E,          // ABNT (Brazilian) keypad separator
    "NumpadEnter": 0x1C,          // extended
    "NumpadDivide": 0x35,         // extended

    // ---- grey cluster: navigation + arrows (all extended) -----------------------------------------
    "Insert": 0x52, "Delete": 0x53,
    "Home": 0x47, "End": 0x4F,
    "PageUp": 0x49, "PageDown": 0x51,
    "ArrowUp": 0x48, "ArrowLeft": 0x4B, "ArrowRight": 0x4D, "ArrowDown": 0x50,

    // ---- right-hand modifiers (extended) ----------------------------------------------------------
    "ControlRight": 0x1D,
    "AltRight": 0x38,             // AltGr on European layouts (host expands it to Ctrl+Alt)
    "MetaLeft": 0x5B, "MetaRight": 0x5C,   // Windows / Command key
    "OSLeft": 0x5B, "OSRight": 0x5C,       // legacy Gecko spelling
    "ContextMenu": 0x5D,

    // ---- Japanese / Korean keyboards ---------------------------------------------------------------
    "IntlRo": 0x73, "IntlYen": 0x7D,
    "KanaMode": 0x70, "Convert": 0x79, "NonConvert": 0x7B,
    "Lang1": 0xF2,                // Hangul / Kana toggle
    "Lang2": 0xF1,                // Hanja / Eisu

    // ---- ACPI + multimedia keys (extended); most never reach the page, but cost nothing ------------
    "Power": 0x5E, "Sleep": 0x5F, "WakeUp": 0x63,
    "AudioVolumeMute": 0x20, "AudioVolumeDown": 0x2E, "AudioVolumeUp": 0x30,
    "MediaTrackNext": 0x19, "MediaTrackPrevious": 0x10,
    "MediaStop": 0x24, "MediaPlayPause": 0x22,
    "LaunchMail": 0x6C, "LaunchMediaPlayer": 0x6D, "LaunchApp1": 0x6B, "LaunchApp2": 0x21,
    "BrowserSearch": 0x65, "BrowserFavorites": 0x66, "BrowserRefresh": 0x67,
    "BrowserStop": 0x68, "BrowserForward": 0x69, "BrowserBack": 0x6A,
};

// Keys that MUST carry the E0 prefix (FASTPATH_INPUT_KBDFLAGS_EXTENDED). Everything here shares a
// make code with a keypad/main-block key above.
const KeyExtended = new Set([
    "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
    "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
    "ControlRight", "AltRight", "MetaLeft", "MetaRight", "OSLeft", "OSRight", "ContextMenu",
    "NumpadEnter", "NumpadDivide", "PrintScreen",
    "Power", "Sleep", "WakeUp",
    "AudioVolumeMute", "AudioVolumeDown", "AudioVolumeUp",
    "MediaTrackNext", "MediaTrackPrevious", "MediaStop", "MediaPlayPause",
    "LaunchMail", "LaunchMediaPlayer", "LaunchApp1", "LaunchApp2",
    "BrowserSearch", "BrowserFavorites", "BrowserRefresh", "BrowserStop",
    "BrowserForward", "BrowserBack",
]);

// Modifier positions, used to decide what may stay held across a focus loss and which keys macOS
// silently swallows the keyup for while Command is down.
const KeyIsModifier = new Set([
    "ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight",
    "AltLeft", "AltRight", "MetaLeft", "MetaRight", "OSLeft", "OSRight", "CapsLock",
]);
