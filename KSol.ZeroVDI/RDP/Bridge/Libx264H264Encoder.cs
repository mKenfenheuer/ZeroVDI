using System.Runtime.InteropServices;

namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// Real-time H.264 encoder for the RDPEGFX AVC420 path, backed by <b>libx264 in-process via direct
/// P/Invoke</b> (no ffmpeg subprocess, no managed wrapper). It drives libx264's C API
/// (<c>x264_encoder_open</c>/<c>x264_encoder_encode</c>/<c>x264_encoder_close</c>) directly and does its own
/// BGRA⇒I420 colour conversion, so a full-frame top-down BGRA buffer goes straight to an Annex-B access unit
/// with no process boundary or stdin/stdout copy.
///
/// Because libx264 encodes synchronously and — with <c>b_frames=0</c> and <c>i_sync_lookahead=0</c> — emits
/// exactly one NAL-bundle per input picture with zero reorder delay, each <see cref="EncodeAsync"/> call
/// produces its access unit immediately and raises <see cref="OnFrame"/> inline. There is no reader thread and
/// no idle-flush timer (both of which the ffmpeg backend needs to reframe a byte stream) — the AU is already
/// whole.
///
/// The encoder is configured to match the constraints the browser's WebCodecs decoder imposes (see
/// <see cref="FfmpegH264Encoder"/> for the archaeology): CABAC on ⇒ <b>Main</b> profile, <b>level 4.2</b>,
/// one slice per picture, an Access Unit Delimiter (NAL 9) leading each AU, SPS/PPS repeated on every IDR
/// (<c>b_repeat_headers=1</c>), and Annex-B start codes (<c>b_annexb=1</c>).
///
/// This is an alternative <see cref="IH264Encoder"/> to <see cref="FfmpegH264Encoder"/>; swap the DI
/// registration of <see cref="IH264EncoderFactory"/> in <c>Program.cs</c> to select it.
/// </summary>
internal sealed class Libx264H264Encoder : IH264Encoder
{
    private readonly int _width, _height, _fps, _crf, _maxKbps;
    private readonly ILogger _logger;
    private readonly int _frameBytes;

    private IntPtr _enc;          // x264_t*
    private IntPtr _picIn;        // unmanaged x264_picture_t (input)
    private IntPtr _picOut;       // unmanaged x264_picture_t (output)
    private IntPtr _yPlane;       // I420 luma buffer (native)
    private IntPtr _uPlane, _vPlane;
    private int _ySize, _cSize;
    private long _pts;
    private readonly object _encLock = new();
    private bool _disposed;

    /// <summary>Raised with one Annex-B access unit (start-code delimited NAL units) per encoded frame.</summary>
    public event Action<byte[]>? OnFrame;

    /// <param name="crf">x264 constant-rate-factor (quality/size knob): ~18 = visually lossless, ~28 =
    /// smaller/softer. The adaptive controller raises it on slow links. Clamped to a sane 16..40.</param>
    /// <param name="maxKbps">Bitrate ceiling (kbit/s) for capped-CRF via VBV, or 0 for unbounded CRF.</param>
    public Libx264H264Encoder(int width, int height, int fps, ILogger logger, int crf = 23, int maxKbps = 0)
    {
        _width = width & ~1; _height = height & ~1; _fps = Math.Clamp(fps, 5, 60);
        _crf = Math.Clamp(crf, 16, 40);
        _maxKbps = Math.Max(0, maxKbps);
        _logger = logger;
        _frameBytes = _width * _height * 4;
    }

    public int Width => _width;
    public int Height => _height;
    public int Crf => _crf;
    public int MaxKbps => _maxKbps;

    public unsafe void Start()
    {
        // ---- x264_param_t: apply the "zerolatency" preset+tune, then pin the settings the WebCodecs decoder
        // needs. We allocate the param struct natively (its layout is large and version-dependent) and let
        // libx264 fill it via x264_param_default_preset, then poke the handful of fields we care about by the
        // documented setter (x264_param_parse), which is ABI-stable across build numbers — far safer than
        // hand-declaring the whole struct.
        var param = Marshal.AllocHGlobal(X264ParamSize);
        try
        {
            if (X264.x264_param_default_preset(param, "ultrafast", "zerolatency") != 0)
                throw new InvalidOperationException("x264_param_default_preset failed");

            // Geometry / timebase. (Colorspace stays at x264's default, X264_CSP_I420, so no input-csp needed —
            // and "input-csp" is not an x264_param_parse key anyway.)
            Parse(param, "fps", $"{_fps}/1");
            SetDimensions(param, _width, _height);
            *(int*)((byte*)param + OffParamCsp) = X264_CSP_I420;   // encoder input colorspace (default anyway)

            // Quality + framing knobs (mirror the ffmpeg backend's -x264-params string).
            Parse(param, "crf", _crf.ToString());
            // Capped CRF: keep CRF as the quality target but clamp the instantaneous bitrate with VBV so a
            // busy/animated desktop can't spike the 5264 stream to tens of Mbit/s. vbv-maxrate is the ceiling
            // (kbit/s); vbv-bufsize is the rate-control window — small (~0.5s of the cap) so we bound bursts
            // and hold latency down rather than smoothing over seconds. maxKbps==0 ⇒ pure CRF (no cap).
            if (_maxKbps > 0)
            {
                Parse(param, "vbv-maxrate", _maxKbps.ToString());
                Parse(param, "vbv-bufsize", Math.Max(1, _maxKbps / 2).ToString());
            }
            Parse(param, "cabac", "1");        // → Main profile (Constrained Baseline is rejected by WebCodecs)
            Parse(param, "level", "4.2");      // Main@L4.2 (avc1.4d402a) is the only level the decoder accepts
            Parse(param, "slices", "1");       // one VCL slice per picture (our AU framing assumes this)
            Parse(param, "keyint", "120");
            Parse(param, "scenecut", "0");
            Parse(param, "bframes", "0");      // no reorder → one AU per input frame, emitted immediately
            Parse(param, "ref", "1");
            Parse(param, "aud", "1");          // Access Unit Delimiter (NAL 9) before each AU
            Parse(param, "threads", "1");      // frame-threading buffers ~nproc frames → latency; single-threaded
            Parse(param, "sync-lookahead", "0");
            Parse(param, "repeat-headers", "1"); // SPS/PPS before every IDR
            Parse(param, "annexb", "1");         // Annex-B start codes (not length-prefixed AVCC)

            _enc = X264.x264_encoder_open(param);
            if (_enc == IntPtr.Zero) throw new InvalidOperationException("x264_encoder_open failed");
        }
        finally { Marshal.FreeHGlobal(param); }

        // ---- I420 input picture. We keep one reusable set of native plane buffers and one x264_picture_t
        // whose img.plane[]/i_stride[] point at them; each frame we memcpy the converted planes in and encode.
        _ySize = _width * _height;
        _cSize = (_width / 2) * (_height / 2);
        _yPlane = Marshal.AllocHGlobal(_ySize);
        _uPlane = Marshal.AllocHGlobal(_cSize);
        _vPlane = Marshal.AllocHGlobal(_cSize);

        _picIn = Marshal.AllocHGlobal(X264PictureSize);
        _picOut = Marshal.AllocHGlobal(X264PictureSize);
        X264.x264_picture_init(_picIn);
        X264.x264_picture_init(_picOut);

        // Fill the input picture header: img.i_csp + plane pointers + strides. x264_picture_t has no top-level
        // i_csp — the colorspace lives in img (x264_image_t). Offsets below are verified against x264.h.
        var pic = (byte*)_picIn;
        var img = pic + OffPicImg;   // x264_image_t: {int i_csp; int i_plane; int i_stride[4]; uint8_t* plane[4];}
        *(int*)(img + OffImgICsp) = X264_CSP_I420;
        *(int*)(img + OffImgIPlane) = 3;
        int* strides = (int*)(img + OffImgIStride);
        strides[0] = _width; strides[1] = _width / 2; strides[2] = _width / 2; strides[3] = 0;
        IntPtr* planes = (IntPtr*)(img + OffImgPlane);
        planes[0] = _yPlane; planes[1] = _uPlane; planes[2] = _vPlane; planes[3] = IntPtr.Zero;

        _logger.LogInformation("Bridge/H264: libx264 encoder started {W}x{H}@{F} crf={C} maxKbps={M}", _width, _height, _fps, _crf, _maxKbps);
    }

    private int _fedCount;
    public unsafe Task EncodeAsync(byte[] bgra, CancellationToken ct)
    {
        if (_disposed || _enc == IntPtr.Zero) { _logger.LogInformation("Bridge/H264: EncodeAsync skipped (encoder null/disposed)"); return Task.CompletedTask; }
        if (bgra.Length != _frameBytes) { _logger.LogInformation("Bridge/H264: EncodeAsync skipped (len {L} != {E})", bgra.Length, _frameBytes); return Task.CompletedTask; }

        byte[]? au = null;
        lock (_encLock)
        {
            if (_disposed || _enc == IntPtr.Zero) return Task.CompletedTask;

            fixed (byte* src = bgra)
                BgraToI420(src, (byte*)_yPlane, (byte*)_uPlane, (byte*)_vPlane, _width, _height);

            // pts must advance monotonically or x264 warns and may drop frames.
            *(long*)((byte*)_picIn + OffPicIPts) = _pts++;

            // x264_encoder_encode: returns the encoded frame size in bytes (>0), 0 when it buffered (won't
            // happen here — no delay), or <0 on error. On success ppNal/piNal describe the NAL array whose
            // payload is one contiguous Annex-B blob starting at nal[0].p_payload.
            IntPtr ppNal; int iNal;
            int size = X264.x264_encoder_encode(_enc, out ppNal, out iNal, _picIn, _picOut);
            if (size < 0) { _logger.LogWarning("Bridge/H264: x264_encoder_encode returned {S}", size); return Task.CompletedTask; }
            if (size > 0 && iNal > 0)
            {
                // All NALs of one picture are laid out contiguously; nal[0].p_payload is the AU start and the
                // total byte count is `size`. (x264_nal_t = {int i_ref_idc; int i_type; int b_long_startcode;
                // int i_first_mb; int i_last_mb; int i_padding; int i_payload; uint8_t* p_payload; ...})
                var nal0 = (byte*)ppNal;
                IntPtr payload = *(IntPtr*)(nal0 + OffNalPayload);
                au = new byte[size];
                Marshal.Copy(payload, au, 0, size);
            }
        }

        if (au != null)
        {
            int c = ++_fedCount;
            if (c <= 5) _logger.LogInformation("Bridge/H264: emitted AU #{C} ({B}B) from libx264", c, au.Length);
            try { OnFrame?.Invoke(au); } catch (Exception ex) { _logger.LogDebug(ex, "Bridge/H264: OnFrame threw"); }
        }
        return Task.CompletedTask;
    }

    // BT.601 limited-range BGRA→I420, 4:2:0 chroma by averaging each 2×2 block. Straightforward integer
    // approximation (same range libx264 assumes for yuv420p); fast enough single-threaded for these sizes.
    private static unsafe void BgraToI420(byte* bgra, byte* yP, byte* uP, byte* vP, int w, int h)
    {
        int cw = w / 2;
        for (int y = 0; y < h; y++)
        {
            byte* row = bgra + (long)y * w * 4;
            byte* yr = yP + (long)y * w;
            for (int x = 0; x < w; x++)
            {
                byte b = row[x * 4 + 0], g = row[x * 4 + 1], r = row[x * 4 + 2];
                yr[x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
            }
        }
        for (int y = 0; y < h; y += 2)
        {
            byte* row0 = bgra + (long)y * w * 4;
            byte* row1 = bgra + (long)(y + 1) * w * 4;
            byte* ur = uP + (long)(y / 2) * cw;
            byte* vr = vP + (long)(y / 2) * cw;
            for (int x = 0; x < w; x += 2)
            {
                int i0 = x * 4, i1 = (x + 1) * 4;
                int b = row0[i0] + row0[i1] + row1[i0] + row1[i1];
                int g = row0[i0 + 1] + row0[i1 + 1] + row1[i0 + 1] + row1[i1 + 1];
                int r = row0[i0 + 2] + row0[i1 + 2] + row1[i0 + 2] + row1[i1 + 2];
                b >>= 2; g >>= 2; r >>= 2;    // 2×2 average
                ur[x / 2] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                vr[x / 2] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
        }
    }

    private static void Parse(IntPtr param, string name, string value)
    {
        int rc = X264.x264_param_parse(param, name, value);
        if (rc != 0) throw new InvalidOperationException($"x264_param_parse({name}={value}) failed: {rc}");
    }

    // width/height live at known offsets in x264_param_t; there is no string setter for them, so poke directly.
    private static unsafe void SetDimensions(IntPtr param, int w, int h)
    {
        *(int*)((byte*)param + OffParamWidth) = w;
        *(int*)((byte*)param + OffParamHeight) = h;
    }

    public ValueTask DisposeAsync()
    {
        lock (_encLock)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            try { if (_enc != IntPtr.Zero) X264.x264_encoder_close(_enc); } catch { }
            _enc = IntPtr.Zero;
            foreach (ref var p in new[] { _picIn, _picOut, _yPlane, _uPlane, _vPlane }.AsSpan())
                if (p != IntPtr.Zero) { try { Marshal.FreeHGlobal(p); } catch { } }
            _picIn = _picOut = _yPlane = _uPlane = _vPlane = IntPtr.Zero;
        }
        return ValueTask.CompletedTask;
    }

    // ---- x264 constants ----
    private const int X264_CSP_I420 = 0x0002;   // yuv 4:2:0 planar (0x0001 is I400 mono — do not confuse)

    // Struct sizes: allocate generously so we never under-allocate across libx264 build numbers (the structs
    // only grow, and we only touch fields at the stable leading offsets below). x264_param_t is ~1KB;
    // x264_picture_t is a few hundred bytes. 4096 bytes is a safe over-allocation for both.
    private const int X264ParamSize = 4096;
    private const int X264PictureSize = 1024;

    // ---- Public-ABI field offsets (LP64: x86-64 & arm64), verified against x264.h with offsetof ----
    // x264_param_t begins with {uint32_t cpu; int i_threads; int i_lookahead_threads; int b_sliced_threads;
    //   int b_deterministic; int b_cpu_independent; int i_sync_lookahead; int i_width; int i_height; int i_csp;}
    // → i_width at 28, i_height at 32. (There is no x264_param_parse key for the dimensions.)
    private const int OffParamWidth = 28;
    private const int OffParamHeight = 32;
    private const int OffParamCsp = 36;   // i_csp, immediately after i_height

    // x264_picture_t: {int i_type; int i_qpplus1; int i_pic_struct; int b_keyframe; int64_t i_pts; int64_t
    //   i_dts; x264_param_t* param; x264_image_t img; ...} → i_pts at 16, img at 40.
    private const int OffPicIPts = 16;
    private const int OffPicImg = 40;

    // x264_image_t: {int i_csp; int i_plane; int i_stride[4]; uint8_t* plane[4];}
    // → i_csp 0, i_plane 4, i_stride 8, plane 24 (8-byte aligned after the 4-int header of 24 bytes).
    private const int OffImgICsp = 0;
    private const int OffImgIPlane = 4;
    private const int OffImgIStride = 8;
    private const int OffImgPlane = 24;

    // x264_nal_t: {int i_ref_idc; int i_type; int b_long_startcode; int i_first_mb; int i_last_mb;
    //   int i_payload; uint8_t* p_payload; ...} → i_payload 20, p_payload 24 (pointer 8-byte aligned).
    private const int OffNalPayload = 24;
}

/// <summary>
/// P/Invoke surface for libx264 (<c>x264.h</c>), resolved by hand. libx264 <b>versions its exported symbols
/// with the build number</b> — the entry points are <c>x264_encoder_open_164</c>, <c>x264_encoder_encode</c>
/// (unsuffixed), <c>x264_encoder_close</c>, etc., and crucially <c>x264_encoder_open</c> exists ONLY as the
/// build-suffixed name. A plain <c>[DllImport]</c> can't express "try <c>x264_encoder_open_164</c> then
/// <c>_163</c> …", so we load the shared object once and bind each symbol to a function pointer, probing the
/// build suffix for the two functions that carry it. <see cref="Init"/> must run before any call.
/// </summary>
internal static unsafe class X264
{
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int> _paramDefaultPreset;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int> _paramParse;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _encoderOpen;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _encoderClose;
    private static delegate* unmanaged[Cdecl]<IntPtr, out IntPtr, out int, IntPtr, IntPtr, int> _encoderEncode;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _pictureInit;

    private static readonly object _initLock = new();
    private static bool _initialized;

    /// <summary>Loads libx264 and binds every entry point (probing the build-suffixed symbols). Idempotent.</summary>
    public static void Init()
    {
        lock (_initLock)
        {
            if (_initialized) return;
            IntPtr lib = LoadLib();
            if (lib == IntPtr.Zero) throw new DllNotFoundException("libx264 shared library not found");

            _paramDefaultPreset = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int>)Require(lib, "x264_param_default_preset");
            _paramParse = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int>)Require(lib, "x264_param_parse");
            _encoderClose = (delegate* unmanaged[Cdecl]<IntPtr, void>)Require(lib, "x264_encoder_close");
            _encoderEncode = (delegate* unmanaged[Cdecl]<IntPtr, out IntPtr, out int, IntPtr, IntPtr, int>)Require(lib, "x264_encoder_encode");
            _pictureInit = (delegate* unmanaged[Cdecl]<IntPtr, void>)Require(lib, "x264_picture_init");
            // x264_encoder_open is ALWAYS build-suffixed (x264_encoder_open_<build>); probe the range.
            _encoderOpen = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)RequireOpen(lib);

            _initialized = true;
        }
    }

    // Resolves libx264 across Linux and macOS. First tries the bare names (works when the lib is on the
    // loader's default search path — most Linux installs, or a co-deployed lib), then walks the version-
    // suffixed sonames/dylibs, and finally probes well-known absolute install dirs (Homebrew on both Apple-
    // silicon and Intel, plus the standard Linux lib dirs) so the server finds libx264 without the operator
    // having to set LD_LIBRARY_PATH/DYLD_LIBRARY_PATH.
    private static IntPtr LoadLib()
    {
        // 1) bare names on the default search path
        foreach (var candidate in new[] { "libx264.so", "x264", "libx264.dll", "libx264.dylib" })
            if (NativeLibrary.TryLoad(candidate, out var h)) return h;
        // 2) version-suffixed names on the default search path
        for (int v = 170; v >= 148; v--)
        {
            if (NativeLibrary.TryLoad($"libx264.so.{v}", out var h)) return h;
            if (NativeLibrary.TryLoad($"libx264.{v}.dylib", out var h2)) return h2;
        }
        // 3) well-known absolute install dirs
        string[] dirs = { "/opt/homebrew/lib", "/usr/local/lib", "/usr/lib", "/usr/lib/x86_64-linux-gnu", "/lib" };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var name in new[] { "libx264.dylib", "libx264.so" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p) && NativeLibrary.TryLoad(p, out var h)) return h;
            }
            // versioned files present in the dir (libx264.165.dylib / libx264.so.165)
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "libx264.*"))
                    if ((f.EndsWith(".dylib") || f.Contains(".so")) && NativeLibrary.TryLoad(f, out var h)) return h;
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private static IntPtr Require(IntPtr lib, string sym)
    {
        if (NativeLibrary.TryGetExport(lib, sym, out var p)) return p;
        throw new EntryPointNotFoundException($"libx264 symbol {sym} not found");
    }

    // x264_encoder_open exists only as x264_encoder_open_<build>. Probe plausible build numbers.
    private static IntPtr RequireOpen(IntPtr lib)
    {
        if (NativeLibrary.TryGetExport(lib, "x264_encoder_open", out var plain)) return plain;
        for (int v = 170; v >= 148; v--)
            if (NativeLibrary.TryGetExport(lib, $"x264_encoder_open_{v}", out var p)) return p;
        throw new EntryPointNotFoundException("libx264 x264_encoder_open_<build> not found");
    }

    // Thin managed wrappers that marshal strings to null-terminated UTF-8 and forward to the bound pointers.
    public static int x264_param_default_preset(IntPtr param, string preset, string tune)
    {
        var pb = Utf8(preset); var tb = Utf8(tune);
        fixed (byte* pp = pb) fixed (byte* tp = tb) return _paramDefaultPreset(param, pp, tp);
    }
    public static int x264_param_parse(IntPtr param, string name, string value)
    {
        var nb = Utf8(name); var vb = Utf8(value);
        fixed (byte* np = nb) fixed (byte* vp = vb) return _paramParse(param, np, vp);
    }
    public static IntPtr x264_encoder_open(IntPtr param) => _encoderOpen(param);
    public static void x264_encoder_close(IntPtr enc) => _encoderClose(enc);
    public static int x264_encoder_encode(IntPtr enc, out IntPtr ppNal, out int piNal, IntPtr picIn, IntPtr picOut)
        => _encoderEncode(enc, out ppNal, out piNal, picIn, picOut);
    public static void x264_picture_init(IntPtr pic) => _pictureInit(pic);

    private static byte[] Utf8(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var z = new byte[bytes.Length + 1];
        Array.Copy(bytes, z, bytes.Length);
        return z;
    }
}

/// <summary>
/// <see cref="IH264EncoderFactory"/> that hands out in-process <see cref="Libx264H264Encoder"/> instances
/// (direct libx264 P/Invoke, no ffmpeg subprocess). Swap the DI registration in <c>Program.cs</c> from
/// <see cref="FfmpegH264EncoderFactory"/> to this to select the libx264 backend.
/// </summary>
internal sealed class Libx264H264EncoderFactory : IH264EncoderFactory
{
    public IH264Encoder Create(int width, int height, int fps, int crf, int maxKbps, ILogger logger)
    {
        X264.Init();   // idempotent: loads libx264 + binds the build-suffixed symbols on first use
        return new Libx264H264Encoder(width, height, fps, logger, crf, maxKbps);
    }
}
