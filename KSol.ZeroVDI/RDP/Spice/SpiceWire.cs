namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// SPICE protocol constants (from spice.proto / the spice-html5 enums). Only the subset the bridge uses.
/// </summary>
internal static class SpiceConst
{
    // Magic is the ASCII "REDQ" as a little-endian u32 on the wire (bytes R,E,D,Q).
    public static readonly byte[] Magic = { (byte)'R', (byte)'E', (byte)'D', (byte)'Q' };
    public const uint VersionMajor = 2;
    public const uint VersionMinor = 2;

    // Common capabilities (SpiceLinkMess.common_caps bit indices).
    public const int CAP_PROTOCOL_AUTH_SELECTION = 0;
    public const int CAP_AUTH_SPICE = 1;
    public const int CAP_AUTH_SASL = 2;
    public const int CAP_MINI_HEADER = 3;

    // Link errors.
    public const uint LINK_ERR_OK = 0;
    public const uint LINK_ERR_PERMISSION_DENIED = 7;

    // Auth mechanisms (matches CAP_AUTH_SPICE = 1).
    public const uint AUTH_SPICE = 1;

    // RSA public key = 128-byte 1024-bit modulus wrapped in a 162-byte X.509 SubjectPublicKeyInfo.
    public const int TicketPubkeyBytes = 1024 / 8 + 34; // 162
    public const int TicketKeyBytes = 1024 / 8;         // 128 (encrypted-ticket length)

    // Channel types.
    public const byte CHANNEL_MAIN = 1;
    public const byte CHANNEL_DISPLAY = 2;
    public const byte CHANNEL_INPUTS = 3;
    public const byte CHANNEL_CURSOR = 4;
    public const byte CHANNEL_PLAYBACK = 5;

    // Common server→client messages.
    public const int MSG_MIGRATE = 1;
    public const int MSG_SET_ACK = 3;
    public const int MSG_PING = 4;
    public const int MSG_DISCONNECTING = 6;
    public const int MSG_NOTIFY = 7;

    // Common client→server messages.
    public const int MSGC_ACK_SYNC = 1;
    public const int MSGC_ACK = 2;
    public const int MSGC_PONG = 3;

    // Main channel.
    public const int MSG_MAIN_INIT = 103;
    public const int MSG_MAIN_CHANNELS_LIST = 104;
    public const int MSG_MAIN_MOUSE_MODE = 105;
    public const int MSG_MAIN_AGENT_CONNECTED = 107;
    public const int MSG_MAIN_AGENT_DISCONNECTED = 108;
    public const int MSG_MAIN_AGENT_DATA = 109;
    public const int MSG_MAIN_AGENT_TOKEN = 110;
    public const int MSG_MAIN_AGENT_CONNECTED_TOKENS = 115;
    public const int MSGC_MAIN_ATTACH_CHANNELS = 104;
    public const int MSGC_MAIN_MOUSE_MODE_REQUEST = 105;
    public const int MSGC_MAIN_AGENT_START = 106;
    public const int MSGC_MAIN_AGENT_DATA = 107;

    // VD agent (rides main-channel AGENT_DATA). Protocol version + message types.
    public const uint VD_AGENT_PROTOCOL = 1;
    public const int VD_AGENT_MONITORS_CONFIG = 2;
    public const int VD_AGENT_ANNOUNCE_CAPABILITIES = 6;
    // Capability bits announced to the agent.
    public const int VD_AGENT_CAP_MOUSE_STATE = 0;
    public const int VD_AGENT_CAP_MONITORS_CONFIG = 1;
    public const int VD_AGENT_CAP_REPLY = 2;
    // MONITORS_CONFIG flags: use the given position (else auto).
    public const uint VD_AGENT_CONFIG_MONITORS_FLAG_USE_POS = 1 << 0;

    public const int MOUSE_MODE_SERVER = 1 << 0;
    public const int MOUSE_MODE_CLIENT = 1 << 1;

    // Display channel.
    public const int MSGC_DISPLAY_INIT = 101;
    public const int MSG_DISPLAY_RESET = 103;
    public const int MSG_DISPLAY_DRAW_FILL = 302;
    public const int MSG_DISPLAY_DRAW_OPAQUE = 303;
    public const int MSG_DISPLAY_DRAW_COPY = 304;
    public const int MSG_DISPLAY_DRAW_BLEND = 305;
    public const int MSG_DISPLAY_COPY_BITS = 104;
    public const int MSG_DISPLAY_SURFACE_CREATE = 314;
    public const int MSG_DISPLAY_SURFACE_DESTROY = 315;
    public const int MSG_DISPLAY_STREAM_CREATE = 122;
    public const int MSG_DISPLAY_STREAM_DATA = 123;
    public const int MSG_DISPLAY_STREAM_DATA_SIZED = 316;
    public const int MSG_DISPLAY_STREAM_CLIP = 124;
    public const int MSG_DISPLAY_STREAM_DESTROY = 125;
    public const int MSG_DISPLAY_STREAM_DESTROY_ALL = 126;
    public const int MSG_DISPLAY_MONITORS_CONFIG = 317;
    public const int MSG_DISPLAY_INVAL_ALL_PALETTES = 108;

    // Image types (SpiceImageDescriptor.type).
    public const int IMAGE_TYPE_BITMAP = 0;
    public const int IMAGE_TYPE_QUIC = 1;
    public const int IMAGE_TYPE_LZ_PLT = 100;
    public const int IMAGE_TYPE_LZ_RGB = 101;
    public const int IMAGE_TYPE_GLZ_RGB = 102;
    public const int IMAGE_TYPE_FROM_CACHE = 103;
    public const int IMAGE_TYPE_SURFACE = 104;
    public const int IMAGE_TYPE_JPEG = 105;
    public const int IMAGE_TYPE_FROM_CACHE_LOSSLESS = 106;
    public const int IMAGE_TYPE_ZLIB_GLZ_RGB = 107;
    public const int IMAGE_TYPE_JPEG_ALPHA = 108;

    public const int IMAGE_FLAGS_CACHE_ME = 1 << 0;

    // Bitmap formats.
    public const int BITMAP_FMT_INVALID = 0;
    public const int BITMAP_FMT_8BIT = 5;
    public const int BITMAP_FMT_16BIT = 6;
    public const int BITMAP_FMT_24BIT = 7;
    public const int BITMAP_FMT_32BIT = 8;
    public const int BITMAP_FMT_RGBA = 9;
    public const int BITMAP_FLAGS_TOP_DOWN = 1 << 2;

    // LZ image sub-types (inside the LZ_RGB stream header).
    public const int LZ_IMAGE_TYPE_RGB16 = 6;
    public const int LZ_IMAGE_TYPE_RGB24 = 7;
    public const int LZ_IMAGE_TYPE_RGB32 = 8;
    public const int LZ_IMAGE_TYPE_RGBA = 9;
    public const int LZ_IMAGE_TYPE_XXXA = 10;

    // Surface formats.
    public const int SURFACE_FMT_32_xRGB = 32;
    public const int SURFACE_FMT_32_ARGB = 96;
    public const int SURFACE_FLAGS_PRIMARY = 1 << 0;

    // ROP descriptor.
    public const int ROPD_OP_PUT = 1 << 3;

    public const int CLIP_TYPE_NONE = 0;
    public const int CLIP_TYPE_RECTS = 1;

    // Inputs channel.
    public const int MSG_INPUTS_INIT = 101;
    public const int MSG_INPUTS_KEY_MODIFIERS = 102;
    public const int MSG_INPUTS_MOUSE_MOTION_ACK = 111;
    public const int MSGC_INPUTS_KEY_DOWN = 101;
    public const int MSGC_INPUTS_KEY_UP = 102;
    public const int MSGC_INPUTS_MOUSE_MOTION = 111;
    public const int MSGC_INPUTS_MOUSE_POSITION = 112;
    public const int MSGC_INPUTS_MOUSE_PRESS = 113;
    public const int MSGC_INPUTS_MOUSE_RELEASE = 114;

    public const int MOUSE_BUTTON_LEFT = 1;
    public const int MOUSE_BUTTON_MIDDLE = 2;
    public const int MOUSE_BUTTON_RIGHT = 3;
    public const int MOUSE_BUTTON_UP = 4;
    public const int MOUSE_BUTTON_DOWN = 5;
    public const int MOUSE_BUTTON_MASK_LEFT = 1 << 0;
    public const int MOUSE_BUTTON_MASK_MIDDLE = 1 << 1;
    public const int MOUSE_BUTTON_MASK_RIGHT = 1 << 2;

    public const int VIDEO_CODEC_TYPE_MJPEG = 1;
    public const int VIDEO_CODEC_TYPE_VP8 = 2;
}

/// <summary>
/// A little-endian byte reader over a buffer, mirroring the SPICE wire (all multi-byte fields are LE).
/// </summary>
internal struct SpiceReader
{
    private readonly byte[] _b;
    private int _at;

    public SpiceReader(byte[] buffer, int at = 0) { _b = buffer; _at = at; }

    public int Pos => _at;
    public int Remaining => _b.Length - _at;
    public void Seek(int at) => _at = at;
    public void Skip(int n) => _at += n;

    public byte U8() => _b[_at++];
    public ushort U16()
    {
        ushort v = (ushort)(_b[_at] | (_b[_at + 1] << 8));
        _at += 2; return v;
    }
    public uint U32()
    {
        uint v = (uint)(_b[_at] | (_b[_at + 1] << 8) | (_b[_at + 2] << 16) | (_b[_at + 3] << 24));
        _at += 4; return v;
    }
    public int I32() => unchecked((int)U32());
    public ulong U64()
    {
        ulong lo = U32(), hi = U32();
        return lo | (hi << 32);
    }
    public byte[] Bytes(int n)
    {
        var r = new byte[n];
        Array.Copy(_b, _at, r, 0, n);
        _at += n;
        return r;
    }
    public ReadOnlySpan<byte> Span(int n)
    {
        var s = _b.AsSpan(_at, n);
        _at += n;
        return s;
    }
}

/// <summary>A growable little-endian byte writer for building SPICE client messages.</summary>
internal sealed class SpiceWriter
{
    private byte[] _b = new byte[64];
    private int _len;

    public int Length => _len;

    private void Ensure(int extra)
    {
        if (_len + extra <= _b.Length) return;
        int cap = _b.Length * 2;
        while (cap < _len + extra) cap *= 2;
        Array.Resize(ref _b, cap);
    }

    public SpiceWriter U8(int v) { Ensure(1); _b[_len++] = (byte)v; return this; }
    public SpiceWriter U16(int v) { Ensure(2); _b[_len++] = (byte)v; _b[_len++] = (byte)(v >> 8); return this; }
    public SpiceWriter U32(long v) { Ensure(4); _b[_len++] = (byte)v; _b[_len++] = (byte)(v >> 8); _b[_len++] = (byte)(v >> 16); _b[_len++] = (byte)(v >> 24); return this; }
    public SpiceWriter Bytes(ReadOnlySpan<byte> s) { Ensure(s.Length); s.CopyTo(_b.AsSpan(_len)); _len += s.Length; return this; }
    public SpiceWriter Zeros(int n) { Ensure(n); _len += n; return this; }

    public byte[] ToArray()
    {
        var r = new byte[_len];
        Array.Copy(_b, r, _len);
        return r;
    }
}
