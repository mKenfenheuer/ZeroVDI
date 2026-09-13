// Fast-path keyboard input events ([MS-RDPBCGR] 2.2.8.1.2.2.1 scancode, 2.2.8.1.2.2.2 Unicode,
// 2.2.8.1.2.2.5 sync). The eventHeader packs a 3-bit eventCode in the high bits and 5 bits of
// event flags in the low bits.
const FASTPATH_INPUT_KBDFLAGS_RELEASE = 0x01;
// E0 prefix: the key lives in a grey cluster (arrows, nav block, right Ctrl/Alt, keypad Enter and "/")
// and shares its make code with a keypad key.
const FASTPATH_INPUT_KBDFLAGS_EXTENDED = 0x02;
// E1 prefix: used only by the PAUSE key, which the spec requires be sent as a Ctrl+NumLock pair.
const FASTPATH_INPUT_KBDFLAGS_EXTENDED1 = 0x04;

// Toggle-key state flags for the sync event ([MS-RDPBCGR] 2.2.8.1.2.2.5).
const TS_SYNC_SCROLL_LOCK = 0x01;
const TS_SYNC_NUM_LOCK = 0x02;
const TS_SYNC_CAPS_LOCK = 0x04;
const TS_SYNC_KANA_LOCK = 0x08;

// ---- scancode events --------------------------------------------------------------------------
// Low-level: an explicit make code plus flags. The DOM-code wrappers below are what client.js uses
// for real keystrokes; the raw form exists for synthesised sequences (Ctrl+Alt+Del, PAUSE).
function ScancodeEvent(scanCode, flags) {
    this.scanCode = scanCode;
    this.flags = flags | 0;
}

ScancodeEvent.prototype.serialize = function () {
    // 2 bytes on the wire: eventHeader + keyCode. (Sending a third, zero byte — as this used to —
    // appends a stray FASTPATH_INPUT_EVENT_SCANCODE header that strict hosts can trip over.)
    const data = new ArrayBuffer(2);
    const w = new BinaryWriter(data);
    w.uint8((this.flags & 0x1f) | ((FASTPATH_INPUT_EVENT_SCANCODE & 0x7) << 5));
    w.uint8(this.scanCode & 0xff);
    return data;
};

function _scanFlagsFor(code, release) {
    let flags = release ? FASTPATH_INPUT_KBDFLAGS_RELEASE : 0;
    if (KeyExtended.has(code)) flags |= FASTPATH_INPUT_KBDFLAGS_EXTENDED;
    return flags;
}

function KeyboardEventKeyDown(code) {
    this.scanCode = KeyMap[code];
    this.flags = _scanFlagsFor(code, false);
}
KeyboardEventKeyDown.prototype.serialize = ScancodeEvent.prototype.serialize;

function KeyboardEventKeyUp(code) {
    this.scanCode = KeyMap[code];
    this.flags = _scanFlagsFor(code, true);
}
KeyboardEventKeyUp.prototype.serialize = ScancodeEvent.prototype.serialize;

// PAUSE has no single make code. [MS-RDPBCGR] 2.2.8.1.2.2.1 mandates this exact four-event
// sequence, with EXTENDED1 on the two Ctrl events.
function pauseKeySequence() {
    const CTRL = 0x1D, NUMLOCK = 0x45, E1 = FASTPATH_INPUT_KBDFLAGS_EXTENDED1;
    return [
        new ScancodeEvent(CTRL, E1),
        new ScancodeEvent(NUMLOCK, 0),
        new ScancodeEvent(CTRL, E1 | FASTPATH_INPUT_KBDFLAGS_RELEASE),
        new ScancodeEvent(NUMLOCK, FASTPATH_INPUT_KBDFLAGS_RELEASE),
    ];
}

// ---- Unicode events ---------------------------------------------------------------------------
// Carries a character the host should insert directly, bypassing scancode→layout translation. Used
// for keys we have no physical position for (on-screen/soft keyboards, exotic layouts) and for
// injecting text. unicodeCode is 16 bits, so astral characters are sent as their surrogate pair.
function UnicodeKeyEvent(unicodeCode, release) {
    this.unicodeCode = unicodeCode & 0xffff;
    this.flags = release ? FASTPATH_INPUT_KBDFLAGS_RELEASE : 0;
}

UnicodeKeyEvent.prototype.serialize = function () {
    const data = new ArrayBuffer(3);
    const w = new BinaryWriter(data);
    w.uint8((this.flags & 0x1f) | ((FASTPATH_INPUT_EVENT_UNICODE & 0x7) << 5));
    w.uint16(this.unicodeCode, true);
    return data;
};

// ---- sync event -------------------------------------------------------------------------------
// Tells the host the true state of the lock keys and resets its idea of which keys are down. Sent on
// activation and whenever we notice the local lock state drifted from what the host last heard —
// without it a Caps Lock toggled outside the session leaves the remote shouting.
function SyncEvent(toggleFlags) {
    this.toggleFlags = toggleFlags | 0;
}

SyncEvent.prototype.serialize = function () {
    const data = new ArrayBuffer(1);
    const w = new BinaryWriter(data);
    w.uint8((this.toggleFlags & 0x1f) | ((FASTPATH_INPUT_EVENT_SYNC & 0x7) << 5));
    return data;
};
