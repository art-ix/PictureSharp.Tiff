namespace PictureSharp.Formats.Tiff.Container;

/// <summary>One IFD entry whose <see cref="Count"/> values lie inside the file at <see cref="ValueOffset"/>.</summary>
internal readonly record struct TiffEntry(ushort Tag, ushort Type, uint Count, int ValueOffset)
{
    public const ushort Byte = 1;
    public const ushort Ascii = 2;
    public const ushort Short = 3;
    public const ushort Long = 4;
    public const ushort Rational = 5;
    public const ushort Undefined = 7;
    public const ushort Float = 11;
    public const ushort Double = 12;
    public const ushort Ifd = 13;
    public const ushort Long8 = 16;
    public const ushort SLong8 = 17;
    public const ushort Ifd8 = 18;

    public int Size(bool bigTiff) => TypeSize(Type, bigTiff);

    public static bool IsInteger(ushort type) => type is Byte or Short or Long or Ifd or Long8 or Ifd8;

    /// <summary>The byte size of one value of a field type; 0 for unknown types, which readers skip.</summary>
    public static int TypeSize(ushort type, bool bigTiff) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        16 or 17 or 18 when bigTiff => 8,
        _ => 0,
    };
}
