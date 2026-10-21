namespace PictureSharp.Formats.Tiff.Container;

/// <summary>One IFD entry whose <see cref="Count"/> values, <see cref="Size"/> bytes each, lie inside the file at <see cref="ValueOffset"/>.</summary>
internal readonly record struct TiffEntry(ushort Tag, ushort Type, uint Count, int ValueOffset)
{
    public const ushort Byte = 1;
    public const ushort Ascii = 2;
    public const ushort Short = 3;
    public const ushort Long = 4;
    public const ushort Rational = 5;
    public const ushort Undefined = 7;

    public int Size => TypeSize(Type);

    /// <summary>The byte size of one value of a classic TIFF field type; 0 for unknown types, which readers skip.</summary>
    public static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };
}
