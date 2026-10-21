namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// The byte order and variant of a TIFF file. BigTIFF (version 43) widens counts and offsets to 8 bytes, so an IFD
/// starts with an 8-byte entry count and each entry takes 20 bytes instead of 12.
/// </summary>
internal readonly record struct TiffHeader(bool BigEndian, bool BigTiff, ulong FirstIfd)
{
    public int OffsetSize => BigTiff ? 8 : 4;

    public int CountSize => BigTiff ? 8 : 2;

    public int EntrySize => BigTiff ? 20 : 12;

    public static TiffHeader Read(ReadOnlySpan<byte> data)
    {
        if (!TiffDecoder.HasSignature(data))
        {
            throw TiffDecoder.Error("Not a TIFF file.");
        }

        bool bigEndian = data[0] == 'M';
        if (TiffDirectory.U16(data, 2, bigEndian) == 42)
        {
            return data.Length >= 8
                ? new TiffHeader(bigEndian, false, TiffDirectory.U32(data, 4, bigEndian))
                : throw TiffDecoder.Error("The TIFF header is truncated.");
        }

        if (data.Length < 16)
        {
            throw TiffDecoder.Error("The BigTIFF header is truncated.");
        }

        // port: libtiff/tif_open.c TIFFClientOpenExt: the offset size must be 8 and the reserved field 0.
        if (TiffDirectory.U16(data, 4, bigEndian) != 8 || TiffDirectory.U16(data, 6, bigEndian) != 0)
        {
            throw TiffDecoder.Error("The BigTIFF header has an invalid offset size.");
        }

        return new TiffHeader(bigEndian, true, TiffDirectory.U64(data, 8, bigEndian));
    }

    public ulong ReadOffset(ReadOnlySpan<byte> data, int pos) =>
        BigTiff ? TiffDirectory.U64(data, pos, BigEndian) : TiffDirectory.U32(data, pos, BigEndian);
}
