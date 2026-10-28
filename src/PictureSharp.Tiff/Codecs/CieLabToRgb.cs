using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// libtiff's CIE L*a*b* 1976 to RGB conversion for the display it calls sRGB: L*a*b* to XYZ relative to the file's
/// WhitePoint (CIE D50 by default), a matrix to linear light, then a 1501-entry gamma 2.4 table. Single precision
/// throughout, as libtiff computes it.
/// </summary>
// port: libtiff/tif_getimage.c display_sRGB and initCIELabConversion, tif_color.c TIFFCIELabToRGBInit,
// TIFFCIELabToXYZ, TIFFCIELab16ToXYZ and TIFFXYZToRGB.
internal sealed class CieLabToRgb
{
    private const int Range = 1500;

    // display_sRGB: light output for reference white and black, pixel value for white, gamma; equal for all three guns.
    private const float LightWhite = 100.0F;
    private const float LightBlack = 1.0F;
    private const uint PixelWhite = 255;
    private const float Gamma = 2.4F;

    private const float D50X = 96.4250F;
    private const float D50Y = 100.0F;
    private const float D50Z = 82.4680F;

    // display_sRGB d_mat: XYZ to luminance of the red, green and blue guns.
    private static ReadOnlySpan<float> Matrix => [3.2410F, -1.5374F, -0.4986F, -0.9692F, 1.8760F, 0.0416F, 0.0556F, -0.2040F, 1.0570F];

    // TIFFCIELabToRGBInit: rstep, gstep and bstep all come from the red gun's values, and the three tables are equal.
    private static readonly float Step = (LightWhite - LightBlack) / Range;
    private static readonly float[] Table = BuildTable();

    private readonly float x0;
    private readonly float y0;
    private readonly float z0;

    public CieLabToRgb(ReadOnlySpan<byte> data, TiffDirectory ifd)
    {
        // TIFFGetFieldDefaulted: CIE D50 as chromaticities, per the Adobe Photoshop TIFF Technical Note.
        float wx = D50X / (D50X + D50Y + D50Z), wy = D50Y / (D50X + D50Y + D50Z);
        if (ifd.TryGet(TiffTag.WhitePoint, out TiffEntry entry) && entry.Type == TiffEntry.Rational && entry.Count == 2)
        {
            (wx, wy) = (ifd.GetFloat(data, entry, 0), ifd.GetFloat(data, entry, 1));
        }

        if (wy == 0.0F)
        {
            throw TiffDecoder.Error("The TIFF WhitePoint tag is invalid.");
        }

        y0 = 100.0F;
        x0 = wx / wy * y0;
        z0 = (1.0F - wx - wy) / wy * y0;
    }

    /// <summary>Converts 8-bit samples: L* in 0–255, a* and b* as signed bytes.</summary>
    public Rgba32 Convert8(byte l, sbyte a, sbyte b) => Convert16((uint)l * 257, a * 256, b * 256);

    /// <summary>Converts 16-bit samples: L* in 0–65535, a* and b* as signed 16-bit values, 256 times the CIE values.</summary>
    public Rgba32 Convert16(uint l, int a, int b)
    {
        (float r, float g, float bl) = Luminance(l, a, b);
        return new Rgba32(Gun(r), Gun(g), Gun(bl), 255);
    }

    /// <summary>
    /// <see cref="Convert16"/> to 16-bit pixels: the same luminance, with the gamma 2.4 curve evaluated exactly instead of
    /// through the 1501-entry table, which would leave fewer than 1501 levels, and rounded once.
    /// </summary>
    public Rgba64 Convert16Wide(uint l, int a, int b)
    {
        (float r, float g, float bl) = Luminance(l, a, b);
        return new Rgba64(GunWide(r), GunWide(g), GunWide(bl));
    }

    private (float R, float G, float B) Luminance(uint l, int a, int b)
    {
        // TIFFCIELab16ToXYZ
        float lightness = (float)l * 100.0F / 65535.0F;
        float x, y, z, cby, tmp;
        if (lightness < 8.856F)
        {
            y = lightness * y0 / 903.292F;
            cby = 7.787F * (y / y0) + 16.0F / 116.0F;
        }
        else
        {
            cby = (lightness + 16.0F) / 116.0F;
            y = y0 * cby * cby * cby;
        }

        tmp = (float)a / 256.0F / 500.0F + cby;
        x = tmp < 0.2069F ? x0 * (tmp - 0.13793F) / 7.787F : x0 * tmp * tmp * tmp;
        tmp = cby - (float)b / 256.0F / 200.0F;
        z = tmp < 0.2069F ? z0 * (tmp - 0.13793F) / 7.787F : z0 * tmp * tmp * tmp;

        // TIFFXYZToRGB
        ReadOnlySpan<float> m = Matrix;
        return (m[0] * x + m[1] * y + m[2] * z, m[3] * x + m[4] * y + m[5] * z, m[6] * x + m[7] * y + m[8] * z);
    }

    private static ushort GunWide(float luminance)
    {
        // The clamping of Gun; the table holds PixelWhite * ((luminance - LightBlack) / (LightWhite - LightBlack))^(1 / Gamma).
        luminance = luminance > LightBlack ? luminance : LightBlack;
        luminance = luminance < LightWhite ? luminance : LightWhite;
        double v = Math.Pow((luminance - LightBlack) / (double)(LightWhite - LightBlack), 1.0 / Gamma);
        return (ushort)Math.Min(ushort.MaxValue, (int)(v * ushort.MaxValue + 0.5));
    }

    private static byte Gun(float luminance)
    {
        // TIFFmax and TIFFmin, in this order (NaN becomes the black level), then the table and RINT in double.
        luminance = luminance > LightBlack ? luminance : LightBlack;
        luminance = luminance < LightWhite ? luminance : LightWhite;
        int i = (int)((luminance - LightBlack) / Step);
        i = Range < i ? Range : i;
        double v = Table[i];
        uint value = (uint)(v > 0 ? v + 0.5 : v - 0.5);
        return (byte)Math.Min(value, PixelWhite);
    }

    private static float[] BuildTable()
    {
        var table = new float[Range + 1];
        double gamma = 1.0 / Gamma;
        for (int i = 0; i <= Range; i++)
        {
            table[i] = PixelWhite * (float)Math.Pow((double)i / Range, gamma);
        }

        return table;
    }
}
