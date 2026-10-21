using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>The fixed-point YCbCr to RGB tables of libtiff, built from YCbCrCoefficients and ReferenceBlackWhite.</summary>
internal sealed class YCbCrToRgb
{
    private const int Shift = 16;
    private const int OneHalf = 1 << (Shift - 1);

    private readonly int[] crR = new int[256];
    private readonly int[] cbB = new int[256];
    private readonly int[] crG = new int[256];
    private readonly int[] cbG = new int[256];
    private readonly int[] y = new int[256];

    // port: libtiff/tif_getimage.c initYCbCrConversion and tif_color.c TIFFYCbCrToRGBInit
    public YCbCrToRgb(ReadOnlySpan<byte> data, TiffDirectory ifd)
    {
        float[] luma = Floats(data, ifd, TiffTag.YCbCrCoefficients, [0.299f, 0.587f, 0.114f]);
        float[] refBlackWhite = Floats(data, ifd, TiffTag.ReferenceBlackWhite, [0f, 255f, 128f, 255f, 128f, 255f]);
        if (float.IsNaN(luma[0]) || float.IsNaN(luma[1]) || luma[1] == 0 || float.IsNaN(luma[2]))
        {
            throw TiffDecoder.Error("The TIFF YCbCrCoefficients are invalid.");
        }

        foreach (float f in refBlackWhite)
        {
            if (!(f > -0x7FFFFFFF + 128) || !(f < 0x7FFFFFFF))
            {
                throw TiffDecoder.Error("The TIFF ReferenceBlackWhite values are invalid.");
            }
        }

        float f1 = 2 - 2 * luma[0];
        int d1 = Fix(Clamp(f1, 0f, 2f));
        float f2 = luma[0] * f1 / luma[1];
        int d2 = -Fix(Clamp(f2, 0f, 2f));
        float f3 = 2 - 2 * luma[2];
        int d3 = Fix(Clamp(f3, 0f, 2f));
        float f4 = luma[2] * f3 / luma[1];
        int d4 = -Fix(Clamp(f4, 0f, 2f));
        for (int i = 0, x = -128; i < 256; i++, x++)
        {
            int cr = (int)ClampW(Code2V(x, refBlackWhite[4] - 128f, refBlackWhite[5] - 128f, 127), -128f * 32, 128f * 32);
            int cb = (int)ClampW(Code2V(x, refBlackWhite[2] - 128f, refBlackWhite[3] - 128f, 127), -128f * 32, 128f * 32);
            crR[i] = (d1 * cr + OneHalf) >> Shift;
            cbB[i] = (d3 * cb + OneHalf) >> Shift;
            crG[i] = d2 * cr;
            cbG[i] = d4 * cb + OneHalf;
            y[i] = (int)ClampW(Code2V(x + 128, refBlackWhite[0], refBlackWhite[1], 255), -128f * 32, 128f * 32);
        }
    }

    // port: libtiff/tif_color.c TIFFYCbCrtoRGB for 8-bit samples.
    public Rgba32 Convert(int luma, int cb, int cr)
    {
        int yv = y[luma];
        return new Rgba32(
            (byte)Math.Clamp(yv + crR[cr], 0, 255),
            (byte)Math.Clamp(yv + ((cbG[cb] + crG[cr]) >> Shift), 0, 255),
            (byte)Math.Clamp(yv + cbB[cb], 0, 255));
    }

    // libtiff reads the RATIONAL tags as floats; a tag with the wrong count or type keeps the default.
    private static float[] Floats(ReadOnlySpan<byte> data, TiffDirectory ifd, ushort tag, float[] fallback)
    {
        if (!ifd.TryGet(tag, out TiffEntry entry) || entry.Type != TiffEntry.Rational || entry.Count < fallback.Length)
        {
            return fallback;
        }

        var values = new float[fallback.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (float)ifd.GetRational(data, entry, i);
        }

        return values;
    }

    // port: libtiff/tif_color.c FIX: float times 2^16 in single precision, then rounded in double precision.
    private static int Fix(float x) => (int)(x * (float)(1L << Shift) + 0.5);

    // port: libtiff/tif_color.c CLAMP: written so that NaN gives the minimum.
    private static float Clamp(float f, float min, float max) => !(f >= min) ? min : f > max ? max : f;

    private static float ClampW(float v, float min, float max) => v < min ? min : v > max ? max : v;

    private static float Code2V(int c, float rb, float rw, float cr) =>
        (c - (int)rb) * cr / (rw - rb != 0 ? rw - rb : 1);
}
