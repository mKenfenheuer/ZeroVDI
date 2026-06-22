// Mppc — MPPC (RDP4/RDP5 bulk) decompression, a C# port of ksol-rdpgw/wwwroot/lib/rdpweb/mppc.js
// (itself a faithful port of FreeRDP libfreerdp/codec/mppc.c + winpr/bitstream.h, Apache-2.0).
//
// With INFO_COMPRESSION negotiated, the host RDP-bulk-compresses slow-path / static virtual channel
// data: the CHANNEL_PDU_HEADER carries CHANNEL_PACKET_COMPRESSED (0x00200000) and a compression type in
// the 0x000F0000 mask (0=8K/RDP4, 1=64K/RDP5). mstsc also sends its GFX CAPS_ADVERTISE this way on the
// drdynvc static channel. A single Mppc instance MUST be reused for the lifetime of a (channel,level)
// pair — the history window carries copy-offset matches across packets unless AT_FRONT/FLUSHED resets it.
//
// Decompress only (the MITM never re-compresses): literals + RDP4/RDP5 copy-offset/length tokens.

namespace KSol.RDPGateway.RDP;

internal sealed class Mppc
{
    public const int PACKET_COMPRESSED = 0x20;
    public const int PACKET_AT_FRONT = 0x40;
    public const int PACKET_FLUSHED = 0x80;

    private readonly int _level;          // 0 = RDP4/8K, 1 = RDP5/64K
    private readonly byte[] _history = new byte[65536];
    private int _historyOffset;

    public Mppc(int level) { _level = level != 0 ? 1 : 0; }

    public void Reset() { _historyOffset = 0; Array.Clear(_history); }

    // ---- bit reader, faithful to winpr BitStream ----
    private sealed class BitStream
    {
        private readonly byte[] _buf;
        private readonly int _capacity;
        public int Position;
        public readonly int Length;
        private int _ptr, _offset;
        private uint _mask, _prefetch;
        public uint Accumulator;

        public BitStream(byte[] buf)
        {
            _buf = buf; _capacity = buf.Length;
            Length = buf.Length * 8;
            Fetch();
        }

        private void Prefetch()
        {
            _prefetch = 0; int d = _ptr;
            if (d + 4 < _capacity) _prefetch |= (uint)_buf[d + 4] << 24;
            if (d + 5 < _capacity) _prefetch |= (uint)_buf[d + 5] << 16;
            if (d + 6 < _capacity) _prefetch |= (uint)_buf[d + 6] << 8;
            if (d + 7 < _capacity) _prefetch |= (uint)_buf[d + 7] << 0;
        }

        private void Fetch()
        {
            Accumulator = 0; int d = _ptr;
            if (d + 0 < _capacity) Accumulator |= (uint)_buf[d + 0] << 24;
            if (d + 1 < _capacity) Accumulator |= (uint)_buf[d + 1] << 16;
            if (d + 2 < _capacity) Accumulator |= (uint)_buf[d + 2] << 8;
            if (d + 3 < _capacity) Accumulator |= (uint)_buf[d + 3] << 0;
            Prefetch();
        }

        public void Shift(int n)
        {
            if (n == 0) return;
            if (n > 0 && n < 32)
            {
                Accumulator <<= n;
                Position += n; _offset += n;
                if (_offset < 32)
                {
                    _mask = (uint)((1 << n) - 1);
                    Accumulator |= (_prefetch >> (32 - n)) & _mask;
                    _prefetch <<= n;
                }
                else
                {
                    _mask = (uint)((1 << n) - 1);
                    Accumulator |= (_prefetch >> (32 - n)) & _mask;
                    _prefetch <<= n;
                    _offset -= 32; _ptr += 4; Prefetch();
                    if (_offset != 0)
                    {
                        _mask = (uint)((1 << _offset) - 1);
                        Accumulator |= (_prefetch >> (32 - _offset)) & _mask;
                        _prefetch <<= _offset;
                    }
                }
            }
        }
    }

    /// <summary>Decompress one bulk packet. `flags` uses the FreeRDP low-byte form (PACKET_COMPRESSED
    /// 0x20, AT_FRONT 0x40, FLUSHED 0x80). Returns the decompressed bytes, or null on a corrupt stream.</summary>
    public byte[]? Decompress(byte[] src, int flags)
    {
        var bs = new BitStream(src);
        if ((flags & PACKET_AT_FRONT) != 0) _historyOffset = 0;
        if ((flags & PACKET_FLUSHED) != 0) { _historyOffset = 0; Array.Clear(_history); }
        if ((flags & PACKET_COMPRESSED) == 0) return (byte[])src.Clone();

        byte[] H = _history;
        int hp = _historyOffset;
        int startHp = hp;
        int lvl = _level;
        int histMask = lvl != 0 ? 0xFFFF : 0x1FFF;

        while ((bs.Length - bs.Position) >= 8)
        {
            uint acc = bs.Accumulator;
            if ((acc & 0x80000000) == 0) { H[hp++] = (byte)((acc & 0x7F000000) >> 24); bs.Shift(8); continue; }
            else if ((acc & 0xC0000000) == 0x80000000) { H[hp++] = (byte)((((acc & 0x3F800000) >> 23) + 0x80) & 0xff); bs.Shift(9); continue; }

            int copyOffset;
            if (lvl != 0)
            {
                if ((acc & 0xF8000000) == 0xF8000000) { copyOffset = (int)((acc >> 21) & 0x3F); bs.Shift(11); }
                else if ((acc & 0xF8000000) == 0xF0000000) { copyOffset = (int)(((acc >> 19) & 0xFF) + 64); bs.Shift(13); }
                else if ((acc & 0xF0000000) == 0xE0000000) { copyOffset = (int)(((acc >> 17) & 0x7FF) + 320); bs.Shift(15); }
                else if ((acc & 0xE0000000) == 0xC0000000) { copyOffset = (int)(((acc >> 13) & 0xFFFF) + 2368); bs.Shift(19); }
                else return null;
            }
            else
            {
                if ((acc & 0xF0000000) == 0xF0000000) { copyOffset = (int)((acc >> 22) & 0x3F); bs.Shift(10); }
                else if ((acc & 0xF0000000) == 0xE0000000) { copyOffset = (int)(((acc >> 20) & 0xFF) + 64); bs.Shift(12); }
                else if ((acc & 0xE0000000) == 0xC0000000) { copyOffset = (int)(((acc >> 16) & 0x1FFF) + 320); bs.Shift(16); }
                else return null;
            }

            acc = bs.Accumulator;
            int len;
            if ((acc & 0x80000000) == 0x00000000) { len = 3; bs.Shift(1); }
            else if ((acc & 0xC0000000) == 0x80000000) { len = (int)((acc >> 28) & 0x3) + 4; bs.Shift(4); }
            else if ((acc & 0xE0000000) == 0xC0000000) { len = (int)((acc >> 26) & 0x7) + 8; bs.Shift(6); }
            else if ((acc & 0xF0000000) == 0xE0000000) { len = (int)((acc >> 24) & 0xF) + 16; bs.Shift(8); }
            else if ((acc & 0xF8000000) == 0xF0000000) { len = (int)((acc >> 22) & 0x1F) + 32; bs.Shift(10); }
            else if ((acc & 0xFC000000) == 0xF8000000) { len = (int)((acc >> 20) & 0x3F) + 64; bs.Shift(12); }
            else if ((acc & 0xFE000000) == 0xFC000000) { len = (int)((acc >> 18) & 0x7F) + 128; bs.Shift(14); }
            else if ((acc & 0xFF000000) == 0xFE000000) { len = (int)((acc >> 16) & 0xFF) + 256; bs.Shift(16); }
            else if ((acc & 0xFF800000) == 0xFF000000) { len = (int)((acc >> 14) & 0x1FF) + 512; bs.Shift(18); }
            else if ((acc & 0xFFC00000) == 0xFF800000) { len = (int)((acc >> 12) & 0x3FF) + 1024; bs.Shift(20); }
            else if ((acc & 0xFFE00000) == 0xFFC00000) { len = (int)((acc >> 10) & 0x7FF) + 2048; bs.Shift(22); }
            else if ((acc & 0xFFF00000) == 0xFFE00000) { len = (int)((acc >> 8) & 0xFFF) + 4096; bs.Shift(24); }
            else if ((acc & 0xFFF80000) == 0xFFF00000 && lvl != 0) { len = (int)((acc >> 6) & 0x1FFF) + 8192; bs.Shift(26); }
            else if ((acc & 0xFFFC0000) == 0xFFF80000 && lvl != 0) { len = (int)((acc >> 4) & 0x3FFF) + 16384; bs.Shift(28); }
            else if ((acc & 0xFFFE0000) == 0xFFFC0000 && lvl != 0) { len = (int)((acc >> 2) & 0x7FFF) + 32768; bs.Shift(30); }
            else return null;

            int srcIdx = (hp - copyOffset) & histMask;
            do { H[hp++] = H[srcIdx++]; } while (--len != 0);
        }

        var outBuf = new byte[hp - startHp];
        Array.Copy(H, startHp, outBuf, 0, outBuf.Length);
        _historyOffset = hp;
        return outBuf;
    }
}
