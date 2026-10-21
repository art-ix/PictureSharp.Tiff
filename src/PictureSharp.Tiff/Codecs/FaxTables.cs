namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// The CCITT T.4/T.6 decoding state tables, indexed by the next 7 (main), 12 (white) or 13 (black) input bits taken
/// LSB first. Built at start-up from the code lists instead of being stored.
/// </summary>
// port: libtiff/mkg3states.c, which generates libtiff/tif_fax3sm.c from the same lists with FillTable.
internal static class FaxTables
{
    public const byte Null = 0;
    public const byte Pass = 1;
    public const byte Horiz = 2;
    public const byte V0 = 3;
    public const byte VR = 4;
    public const byte VL = 5;
    public const byte Ext = 6;
    public const byte TermW = 7;
    public const byte TermB = 8;
    public const byte MakeUpW = 9;
    public const byte MakeUpB = 10;
    public const byte MakeUp = 11;
    public const byte Eol = 12;

    public static readonly Entry[] Main = new Entry[128];
    public static readonly Entry[] White = new Entry[4096];
    public static readonly Entry[] Black = new Entry[8192];

    // Each pair is a code (right justified, LSB first, zero filled) and (pixel count << 4) + code width.
    private static readonly ushort[] PassCodes =
    [
        0x0008, 4,
    ];

    private static readonly ushort[] HorizCodes =
    [
        0x0004, 3,
    ];

    private static readonly ushort[] V0Codes =
    [
        0x0001, 1,
    ];

    private static readonly ushort[] VRCodes =
    [
        0x0006, 19, 0x0030, 38, 0x0060, 55,
    ];

    private static readonly ushort[] VLCodes =
    [
        0x0002, 19, 0x0010, 38, 0x0020, 55,
    ];

    private static readonly ushort[] ExtCodes =
    [
        0x0040, 7,
    ];

    private static readonly ushort[] EolVCodes =
    [
        0x0000, 7,
    ];

    private static readonly ushort[] MakeUpWCodes =
    [
        0x001b, 1029, 0x0009, 2053, 0x003a, 3078, 0x0076, 4103, 0x006c, 5128, 0x00ec, 6152,
        0x0026, 7176, 0x00a6, 8200, 0x0016, 9224, 0x00e6, 10248, 0x0066, 11273, 0x0166, 12297,
        0x0096, 13321, 0x0196, 14345, 0x0056, 15369, 0x0156, 16393, 0x00d6, 17417, 0x01d6, 18441,
        0x0036, 19465, 0x0136, 20489, 0x00b6, 21513, 0x01b6, 22537, 0x0032, 23561, 0x0132, 24585,
        0x00b2, 25609, 0x0006, 26630, 0x01b2, 27657,
    ];

    private static readonly ushort[] MakeUpBCodes =
    [
        0x03c0, 1034, 0x0130, 2060, 0x0930, 3084, 0x0da0, 4108, 0x0cc0, 5132, 0x02c0, 6156,
        0x0ac0, 7180, 0x06c0, 8205, 0x16c0, 9229, 0x0a40, 10253, 0x1a40, 11277, 0x0640, 12301,
        0x1640, 13325, 0x09c0, 14349, 0x19c0, 15373, 0x05c0, 16397, 0x15c0, 17421, 0x0dc0, 18445,
        0x1dc0, 19469, 0x0940, 20493, 0x1940, 21517, 0x0540, 22541, 0x1540, 23565, 0x0b40, 24589,
        0x1b40, 25613, 0x04c0, 26637, 0x14c0, 27661,
    ];

    private static readonly ushort[] MakeUpCodes =
    [
        0x0080, 28683, 0x0180, 29707, 0x0580, 30731, 0x0480, 31756, 0x0c80, 32780, 0x0280, 33804,
        0x0a80, 34828, 0x0680, 35852, 0x0e80, 36876, 0x0380, 37900, 0x0b80, 38924, 0x0780, 39948,
        0x0f80, 40972,
    ];

    private static readonly ushort[] TermWCodes =
    [
        0x00ac, 8, 0x0038, 22, 0x000e, 36, 0x0001, 52, 0x000d, 68, 0x0003, 84,
        0x0007, 100, 0x000f, 116, 0x0019, 133, 0x0005, 149, 0x001c, 165, 0x0002, 181,
        0x0004, 198, 0x0030, 214, 0x000b, 230, 0x002b, 246, 0x0015, 262, 0x0035, 278,
        0x0072, 295, 0x0018, 311, 0x0008, 327, 0x0074, 343, 0x0060, 359, 0x0010, 375,
        0x000a, 391, 0x006a, 407, 0x0064, 423, 0x0012, 439, 0x000c, 455, 0x0040, 472,
        0x00c0, 488, 0x0058, 504, 0x00d8, 520, 0x0048, 536, 0x00c8, 552, 0x0028, 568,
        0x00a8, 584, 0x0068, 600, 0x00e8, 616, 0x0014, 632, 0x0094, 648, 0x0054, 664,
        0x00d4, 680, 0x0034, 696, 0x00b4, 712, 0x0020, 728, 0x00a0, 744, 0x0050, 760,
        0x00d0, 776, 0x004a, 792, 0x00ca, 808, 0x002a, 824, 0x00aa, 840, 0x0024, 856,
        0x00a4, 872, 0x001a, 888, 0x009a, 904, 0x005a, 920, 0x00da, 936, 0x0052, 952,
        0x00d2, 968, 0x004c, 984, 0x00cc, 1000, 0x002c, 1016,
    ];

    private static readonly ushort[] TermBCodes =
    [
        0x03b0, 10, 0x0002, 19, 0x0003, 34, 0x0001, 50, 0x0006, 67, 0x000c, 84,
        0x0004, 100, 0x0018, 117, 0x0028, 134, 0x0008, 150, 0x0010, 167, 0x0050, 183,
        0x0070, 199, 0x0020, 216, 0x00e0, 232, 0x0030, 249, 0x03a0, 266, 0x0060, 282,
        0x0040, 298, 0x0730, 315, 0x00b0, 331, 0x01b0, 347, 0x0760, 363, 0x00a0, 379,
        0x0740, 395, 0x00c0, 411, 0x0530, 428, 0x0d30, 444, 0x0330, 460, 0x0b30, 476,
        0x0160, 492, 0x0960, 508, 0x0560, 524, 0x0d60, 540, 0x04b0, 556, 0x0cb0, 572,
        0x02b0, 588, 0x0ab0, 604, 0x06b0, 620, 0x0eb0, 636, 0x0360, 652, 0x0b60, 668,
        0x05b0, 684, 0x0db0, 700, 0x02a0, 716, 0x0aa0, 732, 0x06a0, 748, 0x0ea0, 764,
        0x0260, 780, 0x0a60, 796, 0x04a0, 812, 0x0ca0, 828, 0x0240, 844, 0x0ec0, 860,
        0x01c0, 876, 0x0e40, 892, 0x0140, 908, 0x01a0, 924, 0x09a0, 940, 0x0d40, 956,
        0x0340, 972, 0x05a0, 988, 0x0660, 1004, 0x0e60, 1020,
    ];

    private static readonly ushort[] EolHCodes =
    [
        0x0000, 11,
    ];

    /// <summary>
    /// The white and black run codes for encoding as (code MSB first, length), indexed by run length for 0–63 and by
    /// 63 + run / 64 for the make-up runs 64–2560.
    /// </summary>
    // port: libtiff/t4.h TIFFFaxWhiteCodes and TIFFFaxBlackCodes, built from the decoding lists above.
    public static readonly (ushort Code, byte Length)[] WhiteCodes = RunCodes(TermWCodes, MakeUpWCodes);

    public static readonly (ushort Code, byte Length)[] BlackCodes = RunCodes(TermBCodes, MakeUpBCodes);

    static FaxTables()
    {
        Fill(Main, 7, PassCodes, Pass);
        Fill(Main, 7, HorizCodes, Horiz);
        Fill(Main, 7, V0Codes, V0);
        Fill(Main, 7, VRCodes, VR);
        Fill(Main, 7, VLCodes, VL);
        Fill(Main, 7, ExtCodes, Ext);
        Fill(Main, 7, EolVCodes, Eol);
        Fill(White, 12, MakeUpWCodes, MakeUpW);
        Fill(White, 12, MakeUpCodes, MakeUp);
        Fill(White, 12, TermWCodes, TermW);
        Fill(White, 12, EolHCodes, Eol);
        Fill(Black, 13, MakeUpBCodes, MakeUpB);
        Fill(Black, 13, MakeUpCodes, MakeUp);
        Fill(Black, 13, TermBCodes, TermB);
        Fill(Black, 13, EolHCodes, Eol);
    }

    // port: libtiff/mkg3states.c FillTable
    private static void Fill(Entry[] table, int size, ushort[] codes, byte state)
    {
        int limit = 1 << size;
        for (int i = 0; i < codes.Length; i += 2)
        {
            int width = codes[i + 1] & 15;
            int param = codes[i + 1] >> 4;
            for (int code = codes[i]; code < limit; code += 1 << width)
            {
                table[code] = new Entry(state, (byte)width, param);
            }
        }
    }

    private static (ushort Code, byte Length)[] RunCodes(ushort[] terminating, ushort[] makeUp)
    {
        var table = new (ushort, byte)[64 + 40];
        foreach (ushort[] codes in new[] { terminating, makeUp, MakeUpCodes })
        {
            for (int i = 0; i < codes.Length; i += 2)
            {
                int width = codes[i + 1] & 15, run = codes[i + 1] >> 4;
                int reversed = 0;
                for (int b = 0; b < width; b++)
                {
                    reversed |= ((codes[i] >> b) & 1) << (width - 1 - b);
                }

                table[run < 64 ? run : 63 + (run >> 6)] = ((ushort)reversed, (byte)width);
            }
        }

        return table;
    }

    public readonly record struct Entry(byte State, byte Width, int Param);
}
