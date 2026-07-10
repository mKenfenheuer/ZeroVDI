namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// SPICE QUIC image decompression — a faithful C# port of spice-html5's quic.js (itself a port of
/// spice-common's quic.c). QUIC is the LOCO-I/JPEG-LS-family lossless codec SPICE uses for most desktop
/// content (image type 1), so this is the load-bearing decoder for a real SPICE session. Supports the
/// RGB24/RGB32/RGBA output types the server emits for the primary surface; GRAY/RGB16 are unsupported
/// (the server does not use them for the desktop). Output is a top-down BGRA buffer (w*h*4).
///
/// A <see cref="QuicDecoder"/> instance carries all decode state and is single-use per image, so the
/// display channel's parallel tile decode stays thread-safe (one decoder per call).
/// </summary>
internal static class SpiceQuic
{
    public const int TYPE_INVALID = 0, TYPE_GRAY = 1, TYPE_RGB16 = 2, TYPE_RGB24 = 3, TYPE_RGB32 = 4, TYPE_RGBA = 5;

    /// <summary>Decodes a QUIC stream to top-down BGRA, returning (width, height, bgra) or null.</summary>
    public static (int w, int h, byte[] bgra)? Decode(byte[] data)
    {
        var dec = new QuicDecoder();
        if (!dec.DecodeBegin(data)) return null;
        if (dec.Type != TYPE_RGB32 && dec.Type != TYPE_RGB24 && dec.Type != TYPE_RGBA) return null;

        int w = dec.Width, h = dec.Height;
        if (w <= 0 || h <= 0) return null;
        var outBuf = new byte[w * h * 4]; // decoder writes [B,G,R,pad] per pixel (rgb32 order)
        if (!dec.DecodeInto(outBuf, w * 4)) return null;

        // The decoder's per-pixel layout is index0=B, 1=G, 2=R, 3=pad/alpha — already BGRA order; only fix
        // alpha (opaque, or 255-a for RGBA, matching convert_spice_quic_to_web).
        bool rgba = dec.Type == TYPE_RGBA;
        for (int i = 0; i < outBuf.Length; i += 4)
            outBuf[i + 3] = rgba ? (byte)(255 - outBuf[i + 3]) : (byte)255;
        return (w, h, outBuf);
    }
}

/// <summary>The QUIC arithmetic-model decoder state (one per image).</summary>
internal sealed class QuicDecoder
{
    // ── shared, one-time initialized tables (thread-safe: read-only after cctor) ──────────────────────
    private const int DEFevol = 3, DEFwmimax = 6, DEFwminext = 2048, DEFmaxclen = 26;
    private const int evol = DEFevol, wmimax = DEFwmimax, wminext = DEFwminext;

    private static readonly uint[] Bppmask =
    {
        0x00000000,
        0x00000001, 0x00000003, 0x00000007, 0x0000000f,
        0x0000001f, 0x0000003f, 0x0000007f, 0x000000ff,
        0x000001ff, 0x000003ff, 0x000007ff, 0x00000fff,
        0x00001fff, 0x00003fff, 0x00007fff, 0x0000ffff,
        0x0001ffff, 0x0003ffff, 0x0007ffff, 0x000fffff,
        0x001fffff, 0x003fffff, 0x007fffff, 0x00ffffff,
        0x01ffffff, 0x03ffffff, 0x07ffffff, 0x0fffffff,
        0x1fffffff, 0x3fffffff, 0x7fffffff, 0xffffffff,
    };

    private static readonly int[] J =
    {
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    };

    private static readonly int[] Lzeroes =
    {
        8, 7, 6, 6, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0,
    };

    private static readonly int[][] Besttrigtab =
    {
        new[] { 550, 900, 800, 700, 500, 350, 300, 200, 180, 180, 160 },
        new[] { 110, 550, 900, 800, 550, 400, 350, 250, 140, 160, 140 },
        new[] { 100, 120, 550, 900, 700, 500, 400, 300, 220, 250, 160 },
    };

    private static readonly uint[] TabrandChaos =
    {
        0x02c57542, 0x35427717, 0x2f5a2153, 0x9244f155, 0x7bd26d07, 0x354c6052,
        0x57329b28, 0x2993868e, 0x6cd8808c, 0x147b46e0, 0x99db66af, 0xe32b4cac,
        0x1b671264, 0x9d433486, 0x62a4c192, 0x06089a4b, 0x9e3dce44, 0xdaabee13,
        0x222425ea, 0xa46f331d, 0xcd589250, 0x8bb81d7f, 0xc8b736b9, 0x35948d33,
        0xd7ac7fd0, 0x5fbe2803, 0x2cfbc105, 0x013dbc4e, 0x7a37820f, 0x39f88e9e,
        0xedd58794, 0xc5076689, 0xfcada5a4, 0x64c2f46d, 0xb3ba3243, 0x8974b4f9,
        0x5a05aebd, 0x20afcd00, 0x39e2b008, 0x88a18a45, 0x600bde29, 0xf3971ace,
        0xf37b0a6b, 0x7041495b, 0x70b707ab, 0x06beffbb, 0x4206051f, 0xe13c4ee3,
        0xc1a78327, 0x91aa067c, 0x8295f72a, 0x732917a6, 0x1d871b4d, 0x4048f136,
        0xf1840e7e, 0x6a6048c1, 0x696cb71a, 0x7ff501c3, 0x0fc6310b, 0x57e0f83d,
        0x8cc26e74, 0x11a525a2, 0x946934c7, 0x7cd888f0, 0x8f9d8604, 0x4f86e73b,
        0x04520316, 0xdeeea20c, 0xf1def496, 0x67687288, 0xf540c5b2, 0x22401484,
        0x3478658a, 0xc2385746, 0x01979c2c, 0x5dad73c8, 0x0321f58b, 0xf0fedbee,
        0x92826ddf, 0x284bec73, 0x5b1a1975, 0x03df1e11, 0x20963e01, 0xa17cf12b,
        0x740d776e, 0xa7a6bf3c, 0x01b5cce4, 0x1118aa76, 0xfc6fac0a, 0xce927e9b,
        0x00bf2567, 0x806f216c, 0xbca69056, 0x795bd3e9, 0xc9dc4557, 0x8929b6c2,
        0x789d52ec, 0x3f3fbf40, 0xb9197368, 0xa38c15b5, 0xc3b44fa8, 0xca8333b0,
        0xb7e8d590, 0xbe807feb, 0xbf5f8360, 0xd99e2f5c, 0x372928e1, 0x7c757c4c,
        0x0db5b154, 0xc01ede02, 0x1fc86e78, 0x1f3985be, 0xb4805c77, 0x00c880fa,
        0x974c1b12, 0x35ab0214, 0xb2dc840d, 0x5b00ae37, 0xd313b026, 0xb260969d,
        0x7f4c8879, 0x1734c4d3, 0x49068631, 0xb9f6a021, 0x6b863e6f, 0xcee5debf,
        0x29f8c9fb, 0x53dd6880, 0x72b61223, 0x1f67a9fd, 0x0a0f6993, 0x13e59119,
        0x11cca12e, 0xfe6b6766, 0x16b6effc, 0x97918fc4, 0xc2b8a563, 0x94f2f741,
        0x0bfa8c9a, 0xd1537ae8, 0xc1da349c, 0x873c60ca, 0x95005b85, 0x9b5c080e,
        0xbc8abbd9, 0xe1eab1d2, 0x6dac9070, 0x4ea9ebf1, 0xe0cf30d4, 0x1ef5bd7b,
        0xd161043e, 0x5d2fa2e2, 0xff5d3cae, 0x86ed9f87, 0x2aa1daa1, 0xbd731a34,
        0x9e8f4b22, 0xb1c2c67a, 0xc21758c9, 0xa182215d, 0xccb01948, 0x8d168df7,
        0x04238cfe, 0x368c3dbc, 0x0aeadca5, 0xbad21c24, 0x0a71fee5, 0x9fc5d872,
        0x54c152c6, 0xfc329483, 0x6783384a, 0xeddb3e1c, 0x65f90e30, 0x884ad098,
        0xce81675a, 0x4b372f7d, 0x68bf9a39, 0x43445f1e, 0x40f8d8cb, 0x90d5acb6,
        0x4cd07282, 0x349eeb06, 0x0c9d5332, 0x520b24ef, 0x80020447, 0x67976491,
        0x2f931ca3, 0xfe9b0535, 0xfcd30220, 0x61a9e6cc, 0xa487d8d7, 0x3f7c5dd1,
        0x7d0127c5, 0x48f51d15, 0x60dea871, 0xc9a91cb7, 0x58b53bb3, 0x9d5e0b2d,
        0x624a78b4, 0x30dbee1b, 0x9bdf22e7, 0x1df5c299, 0x2d5643a7, 0xf4dd35ff,
        0x03ca8fd6, 0x53b47ed8, 0x6f2c19aa, 0xfeb0c1f4, 0x49e54438, 0x2f2577e6,
        0xbf876969, 0x72440ea9, 0xfa0bafb8, 0x74f5b3a0, 0x7dd357cd, 0x89ce1358,
        0x6ef2cdda, 0x1e7767f3, 0xa6be9fdb, 0x4f5f88f8, 0xba994a3a, 0x08ca6b65,
        0xe0893818, 0x9e00a16a, 0xf42bfc8f, 0x9972eedc, 0x749c8b51, 0x32c05f5e,
        0xd706805f, 0x6bfbb7cf, 0xd9210a10, 0x31a1db97, 0x923a9559, 0x37a7a1f6,
        0x059f8861, 0xca493e62, 0x65157e81, 0x8f6467dd, 0xab85ff9f, 0x9331aff2,
        0x8616b9f5, 0xedbd5695, 0xee7e29b1, 0x313ac44f, 0xb903112f, 0x432ef649,
        0xdc0a36c0, 0x61cf2bba, 0x81474925, 0xa8b6c7ad, 0xee5931de, 0xb2f8158d,
        0x59fb7409, 0x2e3dfaed, 0x9af25a3f, 0xe1fed4d5,
    };

    // rgb32 pixel byte offsets within a 4-byte pixel.
    private const int PixPad = 3, PixR = 2, PixG = 1, PixB = 0, PixSize = 4;

    // family tables (Golomb-Rice model), initialized once.
    private sealed class Family
    {
        public readonly int[] nGRcodewords = new int[8];
        public readonly int[] notGRcwlen = new int[8];
        public readonly uint[] notGRprefixmask = new uint[8];
        public readonly int[] notGRsuffixlen = new int[8];
        public readonly int[] xlatU2L = new int[256];
        public readonly int[] xlatL2U = new int[256];
    }

    private static readonly Family Family8;
    private static readonly Family Family5;
    private static readonly int[] ZeroLUT = new int[256];

    static QuicDecoder()
    {
        Family8 = new Family();
        Family5 = new Family();
        FamilyInit(Family8, 8, DEFmaxclen);
        FamilyInit(Family5, 5, DEFmaxclen);
        int j = 1, k = 1, l = 8;
        for (int i = 0; i < 256; ++i)
        {
            ZeroLUT[i] = l;
            --k;
            if (k == 0) { k = j; --l; j *= 2; }
        }
    }

    private static int CeilLog2(int val)
    {
        if (val == 1) return 0;
        int result = 1;
        val -= 1;
        while ((val >>= 1) != 0) result++;
        return result;
    }

    private static void FamilyInit(Family family, int bpc, int limit)
    {
        for (int l = 0; l < bpc; l++)
        {
            long altprefixlen = limit - bpc;
            if (altprefixlen > Bppmask[bpc - l]) altprefixlen = Bppmask[bpc - l];
            long altcodewords = Bppmask[bpc] + 1 - (altprefixlen << l);
            family.nGRcodewords[l] = (int)(altprefixlen << l);
            family.notGRcwlen[l] = (int)(altprefixlen + CeilLog2((int)altcodewords));
            family.notGRprefixmask[l] = Bppmask[32 - (int)altprefixlen];
            family.notGRsuffixlen[l] = CeilLog2((int)altcodewords);
        }

        uint pixelbitmask = Bppmask[bpc];
        uint pixelbitmaskshr = pixelbitmask >> 1;
        for (uint s = 0; s <= pixelbitmask; s++)
            family.xlatU2L[s] = s <= pixelbitmaskshr ? (int)(s << 1) : (int)(((pixelbitmask - s) << 1) + 1);
        for (uint s = 0; s <= pixelbitmask; s++)
            family.xlatL2U[s] = (s & 1) != 0 ? (int)(pixelbitmask - (s >> 1)) : (int)(s >> 1);
    }

    private static int QuicImageBpc(int type) => type switch
    {
        SpiceQuic.TYPE_GRAY => 8,
        SpiceQuic.TYPE_RGB16 => 5,
        SpiceQuic.TYPE_RGB24 => 8,
        SpiceQuic.TYPE_RGB32 => 8,
        SpiceQuic.TYPE_RGBA => 8,
        _ => 0,
    };

    private static int CntLZeroes(uint bits)
    {
        if ((bits & 0xff800000) != 0) return Lzeroes[bits >> 24];
        if ((bits & 0xffff8000) != 0) return 8 + Lzeroes[(bits >> 16) & 0xff];
        if ((bits & 0xffffff80) != 0) return 16 + Lzeroes[(bits >> 8) & 0xff];
        return 24 + Lzeroes[bits & 0xff];
    }

    // Golomb decode: returns (codewordlen, rc). Uses the 8bpc family.
    private static void GolombDecoding8bpc(int l, uint bits, out int codewordlen, out int rc)
    {
        if (bits > Family8.notGRprefixmask[l])
        {
            int zeroprefix = CntLZeroes(bits);
            codewordlen = zeroprefix + 1 + l;
            rc = (int)(((uint)zeroprefix << l) | ((bits >> (32 - codewordlen)) & Bppmask[l]));
        }
        else
        {
            codewordlen = Family8.notGRcwlen[l];
            rc = Family8.nGRcodewords[l] + (int)((bits >> (32 - codewordlen)) & Bppmask[Family8.notGRsuffixlen[l]]);
        }
    }

    private static int GolombCodeLen8bpc(int n, int l) =>
        n < Family8.nGRcodewords[l] ? (n >> l) + 1 + l : Family8.notGRcwlen[l];

    // ── model (bucket structure) ──────────────────────────────────────────────────────────────────────
    private sealed class Model
    {
        public int n_buckets, repfirst, firstsize, repnext, mulsize, levels;

        public Model(int bpc)
        {
            levels = 1 << bpc;
            switch (evol)
            {
                case 1: repfirst = 3; firstsize = 1; repnext = 2; mulsize = 2; break;
                case 3: repfirst = 1; firstsize = 1; repnext = 1; mulsize = 2; break;
                case 5: repfirst = 1; firstsize = 1; repnext = 1; mulsize = 4; break;
            }
            n_buckets = 0;
            int repcntr = repfirst + 1, bsize = firstsize, bend = 0;
            do
            {
                int bstart = n_buckets != 0 ? bend + 1 : 0;
                if (--repcntr == 0) { repcntr = repnext; bsize *= mulsize; }
                bend = bstart + bsize - 1;
                if (bend + bsize >= levels) bend = levels - 1;
                n_buckets++;
            } while (bend < levels - 1);
        }
    }

    private sealed class Bucket
    {
        public readonly long[] counters = new long[8];
        public int bestcode;

        public void Reset(int bpp) { bestcode = bpp; Array.Clear(counters, 0, 8); }

        public void UpdateModel8bpc(CommonState state, int curval, int bpp)
        {
            int best = bpp - 1;
            long bestlen = (counters[best] += GolombCodeLen8bpc(curval, best));
            for (int i = bpp - 2; i >= 0; i--)
            {
                long ithlen = (counters[i] += GolombCodeLen8bpc(curval, i));
                if (ithlen < bestlen) { best = i; bestlen = ithlen; }
            }
            bestcode = best;
            if (bestlen > state.wm_trigger)
                for (int i = 0; i < bpp; i++) counters[i] >>= 1;
        }
    }

    private sealed class FamilyStat
    {
        public Bucket[] buckets_ptrs = Array.Empty<Bucket>();
        public Bucket[] buckets_buf = Array.Empty<Bucket>();

        public void Fill(Model model)
        {
            buckets_ptrs = new Bucket[model.levels];
            var buf = new List<Bucket>();
            int repcntr = model.repfirst + 1, bsize = model.firstsize, bend = 0, bnumber = 0;
            do
            {
                int bstart = bnumber != 0 ? bend + 1 : 0;
                if (--repcntr == 0) { repcntr = model.repnext; bsize *= model.mulsize; }
                bend = bstart + bsize - 1;
                if (bend + bsize >= model.levels) bend = model.levels - 1;
                var bucket = new Bucket();
                buf.Add(bucket);
                for (int i = bstart; i <= bend; i++) buckets_ptrs[i] = bucket;
                bnumber++;
            } while (bend < model.levels - 1);
            buckets_buf = buf.ToArray();
        }
    }

    private sealed class CommonState
    {
        public int waitcnt, tabrand_seed = 0xff, wm_trigger, wmidx, wmileft = wminext;
        public int melcstate, melclen, melcorder;

        public void SetWmTrigger()
        {
            int wm = wmidx > 10 ? 10 : wmidx;
            wm_trigger = Besttrigtab[evol / 2][wm];
        }

        public void Reset()
        {
            waitcnt = 0; tabrand_seed = 0xff; wmidx = 0; wmileft = wminext;
            SetWmTrigger();
            melcstate = 0; melclen = J[0]; melcorder = 1 << melclen;
        }

        public uint Tabrand()
        {
            tabrand_seed++;
            return TabrandChaos[tabrand_seed & 0xff];
        }
    }

    private sealed class CorrelateRow { public int zero; public int[] row = Array.Empty<int>(); }

    private sealed class Channel
    {
        public readonly CommonState state = new();
        public readonly FamilyStat stat8 = new();
        public readonly FamilyStat stat5 = new();
        public CorrelateRow correlate_row = new();
        public Bucket[] buckets_ptrs = Array.Empty<Bucket>();
        private readonly Model model8, model5;

        public Channel(Model m8, Model m5, int width)
        {
            model8 = m8; model5 = m5;
            stat8.Fill(model8);
            stat5.Fill(model5);
            correlate_row.row = new int[width];
        }

        public void Reset(int bpc, int width)
        {
            correlate_row = new CorrelateRow { row = new int[width] };
            if (bpc == 8)
            {
                foreach (var b in stat8.buckets_buf) b.Reset(7);
                buckets_ptrs = stat8.buckets_ptrs;
            }
            else
            {
                foreach (var b in stat8.buckets_buf) b.Reset(4); // matches quic.js (uses stat8 buf for 5bpc)
                buckets_ptrs = stat5.buckets_ptrs;
            }
            state.Reset();
        }
    }

    // ── instance decode state ─────────────────────────────────────────────────────────────────────────
    private readonly CommonState _rgbState = new();
    private readonly Model _model8 = new(8);
    private readonly Model _model5 = new(5);
    private Channel[] _channels = Array.Empty<Channel>();

    private byte[] _io = Array.Empty<byte>();
    private int _ioIdx, _ioEnd, _ioAvailBits;
    private uint _ioWord, _ioNextWord;

    public int Type { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    private void ReadIoWord()
    {
        if (_ioIdx >= _ioEnd) throw new InvalidDataException("quic: out of data");
        _ioNextWord = (uint)(_io[_ioIdx] | (_io[_ioIdx + 1] << 8) | (_io[_ioIdx + 2] << 16) | (_io[_ioIdx + 3] << 24));
        _ioIdx += 4;
    }

    private void DecodeEatbits(int len)
    {
        _ioWord <<= len;
        int delta = _ioAvailBits - len;
        if (delta >= 0)
        {
            _ioAvailBits = delta;
            _ioWord |= _ioNextWord >> _ioAvailBits;
        }
        else
        {
            delta = -delta;
            _ioWord |= _ioNextWord << delta;
            ReadIoWord();
            _ioAvailBits = 32 - delta;
            _ioWord |= _ioNextWord >> _ioAvailBits;
        }
    }

    private void DecodeEat32bits() { DecodeEatbits(16); DecodeEatbits(16); }

    public bool DecodeBegin(byte[] data)
    {
        _rgbState.Reset();
        _io = data; _ioEnd = data.Length; _ioIdx = 0;
        _ioNextWord = (uint)(_io[_ioIdx] | (_io[_ioIdx + 1] << 8) | (_io[_ioIdx + 2] << 16) | (_io[_ioIdx + 3] << 24));
        _ioIdx += 4;
        _ioWord = _ioNextWord;
        _ioAvailBits = 0;

        uint magic = _ioWord; DecodeEat32bits();
        if (magic != 0x43495551) return false;               // "QUIC"
        uint version = _ioWord; DecodeEat32bits();
        if (version != 0) return false;
        Type = (int)_ioWord; DecodeEat32bits();
        Width = (int)_ioWord; DecodeEat32bits();
        Height = (int)_ioWord; DecodeEat32bits();

        int bpc = QuicImageBpc(Type);
        if (bpc == 0) return false;

        _channels = new Channel[4];
        for (int i = 0; i < 4; i++) _channels[i] = new Channel(_model8, _model5, Width + 1);
        for (int i = 0; i < 4; i++) _channels[i].Reset(bpc, Width + 1);
        return true;
    }

    // decode into a top-down buffer; stride is bytes per row (== width*4).
    public bool DecodeInto(byte[] buf, int stride)
    {
        switch (Type)
        {
            case SpiceQuic.TYPE_RGB32:
            case SpiceQuic.TYPE_RGB24:
                Rgb32Row0(buf, 0);
                for (int row = 1; row < Height; row++)
                {
                    int prev = (row - 1) * stride, cur = row * stride;
                    _channels[0].correlate_row.zero = _channels[0].correlate_row.row[0];
                    _channels[1].correlate_row.zero = _channels[1].correlate_row.row[0];
                    _channels[2].correlate_row.zero = _channels[2].correlate_row.row[0];
                    Rgb32Row(buf, prev, cur);
                }
                return true;

            case SpiceQuic.TYPE_RGBA:
                Rgb32Row0(buf, 0);
                _channels[3].correlate_row.zero = 0;
                FourRow0(_channels[3], buf, 0);
                for (int row = 1; row < Height; row++)
                {
                    int prev = (row - 1) * stride, cur = row * stride;
                    _channels[0].correlate_row.zero = _channels[0].correlate_row.row[0];
                    _channels[1].correlate_row.zero = _channels[1].correlate_row.row[0];
                    _channels[2].correlate_row.zero = _channels[2].correlate_row.row[0];
                    Rgb32Row(buf, prev, cur);
                    _channels[3].correlate_row.zero = _channels[3].correlate_row.row[0];
                    FourRow(_channels[3], buf, prev, cur);
                }
                return true;

            default:
                return false;
        }
    }

    // ── run length (MELCODE) ─────────────────────────────────────────────────────────────────────────
    private int DecodeRun(CommonState state)
    {
        int runlen = 0;
        while (true)
        {
            uint x = (~(_ioWord >> 24)) & 0xff;
            int temp = ZeroLUT[x];
            for (int hits = 1; hits <= temp; hits++)
            {
                runlen += state.melcorder;
                if (state.melcstate < 32)
                {
                    state.melclen = J[++state.melcstate];
                    state.melcorder = 1 << state.melclen;
                }
            }
            if (temp != 8) { DecodeEatbits(temp + 1); break; }
            DecodeEatbits(8);
        }
        if (state.melclen != 0)
        {
            runlen += (int)(_ioWord >> (32 - state.melclen));
            DecodeEatbits(state.melclen);
        }
        if (state.melcstate != 0)
        {
            state.melclen = J[--state.melcstate];
            state.melcorder = 1 << state.melclen;
        }
        return runlen;
    }

    // ── rgb32 row 0 ───────────────────────────────────────────────────────────────────────────────────
    private void Rgb32Row0Seg(int start, byte[] cur, int curBase, int end, uint waitmask, int bpc, int bpcMask)
    {
        int i = start, n = 3;
        int stopidx;
        if (i == 0)
        {
            cur[curBase + PixPad] = 0;
            for (int c = 0; c < n; c++)
            {
                GolombDecoding8bpc(_channels[c].buckets_ptrs[_channels[c].correlate_row.zero].bestcode, _ioWord, out int cwl, out int rc);
                _channels[c].correlate_row.row[0] = rc;
                cur[curBase + (2 - c)] = (byte)(Family8.xlatL2U[rc] & 0xff);
                DecodeEatbits(cwl);
            }
            if (_rgbState.waitcnt != 0) --_rgbState.waitcnt;
            else
            {
                _rgbState.waitcnt = (int)(_rgbState.Tabrand() & waitmask);
                for (int c = 0; c < n; c++)
                    _channels[c].buckets_ptrs[_channels[c].correlate_row.zero].UpdateModel8bpc(_rgbState, _channels[c].correlate_row.row[0], bpc);
            }
            stopidx = ++i + _rgbState.waitcnt;
        }
        else stopidx = i + _rgbState.waitcnt;

        while (stopidx < end)
        {
            for (; i <= stopidx; i++)
            {
                int px = curBase + i * PixSize, pxm1 = curBase + (i - 1) * PixSize;
                cur[px + PixPad] = 0;
                for (int c = 0; c < n; c++)
                {
                    GolombDecoding8bpc(_channels[c].buckets_ptrs[_channels[c].correlate_row.row[i - 1]].bestcode, _ioWord, out int cwl, out int rc);
                    _channels[c].correlate_row.row[i] = rc;
                    cur[px + (2 - c)] = (byte)((Family8.xlatL2U[rc] + cur[pxm1 + (2 - c)]) & bpcMask);
                    DecodeEatbits(cwl);
                }
            }
            for (int c = 0; c < n; c++)
                _channels[c].buckets_ptrs[_channels[c].correlate_row.row[stopidx - 1]].UpdateModel8bpc(_rgbState, _channels[c].correlate_row.row[stopidx], bpc);
            stopidx = i + (int)(_rgbState.Tabrand() & waitmask);
        }

        for (; i < end; i++)
        {
            int px = curBase + i * PixSize, pxm1 = curBase + (i - 1) * PixSize;
            cur[px + PixPad] = 0;
            for (int c = 0; c < n; c++)
            {
                GolombDecoding8bpc(_channels[c].buckets_ptrs[_channels[c].correlate_row.row[i - 1]].bestcode, _ioWord, out int cwl, out int rc);
                _channels[c].correlate_row.row[i] = rc;
                cur[px + (2 - c)] = (byte)((Family8.xlatL2U[rc] + cur[pxm1 + (2 - c)]) & bpcMask);
                DecodeEatbits(cwl);
            }
        }
        _rgbState.waitcnt = stopidx - end;
    }

    private void Rgb32Row0(byte[] buf, int curBase)
    {
        int bpc = 8, bpcMask = 0xff, pos = 0, width = Width;
        while (wmimax > _rgbState.wmidx && _rgbState.wmileft <= width)
        {
            if (_rgbState.wmileft != 0)
            {
                Rgb32Row0Seg(pos, buf, curBase, pos + _rgbState.wmileft, Bppmask[_rgbState.wmidx], bpc, bpcMask);
                pos += _rgbState.wmileft; width -= _rgbState.wmileft;
            }
            _rgbState.wmidx++; _rgbState.SetWmTrigger(); _rgbState.wmileft = wminext;
        }
        if (width != 0)
        {
            Rgb32Row0Seg(pos, buf, curBase, pos + width, Bppmask[_rgbState.wmidx], bpc, bpcMask);
            if (wmimax > _rgbState.wmidx) _rgbState.wmileft -= width;
        }
    }

    // ── rgb32 subsequent rows ─────────────────────────────────────────────────────────────────────────
    private void Rgb32RowSeg(byte[] buf, int prevBase, int curBase, int start, int end, int bpc, int bpcMask)
    {
        int n = 3;
        uint waitmask = Bppmask[_rgbState.wmidx];
        int i = start, run_index = 0, stopidx, run_end;

        if (i == 0)
        {
            buf[curBase + PixPad] = 0;
            for (int c = 0; c < n; c++)
            {
                GolombDecoding8bpc(_channels[c].buckets_ptrs[_channels[c].correlate_row.zero].bestcode, _ioWord, out int cwl, out int rc);
                _channels[c].correlate_row.row[0] = rc;
                buf[curBase + (2 - c)] = (byte)((Family8.xlatL2U[_channels[c].correlate_row.row[0]] + buf[prevBase + (2 - c)]) & bpcMask);
                DecodeEatbits(cwl);
            }
            if (_rgbState.waitcnt != 0) --_rgbState.waitcnt;
            else
            {
                _rgbState.waitcnt = (int)(_rgbState.Tabrand() & waitmask);
                for (int c = 0; c < n; c++)
                    _channels[c].buckets_ptrs[_channels[c].correlate_row.zero].UpdateModel8bpc(_rgbState, _channels[c].correlate_row.row[0], bpc);
            }
            stopidx = ++i + _rgbState.waitcnt;
        }
        else stopidx = i + _rgbState.waitcnt;

        while (true)
        {
            int rc = 0;
            while (stopidx < end && rc == 0)
            {
                for (; i <= stopidx && rc == 0; i++)
                {
                    int pixel = curBase + i * PixSize, pixelm1 = curBase + (i - 1) * PixSize, pixelm2 = curBase + (i - 2) * PixSize;
                    int pPixel = prevBase + i * PixSize, pPixelm1 = prevBase + (i - 1) * PixSize;
                    if (buf[pPixelm1 + PixR] == buf[pPixel + PixR] && buf[pPixelm1 + PixG] == buf[pPixel + PixG] && buf[pPixelm1 + PixB] == buf[pPixel + PixB])
                    {
                        if (run_index != i && i > 2 && buf[pixelm1 + PixR] == buf[pixelm2 + PixR] && buf[pixelm1 + PixG] == buf[pixelm2 + PixG] && buf[pixelm1 + PixB] == buf[pixelm2 + PixB])
                        {
                            _rgbState.waitcnt = stopidx - i;
                            run_index = i;
                            run_end = i + DecodeRun(_rgbState);
                            for (; i < run_end; i++)
                            {
                                int p = curBase + i * PixSize, pm1 = curBase + (i - 1) * PixSize;
                                buf[p + PixPad] = 0; buf[p + PixR] = buf[pm1 + PixR]; buf[p + PixG] = buf[pm1 + PixG]; buf[p + PixB] = buf[pm1 + PixB];
                            }
                            if (i == end) return;
                            stopidx = i + _rgbState.waitcnt; rc = 1; break;
                        }
                    }
                    buf[pixel + PixPad] = 0;
                    for (int c = 0; c < n; c++)
                    {
                        var cr = _channels[c].correlate_row;
                        GolombDecoding8bpc(_channels[c].buckets_ptrs[cr.row[i - 1]].bestcode, _ioWord, out int cwl, out int rcc);
                        cr.row[i] = rcc;
                        buf[pixel + (2 - c)] = (byte)((Family8.xlatL2U[rcc] + ((buf[pixelm1 + (2 - c)] + buf[pPixel + (2 - c)]) >> 1)) & bpcMask);
                        DecodeEatbits(cwl);
                    }
                }
                if (rc != 0) break;
                for (int c = 0; c < n; c++)
                    _channels[c].buckets_ptrs[_channels[c].correlate_row.row[stopidx - 1]].UpdateModel8bpc(_rgbState, _channels[c].correlate_row.row[stopidx], bpc);
                stopidx = i + (int)(_rgbState.Tabrand() & waitmask);
            }

            for (; i < end && rc == 0; i++)
            {
                int pixel = curBase + i * PixSize, pixelm1 = curBase + (i - 1) * PixSize, pixelm2 = curBase + (i - 2) * PixSize;
                int pPixel = prevBase + i * PixSize, pPixelm1 = prevBase + (i - 1) * PixSize;
                if (buf[pPixelm1 + PixR] == buf[pPixel + PixR] && buf[pPixelm1 + PixG] == buf[pPixel + PixG] && buf[pPixelm1 + PixB] == buf[pPixel + PixB])
                {
                    if (run_index != i && i > 2 && buf[pixelm1 + PixR] == buf[pixelm2 + PixR] && buf[pixelm1 + PixG] == buf[pixelm2 + PixG] && buf[pixelm1 + PixB] == buf[pixelm2 + PixB])
                    {
                        _rgbState.waitcnt = stopidx - i;
                        run_index = i;
                        run_end = i + DecodeRun(_rgbState);
                        for (; i < run_end; i++)
                        {
                            int p = curBase + i * PixSize, pm1 = curBase + (i - 1) * PixSize;
                            buf[p + PixPad] = 0; buf[p + PixR] = buf[pm1 + PixR]; buf[p + PixG] = buf[pm1 + PixG]; buf[p + PixB] = buf[pm1 + PixB];
                        }
                        if (i == end) return;
                        stopidx = i + _rgbState.waitcnt; rc = 1; break;
                    }
                }
                buf[pixel + PixPad] = 0;
                for (int c = 0; c < n; c++)
                {
                    GolombDecoding8bpc(_channels[c].buckets_ptrs[_channels[c].correlate_row.row[i - 1]].bestcode, _ioWord, out int cwl, out int rcc);
                    _channels[c].correlate_row.row[i] = rcc;
                    buf[pixel + (2 - c)] = (byte)((Family8.xlatL2U[rcc] + ((buf[pixelm1 + (2 - c)] + buf[pPixel + (2 - c)]) >> 1)) & bpcMask);
                    DecodeEatbits(cwl);
                }
            }
            if (rc == 0) { _rgbState.waitcnt = stopidx - end; return; }
        }
    }

    private void Rgb32Row(byte[] buf, int prevBase, int curBase)
    {
        int bpc = 8, bpcMask = 0xff, pos = 0, width = Width;
        while (wmimax > _rgbState.wmidx && _rgbState.wmileft <= width)
        {
            if (_rgbState.wmileft != 0)
            {
                Rgb32RowSeg(buf, prevBase, curBase, pos, pos + _rgbState.wmileft, bpc, bpcMask);
                pos += _rgbState.wmileft; width -= _rgbState.wmileft;
            }
            _rgbState.wmidx++; _rgbState.SetWmTrigger(); _rgbState.wmileft = wminext;
        }
        if (width != 0)
        {
            Rgb32RowSeg(buf, prevBase, curBase, pos, pos + width, bpc, bpcMask);
            if (wmimax > _rgbState.wmidx) _rgbState.wmileft -= width;
        }
    }

    // ── 4th channel (alpha) ───────────────────────────────────────────────────────────────────────────
    private void FourRow0Seg(Channel ch, int start, CorrelateRow cr, byte[] cur, int curBase, int end, uint waitmask, int bpc, int bpcMask)
    {
        int i = start, stopidx;
        if (i == 0)
        {
            GolombDecoding8bpc(ch.buckets_ptrs[cr.zero].bestcode, _ioWord, out int cwl, out int rc);
            cr.row[0] = rc;
            cur[curBase + PixPad] = (byte)Family8.xlatL2U[rc];
            DecodeEatbits(cwl);
            if (ch.state.waitcnt != 0) --ch.state.waitcnt;
            else
            {
                ch.state.waitcnt = (int)(ch.state.Tabrand() & waitmask);
                ch.buckets_ptrs[cr.zero].UpdateModel8bpc(ch.state, cr.row[0], bpc);
            }
            stopidx = ++i + ch.state.waitcnt;
        }
        else stopidx = i + ch.state.waitcnt;

        while (stopidx < end)
        {
            Bucket pbucket = ch.buckets_ptrs[cr.row[Math.Max(i - 1, 0)]];
            for (; i <= stopidx; i++)
            {
                pbucket = ch.buckets_ptrs[cr.row[i - 1]];
                GolombDecoding8bpc(pbucket.bestcode, _ioWord, out int cwl, out int rc);
                cr.row[i] = rc;
                cur[curBase + i * PixSize + PixPad] = (byte)((Family8.xlatL2U[rc] + cur[curBase + (i - 1) * PixSize + PixPad]) & bpcMask);
                DecodeEatbits(cwl);
            }
            pbucket.UpdateModel8bpc(ch.state, cr.row[stopidx], bpc);
            stopidx = i + (int)(ch.state.Tabrand() & waitmask);
        }

        for (; i < end; i++)
        {
            GolombDecoding8bpc(ch.buckets_ptrs[cr.row[i - 1]].bestcode, _ioWord, out int cwl, out int rc);
            cr.row[i] = rc;
            cur[curBase + i * PixSize + PixPad] = (byte)((Family8.xlatL2U[rc] + cur[curBase + (i - 1) * PixSize + PixPad]) & bpcMask);
            DecodeEatbits(cwl);
        }
        ch.state.waitcnt = stopidx - end;
    }

    private void FourRow0(Channel ch, byte[] buf, int curBase)
    {
        int bpc = 8, bpcMask = 0xff, pos = 0, width = Width;
        var cr = ch.correlate_row;
        while (wmimax > ch.state.wmidx && ch.state.wmileft <= width)
        {
            if (ch.state.wmileft != 0)
            {
                FourRow0Seg(ch, pos, cr, buf, curBase, pos + ch.state.wmileft, Bppmask[ch.state.wmidx], bpc, bpcMask);
                pos += ch.state.wmileft; width -= ch.state.wmileft;
            }
            ch.state.wmidx++; ch.state.SetWmTrigger(); ch.state.wmileft = wminext;
        }
        if (width != 0)
        {
            FourRow0Seg(ch, pos, cr, buf, curBase, pos + width, Bppmask[ch.state.wmidx], bpc, bpcMask);
            if (wmimax > ch.state.wmidx) ch.state.wmileft -= width;
        }
    }

    private void FourRowSeg(Channel ch, CorrelateRow cr, byte[] buf, int prevBase, int curBase, int start, int end, int bpc, int bpcMask)
    {
        uint waitmask = Bppmask[ch.state.wmidx];
        int i = start, run_index = 0, stopidx, run_end;

        if (i == 0)
        {
            GolombDecoding8bpc(ch.buckets_ptrs[cr.zero].bestcode, _ioWord, out int cwl, out int rc);
            cr.row[0] = rc;
            buf[curBase + PixPad] = (byte)((Family8.xlatL2U[rc] + buf[prevBase + PixPad]) & bpcMask);
            DecodeEatbits(cwl);
            if (ch.state.waitcnt != 0) --ch.state.waitcnt;
            else
            {
                ch.state.waitcnt = (int)(ch.state.Tabrand() & waitmask);
                ch.buckets_ptrs[cr.zero].UpdateModel8bpc(ch.state, cr.row[0], bpc);
            }
            stopidx = ++i + ch.state.waitcnt;
        }
        else stopidx = i + ch.state.waitcnt;

        while (true)
        {
            int rc2 = 0;
            while (stopidx < end && rc2 == 0)
            {
                Bucket pbucket = ch.buckets_ptrs[cr.row[Math.Max(i - 1, 0)]];
                for (; i <= stopidx && rc2 == 0; i++)
                {
                    int pixel = curBase + i * PixSize, pixelm1 = curBase + (i - 1) * PixSize, pixelm2 = curBase + (i - 2) * PixSize;
                    int pPixel = prevBase + i * PixSize, pPixelm1 = prevBase + (i - 1) * PixSize;
                    if (buf[pPixelm1 + PixPad] == buf[pPixel + PixPad])
                    {
                        if (run_index != i && i > 2 && buf[pixelm1 + PixPad] == buf[pixelm2 + PixPad])
                        {
                            ch.state.waitcnt = stopidx - i;
                            run_index = i;
                            run_end = i + DecodeRun(ch.state);
                            for (; i < run_end; i++)
                                buf[curBase + i * PixSize + PixPad] = buf[curBase + (i - 1) * PixSize + PixPad];
                            if (i == end) return;
                            stopidx = i + ch.state.waitcnt; rc2 = 1; break;
                        }
                    }
                    pbucket = ch.buckets_ptrs[cr.row[i - 1]];
                    GolombDecoding8bpc(pbucket.bestcode, _ioWord, out int cwl, out int rc);
                    cr.row[i] = rc;
                    buf[pixel + PixPad] = (byte)((Family8.xlatL2U[rc] + ((buf[pixelm1 + PixPad] + buf[pPixel + PixPad]) >> 1)) & bpcMask);
                    DecodeEatbits(cwl);
                }
                if (rc2 != 0) break;
                pbucket.UpdateModel8bpc(ch.state, cr.row[stopidx], bpc);
                stopidx = i + (int)(ch.state.Tabrand() & waitmask);
            }

            for (; i < end && rc2 == 0; i++)
            {
                int pixel = curBase + i * PixSize, pixelm1 = curBase + (i - 1) * PixSize, pixelm2 = curBase + (i - 2) * PixSize;
                int pPixel = prevBase + i * PixSize, pPixelm1 = prevBase + (i - 1) * PixSize;
                if (buf[pPixelm1 + PixPad] == buf[pPixel + PixPad])
                {
                    if (run_index != i && i > 2 && buf[pixelm1 + PixPad] == buf[pixelm2 + PixPad])
                    {
                        ch.state.waitcnt = stopidx - i;
                        run_index = i;
                        run_end = i + DecodeRun(ch.state);
                        for (; i < run_end; i++)
                            buf[curBase + i * PixSize + PixPad] = buf[curBase + (i - 1) * PixSize + PixPad];
                        if (i == end) return;
                        stopidx = i + ch.state.waitcnt; rc2 = 1; break;
                    }
                }
                GolombDecoding8bpc(ch.buckets_ptrs[cr.row[i - 1]].bestcode, _ioWord, out int cwl, out int rc);
                cr.row[i] = rc;
                buf[pixel + PixPad] = (byte)((Family8.xlatL2U[rc] + ((buf[pixelm1 + PixPad] + buf[pPixel + PixPad]) >> 1)) & bpcMask);
                DecodeEatbits(cwl);
            }
            if (rc2 == 0) { ch.state.waitcnt = stopidx - end; return; }
        }
    }

    private void FourRow(Channel ch, byte[] buf, int prevBase, int curBase)
    {
        int bpc = 8, bpcMask = 0xff, pos = 0, width = Width;
        var cr = ch.correlate_row;
        while (wmimax > ch.state.wmidx && ch.state.wmileft <= width)
        {
            if (ch.state.wmileft != 0)
            {
                FourRowSeg(ch, cr, buf, prevBase, curBase, pos, pos + ch.state.wmileft, bpc, bpcMask);
                pos += ch.state.wmileft; width -= ch.state.wmileft;
            }
            ch.state.wmidx++; ch.state.SetWmTrigger(); ch.state.wmileft = wminext;
        }
        if (width != 0)
        {
            FourRowSeg(ch, cr, buf, prevBase, curBase, pos, pos + width, bpc, bpcMask);
            if (wmimax > ch.state.wmidx) ch.state.wmileft -= width;
        }
    }
}
