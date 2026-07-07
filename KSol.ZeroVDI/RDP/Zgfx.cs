// Zgfx — ZGFX (RDP8) bulk decompression, a C# port of ksol-rdpgw/wwwroot/lib/rdpweb/zgfx.js
// (itself a faithful port of FreeRDP libfreerdp/codec/zgfx.c, Apache-2.0).
//
// Every payload on the RDPEGFX Graphics DVC, and every v3 DATA_*_COMPRESSED dynamic-channel chunk, is
// wrapped in this scheme: a descriptor byte then one or more segments, each raw or LZ77+Huffman
// compressed against a 2.5 MB history ring. A single Zgfx instance MUST be reused for the whole
// lifetime of a channel — the history ring carries matches across PDUs.

namespace KSol.ZeroVDI.RDP;

internal sealed class Zgfx
{
    private const byte ZGFX_SEGMENTED_SINGLE = 0xe0;
    private const byte ZGFX_SEGMENTED_MULTIPART = 0xe1;
    private const byte ZGFX_PACKET_COMPRESSED = 0x20;
    private const int ZGFX_HISTORY_SIZE = 2500000;
    private const int ZGFX_OUTPUT_MAX = 65536;

    // [prefixLength, prefixCode, valueBits, tokenType(0=literal,1=match), valueBase]
    private static readonly int[][] Tokens =
    {
        new[]{1,0,8,0,0}, new[]{5,17,5,1,0}, new[]{5,18,7,1,32}, new[]{5,19,9,1,160},
        new[]{5,20,10,1,672}, new[]{5,21,12,1,1696}, new[]{5,24,0,0,0x00}, new[]{5,25,0,0,0x01},
        new[]{6,44,14,1,5792}, new[]{6,45,15,1,22176}, new[]{6,52,0,0,0x02}, new[]{6,53,0,0,0x03},
        new[]{6,54,0,0,0xff}, new[]{7,92,18,1,54944}, new[]{7,93,20,1,317088}, new[]{7,110,0,0,0x04},
        new[]{7,111,0,0,0x05}, new[]{7,112,0,0,0x06}, new[]{7,113,0,0,0x07}, new[]{7,114,0,0,0x08},
        new[]{7,115,0,0,0x09}, new[]{7,116,0,0,0x0a}, new[]{7,117,0,0,0x0b}, new[]{7,118,0,0,0x3a},
        new[]{7,119,0,0,0x3b}, new[]{7,120,0,0,0x3c}, new[]{7,121,0,0,0x3d}, new[]{7,122,0,0,0x3e},
        new[]{7,123,0,0,0x3f}, new[]{7,124,0,0,0x40}, new[]{7,125,0,0,0x80}, new[]{8,188,20,1,1365664},
        new[]{8,189,21,1,2414240}, new[]{8,252,0,0,0x0c}, new[]{8,253,0,0,0x38}, new[]{8,254,0,0,0x39},
        new[]{8,255,0,0,0x66}, new[]{9,380,22,1,4511392}, new[]{9,381,23,1,8705696}, new[]{9,382,24,1,17094304},
    };

    private readonly byte[] _history = new byte[ZGFX_HISTORY_SIZE];
    private int _historyIndex;
    private byte[] _in = Array.Empty<byte>();
    private int _pos, _end;
    private uint _bits, _bitsCurrent;
    private int _cBitsRemaining, _cBitsCurrent;
    private readonly byte[] _output = new byte[ZGFX_OUTPUT_MAX];
    private int _outputCount;

    public void Reset() => _historyIndex = 0;

    private void GetBits(int nbits)
    {
        while (_cBitsCurrent < nbits)
        {
            _bitsCurrent <<= 8;
            if (_pos < _end) _bitsCurrent += _in[_pos++];
            _cBitsCurrent += 8;
        }
        _cBitsRemaining -= nbits;
        _cBitsCurrent -= nbits;
        _bits = _bitsCurrent >> _cBitsCurrent;
        _bitsCurrent &= (uint)((1 << _cBitsCurrent) - 1);
    }

    private void HistoryWrite(byte[] src, int srcOff, int count)
    {
        if (count <= 0) return;
        if (count > ZGFX_HISTORY_SIZE)
        {
            int residue = count - ZGFX_HISTORY_SIZE;
            srcOff += residue;
            count = ZGFX_HISTORY_SIZE;
            _historyIndex = (_historyIndex + residue) % ZGFX_HISTORY_SIZE;
        }
        if (_historyIndex + count <= ZGFX_HISTORY_SIZE)
        {
            Array.Copy(src, srcOff, _history, _historyIndex, count);
            _historyIndex += count;
            if (_historyIndex == ZGFX_HISTORY_SIZE) _historyIndex = 0;
        }
        else
        {
            int front = ZGFX_HISTORY_SIZE - _historyIndex;
            Array.Copy(src, srcOff, _history, _historyIndex, front);
            Array.Copy(src, srcOff + front, _history, 0, count - front);
            _historyIndex = count - front;
        }
    }

    private void HistoryRead(int offset, byte[] dst, int dstOff, int count)
    {
        if (count <= 0) return;
        int bytesLeft = count;
        int index = (_historyIndex + ZGFX_HISTORY_SIZE - offset) % ZGFX_HISTORY_SIZE;
        int bytes = Math.Min(bytesLeft, offset);
        int origDstOff = dstOff;

        if (index + bytes <= ZGFX_HISTORY_SIZE)
            Array.Copy(_history, index, dst, dstOff, bytes);
        else
        {
            int front = ZGFX_HISTORY_SIZE - index;
            Array.Copy(_history, index, dst, dstOff, front);
            Array.Copy(_history, 0, dst, dstOff + front, bytes - front);
        }

        bytesLeft -= bytes;
        if (bytesLeft == 0) return;

        int dptr = dstOff + bytes;
        int valid = bytes;
        do
        {
            bytes = valid;
            if (bytes > bytesLeft) bytes = bytesLeft;
            // copyWithin(dptr, origDstOff, origDstOff+bytes) — may self-overlap (LZ77 run extension).
            for (int i = 0; i < bytes; i++) dst[dptr + i] = dst[origDstOff + i];
            dptr += bytes;
            valid <<= 1;
            bytesLeft -= bytes;
        } while (bytesLeft > 0);
    }

    private bool DecodeSegment(byte[] all, int segOff, int segLen)
    {
        if (segLen < 2) return false;
        byte flags = all[segOff];
        int bodyOff = segOff + 1;
        int cbSegment = segLen - 1;
        _outputCount = 0;

        if ((flags & ZGFX_PACKET_COMPRESSED) == 0)
        {
            if (cbSegment > ZGFX_OUTPUT_MAX) return false;
            // Body must be a standalone array for HistoryWrite/output copy.
            var body = new byte[cbSegment];
            Array.Copy(all, bodyOff, body, 0, cbSegment);
            HistoryWrite(body, 0, cbSegment);
            Array.Copy(body, 0, _output, 0, cbSegment);
            _outputCount = cbSegment;
            return true;
        }

        _in = new byte[cbSegment];
        Array.Copy(all, bodyOff, _in, 0, cbSegment);
        _pos = 0;
        _end = cbSegment - 1;
        int totalBits = 8 * (cbSegment - 1);
        byte lastByte = _in[cbSegment - 1];
        if (totalBits < lastByte) return false;
        _cBitsRemaining = totalBits - lastByte;
        _cBitsCurrent = 0;
        _bitsCurrent = 0;

        while (_cBitsRemaining > 0)
        {
            int haveBits = 0, inPrefix = 0;
            bool matched = false;

            foreach (var tok in Tokens)
            {
                int prefixLength = tok[0], prefixCode = tok[1], valueBits = tok[2],
                    tokenType = tok[3], valueBase = tok[4];
                while (haveBits < prefixLength)
                {
                    GetBits(1);
                    inPrefix = (inPrefix << 1) + (int)_bits;
                    haveBits++;
                }
                if (inPrefix != prefixCode) continue;
                matched = true;

                if (tokenType == 0)
                {
                    GetBits(valueBits);
                    byte c = (byte)((valueBase + _bits) & 0xff);
                    _history[_historyIndex] = c;
                    if (++_historyIndex == ZGFX_HISTORY_SIZE) _historyIndex = 0;
                    if (_outputCount >= ZGFX_OUTPUT_MAX) return false;
                    _output[_outputCount++] = c;
                }
                else
                {
                    GetBits(valueBits);
                    int distance = (int)(valueBase + _bits);
                    if (distance != 0)
                    {
                        GetBits(1);
                        int count;
                        if (_bits == 0) count = 3;
                        else
                        {
                            count = 4;
                            int extra = 2;
                            GetBits(1);
                            while (_bits == 1) { count *= 2; extra++; GetBits(1); }
                            GetBits(extra);
                            count += (int)_bits;
                        }
                        if (count > ZGFX_OUTPUT_MAX - _outputCount) return false;
                        HistoryRead(distance, _output, _outputCount, count);
                        HistoryWrite(_output, _outputCount, count);
                        _outputCount += count;
                    }
                    else
                    {
                        GetBits(15);
                        int count = (int)_bits;
                        _cBitsRemaining -= _cBitsCurrent;
                        _cBitsCurrent = 0;
                        _bitsCurrent = 0;
                        if (count > ZGFX_OUTPUT_MAX - _outputCount) return false;
                        if (count > (_cBitsRemaining >> 3)) return false;
                        if (_pos + count > _end) return false;
                        Array.Copy(_in, _pos, _output, _outputCount, count);
                        HistoryWrite(_in, _pos, count);
                        _pos += count;
                        _cBitsRemaining -= 8 * count;
                        _outputCount += count;
                    }
                }
                break;
            }
            if (!matched) return false;
        }
        return true;
    }

    /// <summary>Decompress a complete ZGFX message; returns null on error.</summary>
    public byte[]? Decompress(ReadOnlySpan<byte> dataSpan)
    {
        if (dataSpan.Length < 1) return null;
        var data = dataSpan.ToArray();
        byte descriptor = data[0];

        if (descriptor == ZGFX_SEGMENTED_SINGLE)
        {
            if (!DecodeSegment(data, 1, data.Length - 1)) return null;
            return _output.AsSpan(0, _outputCount).ToArray();
        }

        if (descriptor == ZGFX_SEGMENTED_MULTIPART)
        {
            if (data.Length < 7) return null;
            int o = 1;
            int segmentCount = data[o] | (data[o + 1] << 8); o += 2;
            uint uncompressedSize = (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24)); o += 4;
            var outBuf = new byte[uncompressedSize];
            int used = 0;
            for (int i = 0; i < segmentCount; i++)
            {
                if (data.Length - o < 4) return null;
                int segmentSize = (int)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24)); o += 4;
                if (data.Length - o < segmentSize) return null;
                if (!DecodeSegment(data, o, segmentSize)) return null;
                o += segmentSize;
                if (used + _outputCount > uncompressedSize) return null;
                Array.Copy(_output, 0, outBuf, used, _outputCount);
                used += _outputCount;
            }
            if (used != uncompressedSize) return null;
            return outBuf;
        }

        return null;
    }
}
