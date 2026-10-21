namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>A read-only, seekable stream over memory, so <see cref="System.IO.Compression.ZLibStream"/> can read strips without copying them.</summary>
internal sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> data) : Stream
{
    private int position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => position;
        set => position = (int)Math.Clamp(value, 0, data.Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count = Math.Min(buffer.Length, data.Length - position);
        data.Span.Slice(position, count).CopyTo(buffer);
        position += count;
        return count;
    }

    public override int ReadByte() => position < data.Length ? data.Span[position++] : -1;

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => position + offset,
        _ => data.Length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
