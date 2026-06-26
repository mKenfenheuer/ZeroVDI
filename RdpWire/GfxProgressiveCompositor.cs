// GfxProgressiveCompositor — server-side surface compositor for RemoteFX Progressive desktop streams.
//
// GNOME Remote Desktop streams the desktop as RemoteFX Progressive over GFX WIRE_TO_SURFACE_2. To
// RECORD such a session we must decode it server-side (the recorder otherwise only remuxes H.264).
// This composites decoded 64x64 progressive tiles into a per-surface BGRA framebuffer following the
// GFX surface/output model, and emits one full BGRA frame per END_FRAME to the media sink, which an
// H.264 encoder turns into desktop.mp4 — just like the AVC path, only decoded+re-encoded here.
//
// Geometry: RESET_GRAPHICS sets the output size; CREATE_SURFACE makes an off-screen surface;
// MAP_SURFACE_TO_OUTPUT binds a surface's (0,0) to an output origin. We emit the surface that is
// mapped to output (0,0) — the visible desktop — sized to the output. Tiles land at (xIdx*64, yIdx*64).

using System;

namespace KSol.RDPGateway.RDP;

internal sealed class GfxProgressiveCompositor
{
    private sealed class Surface
    {
        public int Width, Height;
        public byte[] Bgra = Array.Empty<byte>(); // top-down, stride = Width*4
        public RfxProgressiveDecoder Decoder = new();
        public int OriginX, OriginY;
        public bool Mapped;
        public bool Dirty; // a progressive tile was written since the last emit
    }

    private readonly System.Collections.Generic.Dictionary<int, Surface> _surfaces = new();
    private int _outputWidth, _outputHeight;
    private bool _everComposited;

    /// <summary>True if any progressive tile was ever decoded — i.e. this is a progressive session.</summary>
    public bool HasProgressive => _everComposited;

    public void OnResetGraphics(int width, int height)
    {
        _outputWidth = width; _outputHeight = height;
    }

    public void OnCreateSurface(int surfaceId, int width, int height)
    {
        var s = new Surface { Width = width, Height = height, Bgra = new byte[Math.Max(0, width * height * 4)] };
        // Opaque black to start (alpha 255).
        for (int i = 3; i < s.Bgra.Length; i += 4) s.Bgra[i] = 255;
        _surfaces[surfaceId] = s;
    }

    public void OnDeleteSurface(int surfaceId) => _surfaces.Remove(surfaceId);

    public void OnMapSurfaceToOutput(int surfaceId, int originX, int originY)
    {
        if (_surfaces.TryGetValue(surfaceId, out var s)) { s.OriginX = originX; s.OriginY = originY; s.Mapped = true; }
    }

    /// <summary>
    /// Decode a WIRE_TO_SURFACE_2 progressive payload into the surface framebuffer. Returns true if at
    /// least one tile was composited.
    /// </summary>
    public bool OnProgressive(int surfaceId, ReadOnlySpan<byte> payload)
    {
        if (!_surfaces.TryGetValue(surfaceId, out var s) || s.Width <= 0 || s.Height <= 0) return false;
        int stride = s.Width * 4;
        int n = s.Decoder.Decode(payload, (xIdx, yIdx, bgra) =>
        {
            int x0 = xIdx * 64, y0 = yIdx * 64;
            int w = Math.Min(64, s.Width - x0);
            int h = Math.Min(64, s.Height - y0);
            if (w <= 0 || h <= 0) return;
            for (int row = 0; row < h; row++)
            {
                int dst = (y0 + row) * stride + x0 * 4;
                int src = row * 64 * 4;
                Buffer.BlockCopy(bgra, src, s.Bgra, dst, w * 4);
            }
        });
        if (n > 0) { s.Dirty = true; _everComposited = true; }
        return n > 0;
    }

    /// <summary>
    /// At END_FRAME: emit the visible desktop (the surface mapped to output 0,0, or the only/first dirty
    /// surface) as one BGRA frame. No-op if nothing was composited this frame.
    /// </summary>
    public void OnEndFrame(IRdpMediaSink sink, long timestampMs)
    {
        Surface? best = null;
        foreach (var s in _surfaces.Values)
        {
            if (!s.Dirty) continue;
            if (s.Mapped && s.OriginX == 0 && s.OriginY == 0) { best = s; break; }
            best ??= s;
        }
        if (best == null) return;
        sink.OnDesktopRawFrame(best.Width, best.Height, best.Bgra, timestampMs);
        foreach (var s in _surfaces.Values) s.Dirty = false;
    }

    public void Reset()
    {
        _surfaces.Clear(); _outputWidth = _outputHeight = 0; _everComposited = false;
    }
}
