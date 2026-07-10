using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// JPEG decode for SPICE display images (IMAGE_TYPE_JPEG / JPEG_ALPHA), via ImageSharp. SPICE sends
/// baseline JPEG for the RGB; JPEG_ALPHA additionally carries an LZ-compressed alpha plane the caller
/// decodes and passes in. Output is top-down BGRA (w*h*4).
/// </summary>
internal static class SpiceJpeg
{
    /// <summary>
    /// Decodes a baseline JPEG to top-down BGRA. If <paramref name="alphaBgra"/> is non-null (an LZ-decoded
    /// alpha plane of the same dimensions, itself BGRA where the alpha lives in its blue byte per
    /// spice-html5), its alpha is applied; otherwise the image is opaque.
    /// </summary>
    public static (int w, int h, byte[] bgra)? DecodeToBgra(ReadOnlySpan<byte> jpeg, byte[]? alphaBgra)
    {
        try
        {
            using var img = Image.Load<Bgra32>(jpeg);
            int w = img.Width, h = img.Height;
            var outBuf = new byte[w * h * 4];
            img.CopyPixelDataTo(outBuf);
            // ImageSharp gives us opaque BGRA (JPEG has no alpha); force alpha to 255 unless we have a plane.
            for (int i = 3; i < outBuf.Length; i += 4) outBuf[i] = 255;

            if (alphaBgra != null && alphaBgra.Length >= w * h * 4)
            {
                // The LZ alpha plane stores the coverage in each pixel; spice-html5 uses its blue channel.
                for (int p = 0, a = 0; p < outBuf.Length; p += 4, a += 4)
                    outBuf[p + 3] = alphaBgra[a];
            }
            return (w, h, outBuf);
        }
        catch
        {
            return null;
        }
    }
}
