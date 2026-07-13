namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// Self-contained baseline (sequential DCT, 8-bit) JPEG decoder for SPICE display images and MJPEG video
/// streams. No third-party dependency (ImageSharp requires a commercial licence and, worse, decodes each
/// buffer standalone — which breaks SPICE's MJPEG streams).
///
/// SPICE/QEMU MJPEG streams are <b>abbreviated</b>: the first frame carries the DQT + DHT tables, and every
/// later frame OMITS the DHT (Huffman) tables to save bytes, relying on the decoder to remember them — a
/// standalone decode of a later frame has no Huffman tables and yields grey. So this decoder is an
/// <b>instance</b> that PERSISTS its quantization + Huffman tables across <see cref="Decode"/> calls; a
/// table-less frame reuses the tables from the previous one. For one-shot images (SPICE DRAW ops, which are
/// self-contained) use <see cref="DecodeOnce"/>.
///
/// Supported: baseline SOF0, 8-bit precision, 1 (grayscale) or 3 (YCbCr) components, any of the common
/// 4:4:4 / 4:2:2 / 4:2:0 subsamplings, optional restart intervals (DRI/RSTn). Progressive (SOF2) and
/// arithmetic coding are not used by SPICE and are rejected. Output is top-down BGRA (w*h*4), opaque unless
/// an alpha plane is supplied.
/// </summary>
internal sealed class SpiceJpeg
{
    // ── persistent decoder state (survives across frames for abbreviated MJPEG streams) ─────────────
    private readonly ushort[][] _quant = new ushort[4][];             // [tableId] → 64 dequant factors (zigzag order)
    private readonly HuffTable?[] _huffDc = new HuffTable?[4];
    private readonly HuffTable?[] _huffAc = new HuffTable?[4];

    /// <summary>Decodes one JPEG frame, reusing tables cached from previous frames (abbreviated streams).
    /// Returns top-down BGRA or null on unsupported/invalid input. Not thread-safe (single draw worker).</summary>
    public (int w, int h, byte[] bgra)? Decode(ReadOnlySpan<byte> jpeg, byte[]? alphaBgra = null)
    {
        try { return DecodeInternal(jpeg, alphaBgra); }
        catch { return null; }
    }

    /// <summary>One-shot decode of a self-contained JPEG (no cross-frame table state). Convenience wrapper
    /// for SPICE DRAW images, which always carry their own tables.</summary>
    public static (int w, int h, byte[] bgra)? DecodeOnce(ReadOnlySpan<byte> jpeg, byte[]? alphaBgra = null)
        => new SpiceJpeg().Decode(jpeg, alphaBgra);

    // Back-compat shim for existing callers (self-contained decode).
    public static (int w, int h, byte[] bgra)? DecodeToBgra(ReadOnlySpan<byte> jpeg, byte[]? alphaBgra)
        => DecodeOnce(jpeg, alphaBgra);

    // ── frame geometry (per-decode) ─────────────────────────────────────────────────────────────────
    private int _width, _height, _numComp, _restartInterval;
    private Comp[] _comp = Array.Empty<Comp>();
    private int _maxH, _maxV;

    private struct Comp
    {
        public int Id, H, V, QuantId;      // sampling factors + quant table id
        public int DcId, AcId;             // Huffman table ids (from SOS)
        public int[] Data;                 // full-plane samples at this component's block resolution
        public int PlaneW, PlaneH;         // padded plane size in samples (mcuW*H*8 …)
        public int PrevDc;                 // DC predictor
    }

    private (int w, int h, byte[] bgra)? DecodeInternal(ReadOnlySpan<byte> d, byte[]? alphaBgra)
    {
        if (d.Length < 2 || d[0] != 0xFF || d[1] != 0xD8) return null;   // SOI
        int p = 2;
        bool haveFrame = false;

        while (p + 1 < d.Length)
        {
            if (d[p] != 0xFF) { p++; continue; }
            int marker = d[p + 1];
            p += 2;
            if (marker == 0xD9) break;                      // EOI
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue; // TEM/RSTn (no payload here)
            if (p + 1 >= d.Length) break;
            int len = (d[p] << 8) | d[p + 1];               // includes the 2 length bytes
            int seg = p + 2, segEnd = p + len;
            if (segEnd > d.Length) return null;

            switch (marker)
            {
                case 0xDB: ReadDqt(d, seg, segEnd); break;
                case 0xC4: ReadDht(d, seg, segEnd); break;
                case 0xDD: _restartInterval = (d[seg] << 8) | d[seg + 1]; break;   // DRI
                case 0xC0: if (!ReadSof0(d, seg, segEnd)) return null; haveFrame = true; break;
                case 0xC1: if (!ReadSof0(d, seg, segEnd)) return null; haveFrame = true; break; // extended sequential ≈ baseline layout
                case 0xC2: case 0xC3: case 0xC5: case 0xC6: case 0xC7:
                case 0xC9: case 0xCA: case 0xCB: case 0xCD: case 0xCE: case 0xCF:
                    return null;                             // progressive / arithmetic / lossless — unsupported
                case 0xDA:
                    if (!haveFrame) return null;
                    return DecodeScan(d, seg, segEnd, alphaBgra);
                default: break;                              // APPn, COM, etc. — skip
            }
            p = segEnd;
        }
        return null;
    }

    private void ReadDqt(ReadOnlySpan<byte> d, int p, int end)
    {
        while (p < end)
        {
            int pq = d[p] >> 4, tq = d[p] & 0x0F; p++;
            if (tq > 3) return;
            var q = new ushort[64];
            if (pq == 0) { for (int i = 0; i < 64; i++) q[i] = d[p++]; }
            else { for (int i = 0; i < 64; i++) { q[i] = (ushort)((d[p] << 8) | d[p + 1]); p += 2; } }
            _quant[tq] = q;
        }
    }

    private sealed class HuffTable
    {
        // Canonical decode via (mincode/maxcode/valptr) per code length, plus the value list.
        public readonly int[] MinCode = new int[17];
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValPtr = new int[17];
        public byte[] Values = Array.Empty<byte>();
    }

    private void ReadDht(ReadOnlySpan<byte> d, int p, int end)
    {
        while (p < end)
        {
            int tc = d[p] >> 4, th = d[p] & 0x0F; p++;      // class (0=DC,1=AC), id
            if (th > 3) return;
            var counts = new int[17];
            int total = 0;
            for (int i = 1; i <= 16; i++) { counts[i] = d[p++]; total += counts[i]; }
            var values = new byte[total];
            for (int i = 0; i < total; i++) values[i] = d[p++];

            var t = new HuffTable { Values = values };
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                if (counts[len] == 0) { t.MaxCode[len] = -1; }
                else
                {
                    t.ValPtr[len] = k;
                    t.MinCode[len] = code;
                    code += counts[len];
                    t.MaxCode[len] = code - 1;
                    k += counts[len];
                }
                code <<= 1;
            }
            t.MaxCode[17] = 0x7FFFFFFF;
            if (tc == 0) _huffDc[th] = t; else _huffAc[th] = t;
        }
    }

    private bool ReadSof0(ReadOnlySpan<byte> d, int p, int end)
    {
        int prec = d[p]; p++;
        if (prec != 8) return false;
        _height = (d[p] << 8) | d[p + 1]; p += 2;
        _width = (d[p] << 8) | d[p + 1]; p += 2;
        _numComp = d[p]; p++;
        if (_numComp != 1 && _numComp != 3) return false;
        if (_width <= 0 || _height <= 0) return false;

        _comp = new Comp[_numComp];
        _maxH = _maxV = 1;
        for (int i = 0; i < _numComp; i++)
        {
            _comp[i].Id = d[p]; p++;
            _comp[i].H = d[p] >> 4; _comp[i].V = d[p] & 0x0F; p++;
            _comp[i].QuantId = d[p]; p++;
            if (_comp[i].H < 1 || _comp[i].V < 1) return false;
            if (_comp[i].H > _maxH) _maxH = _comp[i].H;
            if (_comp[i].V > _maxV) _maxV = _comp[i].V;
        }
        return true;
    }

    // ── entropy-coded scan ──────────────────────────────────────────────────────────────────────────
    private int _bitPos;                // byte index into _scanArr
    private int _bitBuf, _bitCnt;       // bit accumulator
    private bool _marker;               // set when a non-RST marker is hit inside the scan
    private byte[] _scanArr = Array.Empty<byte>();

    private (int w, int h, byte[] bgra)? DecodeScan(ReadOnlySpan<byte> d, int p, int segEnd, byte[]? alphaBgra)
    {
        int ns = d[p]; p++;
        if (ns != _numComp) return null;
        // Map scan component selectors to frame components and assign Huffman table ids.
        for (int i = 0; i < ns; i++)
        {
            int cs = d[p]; p++;
            int td = d[p] >> 4, ta = d[p] & 0x0F; p++;
            int ci = -1;
            for (int c = 0; c < _numComp; c++) if (_comp[c].Id == cs) { ci = c; break; }
            if (ci < 0) return null;
            _comp[ci].DcId = td; _comp[ci].AcId = ta;
        }
        p += 3; // Ss, Se, Ah/Al — ignored for baseline

        // Allocate per-component sample planes at MCU-padded resolution.
        int mcuW = _maxH * 8, mcuH = _maxV * 8;
        int mcusX = (_width + mcuW - 1) / mcuW;
        int mcusY = (_height + mcuH - 1) / mcuH;
        for (int c = 0; c < _numComp; c++)
        {
            _comp[c].PlaneW = mcusX * _comp[c].H * 8;
            _comp[c].PlaneH = mcusY * _comp[c].V * 8;
            _comp[c].Data = new int[_comp[c].PlaneW * _comp[c].PlaneH];
            _comp[c].PrevDc = 0;
        }

        // Scan bytes run from p to the next marker (0xFF not followed by 0x00 / RSTn). The provided segEnd is
        // just this SOS header's length; the actual entropy data extends past it to EOI, so scan forward.
        _scanArr = d.Slice(p).ToArray();
        _bitPos = 0; _bitBuf = 0; _bitCnt = 0; _marker = false;

        var block = new int[64];
        int restartCountdown = _restartInterval;
        for (int my = 0; my < mcusY; my++)
        {
            for (int mx = 0; mx < mcusX; mx++)
            {
                for (int c = 0; c < _numComp; c++)
                {
                    for (int by = 0; by < _comp[c].V; by++)
                    for (int bx = 0; bx < _comp[c].H; bx++)
                    {
                        Array.Clear(block, 0, 64);
                        if (!DecodeBlock(c, block)) goto done;
                        int px = (mx * _comp[c].H + bx) * 8;
                        int py = (my * _comp[c].V + by) * 8;
                        IdctAndStore(block, _comp[c].Data, _comp[c].PlaneW, px, py, _quant[_comp[c].QuantId]);
                    }
                }
                if (_restartInterval > 0 && --restartCountdown == 0)
                {
                    restartCountdown = _restartInterval;
                    if (!(my == mcusY - 1 && mx == mcusX - 1)) HandleRestart();
                }
            }
        }
        done:
        return Compose(alphaBgra);
    }

    // Bit reader: MSB-first; 0xFF00 stuffing → 0xFF; any other 0xFFxx is a marker (stop).
    private int GetBit()
    {
        if (_bitCnt == 0)
        {
            if (_marker || _bitPos >= _scanArr.Length) { _marker = true; return 0; }
            int b = _scanArr[_bitPos++];
            if (b == 0xFF)
            {
                int b2 = _bitPos < _scanArr.Length ? _scanArr[_bitPos] : 0xD9;
                if (b2 == 0x00) { _bitPos++; }
                else if (b2 >= 0xD0 && b2 <= 0xD7) { /* RST handled by HandleRestart */ _marker = true; return 0; }
                else { _marker = true; return 0; }
            }
            _bitBuf = b; _bitCnt = 8;
        }
        _bitCnt--;
        return (_bitBuf >> _bitCnt) & 1;
    }

    private int GetBits(int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++) v = (v << 1) | GetBit();
        return v;
    }

    private int Receive(int n)   // read n bits, sign-extend per JPEG (F.12)
    {
        if (n == 0) return 0;
        int v = GetBits(n);
        if (v < (1 << (n - 1))) v += (-1 << n) + 1;
        return v;
    }

    private int DecodeHuff(HuffTable? t)
    {
        if (t == null) { _marker = true; return -1; }
        int code = 0;
        for (int len = 1; len <= 16; len++)
        {
            code = (code << 1) | GetBit();
            if (code <= t.MaxCode[len]) return t.Values[t.ValPtr[len] + (code - t.MinCode[len])];
        }
        _marker = true;
        return -1;
    }

    private bool DecodeBlock(int c, int[] block)
    {
        int t = DecodeHuff(_huffDc[_comp[c].DcId]);
        if (t < 0) return false;
        int diff = t == 0 ? 0 : Receive(t);
        _comp[c].PrevDc += diff;
        block[0] = _comp[c].PrevDc;

        int k = 1;
        while (k < 64)
        {
            int rs = DecodeHuff(_huffAc[_comp[c].AcId]);
            if (rs < 0) return false;
            int r = rs >> 4, s = rs & 0x0F;
            if (s == 0) { if (r != 15) break; k += 16; continue; }   // EOB or ZRL
            k += r;
            if (k >= 64) break;
            block[ZigZag[k]] = Receive(s);
            k++;
        }
        return true;
    }

    private void HandleRestart()
    {
        // Align to the RST marker, consume it, reset predictors + bit buffer.
        _bitCnt = 0;
        // find 0xFF Dn
        while (_bitPos + 1 < _scanArr.Length)
        {
            if (_scanArr[_bitPos] == 0xFF)
            {
                int m = _scanArr[_bitPos + 1];
                if (m >= 0xD0 && m <= 0xD7) { _bitPos += 2; break; }
                if (m == 0x00) { _bitPos += 2; continue; }
                break;
            }
            _bitPos++;
        }
        _marker = false;
        for (int c = 0; c < _numComp; c++) _comp[c].PrevDc = 0;
    }

    // ── IDCT (integer, separable AAN — the jidctint/stb_image algorithm) ────────────────────────────
    // Reused scratch, allocated once per decoder (single-threaded), sized for one 8x8 block.
    private readonly int[] _dq = new int[64];
    private readonly int[] _idctTmp = new int[64];

    private void IdctAndStore(int[] block, int[] plane, int planeW, int px, int py, ushort[]? quant)
    {
        // Dequantize into natural order. block[] is natural (coeffs scattered via ZigZag[]); quant[] is
        // zigzag order as read from DQT, so a natural coeff i uses quant[ZigZagInv[i]].
        var dq = _dq;
        for (int i = 0; i < 64; i++) dq[i] = block[i] * (quant != null ? quant[ZigZagInv[i]] : 1);

        var t = _idctTmp;
        // Exact port of stb_image stbi__idct_block. Pass 1 (columns): bias +512, descale >>10. Pass 2 (rows):
        // bias +(65536+(128<<17)), descale >>17 — folds in the +128 level shift — then clamp to byte.
        for (int i = 0; i < 8; i++)
        {
            IdctKernel(dq[i], dq[i + 8], dq[i + 16], dq[i + 24], dq[i + 32], dq[i + 40], dq[i + 48], dq[i + 56],
                512, out int x0, out int x1, out int x2, out int x3, out int x4, out int x5, out int x6, out int x7);
            t[i] = x0 >> 10; t[i + 8] = x1 >> 10; t[i + 16] = x2 >> 10; t[i + 24] = x3 >> 10;
            t[i + 32] = x4 >> 10; t[i + 40] = x5 >> 10; t[i + 48] = x6 >> 10; t[i + 56] = x7 >> 10;
        }
        const int rowBias = 65536 + (128 << 17);
        for (int y = 0; y < 8; y++)
        {
            int b = y * 8;
            IdctKernel(t[b], t[b + 1], t[b + 2], t[b + 3], t[b + 4], t[b + 5], t[b + 6], t[b + 7],
                rowBias, out int x0, out int x1, out int x2, out int x3, out int x4, out int x5, out int x6, out int x7);
            int row = (py + y) * planeW + px;
            plane[row]     = Clamp(x0 >> 17); plane[row + 1] = Clamp(x1 >> 17);
            plane[row + 2] = Clamp(x2 >> 17); plane[row + 3] = Clamp(x3 >> 17);
            plane[row + 4] = Clamp(x4 >> 17); plane[row + 5] = Clamp(x5 >> 17);
            plane[row + 6] = Clamp(x6 >> 17); plane[row + 7] = Clamp(x7 >> 17);
        }
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    // stb_image stbi__idct_block 1-D kernel (constants are stbi__f2f(x)=round(x*4096)). `bias` is added to the
    // even-part accumulators before the caller descales — 512 for the column pass, 65536+(128<<17) for rows.
    private static void IdctKernel(int s0, int s1, int s2, int s3, int s4, int s5, int s6, int s7,
        int bias, out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7)
    {
        int p2 = s2, p3 = s6;
        int p1 = (p2 + p3) * 2217;          // f2f(0.5411961)
        int t2 = p1 + p3 * -7567;           // f2f(-1.847759065)
        int t3 = p1 + p2 * 3135;            // f2f(0.765366865)
        p2 = s0; p3 = s4;
        int t0 = (p2 + p3) << 12;
        int t1 = (p2 - p3) << 12;
        int x0 = t0 + t3 + bias, x3 = t0 - t3 + bias, x1 = t1 + t2 + bias, x2 = t1 - t2 + bias;
        t0 = s7; t1 = s5; t2 = s3; t3 = s1;
        p3 = t0 + t2; int p4 = t1 + t3; p1 = t0 + t3; p2 = t1 + t2;
        int p5 = (p3 + p4) * 4816;          // f2f(1.175875602)
        t0 *= 1223;                         // f2f(0.298631336)
        t1 *= 8410;                         // f2f(2.053119869)
        t2 *= 12586;                        // f2f(3.072711026)
        t3 *= 6149;                         // f2f(1.501321110)
        p1 = p5 + p1 * -3685;               // f2f(-0.899976223)
        p2 = p5 + p2 * -10497;              // f2f(-2.562915447)
        p3 *= -8034;                        // f2f(-1.961570560)
        p4 *= -1597;                        // f2f(-0.390180644)
        t3 += p1 + p4; t2 += p2 + p3; t1 += p2 + p4; t0 += p1 + p3;
        o0 = x0 + t3; o7 = x0 - t3;
        o1 = x1 + t2; o6 = x1 - t2;
        o2 = x2 + t1; o5 = x2 - t1;
        o3 = x3 + t0; o4 = x3 - t0;
    }

    // ── colour convert + upsample into top-down BGRA ───────────────────────────────────────────────
    private (int w, int h, byte[] bgra)? Compose(byte[]? alphaBgra)
    {
        int w = _width, h = _height;
        var outBuf = new byte[w * h * 4];

        if (_numComp == 1)
        {
            var y = _comp[0];
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int v = y.Data[j * y.PlaneW + i];
                int o = (j * w + i) * 4;
                outBuf[o] = (byte)v; outBuf[o + 1] = (byte)v; outBuf[o + 2] = (byte)v; outBuf[o + 3] = 255;
            }
        }
        else
        {
            var Y = _comp[0]; var Cb = _comp[1]; var Cr = _comp[2];
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    // Nearest-sample upsample: map the output pixel to each plane's sub-sampled grid.
                    int yy = Y.Data[(j * Y.V / _maxV) * Y.PlaneW + (i * Y.H / _maxH)];
                    int cb = Cb.Data[(j * Cb.V / _maxV) * Cb.PlaneW + (i * Cb.H / _maxH)] - 128;
                    int cr = Cr.Data[(j * Cr.V / _maxV) * Cr.PlaneW + (i * Cr.H / _maxH)] - 128;
                    int r = yy + ((91881 * cr) >> 16);
                    int g = yy - ((22554 * cb + 46802 * cr) >> 16);
                    int b = yy + ((116130 * cb) >> 16);
                    int o = (j * w + i) * 4;
                    outBuf[o] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                    outBuf[o + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                    outBuf[o + 2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                    outBuf[o + 3] = 255;
                }
            }
        }

        if (alphaBgra != null && alphaBgra.Length >= w * h * 4)
            for (int i = 3; i < outBuf.Length; i += 4) outBuf[i] = alphaBgra[i - 3];

        return (w, h, outBuf);
    }

    // Zigzag scan order (index k → natural block position) and its inverse.
    private static readonly int[] ZigZag =
    {
        0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,
        35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63
    };
    private static readonly int[] ZigZagInv = BuildZigZagInv();
    private static int[] BuildZigZagInv()
    {
        var inv = new int[64];
        for (int k = 0; k < 64; k++) inv[ZigZag[k]] = k;
        return inv;
    }
}
