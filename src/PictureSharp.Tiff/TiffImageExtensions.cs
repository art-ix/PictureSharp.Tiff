namespace PictureSharp.Formats.Tiff;

/// <summary>TIFF shortcuts for <see cref="Image"/>; require <see cref="TiffFormat"/> to be registered.</summary>
public static class TiffImageExtensions
{
    extension(Image image)
    {
        /// <summary>Saves the image as TIFF; null options mean the defaults. Animations keep the first frame only.</summary>
        public void SaveAsTiff(string path, TiffEncoderOptions? options = null) => image.Save(path, options ?? new());

        /// <summary>Writes the image to a stream as TIFF; null options mean the defaults. The stream is left open.</summary>
        public void SaveAsTiff(Stream stream, TiffEncoderOptions? options = null) => image.Save(stream, options ?? new());

        /// <summary>Saves the image as TIFF asynchronously; null options mean the defaults.</summary>
        public Task SaveAsTiffAsync(string path, TiffEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(path, options ?? new(), cancellationToken);

        /// <summary>Writes the image to a stream as TIFF asynchronously; null options mean the defaults.</summary>
        public Task SaveAsTiffAsync(Stream stream, TiffEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(stream, options ?? new(), cancellationToken);

        /// <summary>Encodes the image as TIFF; null options mean the defaults.</summary>
        public byte[] ToTiff(TiffEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
