using System.Buffers;

namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// Holds strip data for a stream that cannot seek, in pooled 1 MiB blocks, until the header can be written in front of
/// it: the header points at the IFD, which follows the data.
/// </summary>
internal sealed class TiffSpool : IDisposable
{
    private const int BlockSize = 1 << 20;
    private readonly List<byte[]> _blocks = [];
    private int _used = BlockSize;

    public long Length { get; private set; }

    public void Write(ReadOnlySpan<byte> data)
    {
        Length += data.Length;
        while (!data.IsEmpty)
        {
            if (_used == BlockSize)
            {
                _blocks.Add(ArrayPool<byte>.Shared.Rent(BlockSize));
                _used = 0;
            }

            int count = Math.Min(data.Length, BlockSize - _used);
            data[..count].CopyTo(_blocks[^1].AsSpan(_used));
            _used += count;
            data = data[count..];
        }
    }

    public void CopyTo(Stream output)
    {
        for (int i = 0; i < _blocks.Count; i++)
        {
            output.Write(_blocks[i], 0, i == _blocks.Count - 1 ? _used : BlockSize);
        }
    }

    public void Dispose()
    {
        foreach (byte[] block in _blocks)
        {
            ArrayPool<byte>.Shared.Return(block);
        }

        _blocks.Clear();
    }
}
