using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoManager.Application.Library;

namespace PhotoManager.Infrastructure.Images;

/// <summary>Decodifica uma imagem (normalmente a miniatura JPEG do cache) em tons de cinza de 8 bits, liberando o arquivo na hora.</summary>
public sealed class GrayImageReader : IGrayImageReader
{
    public Task<GrayImage?> ReadAsync(string imagePath, CancellationToken cancellationToken = default) => Task.Run<GrayImage?>(() =>
    {
        if (!File.Exists(imagePath)) return null;
        using var stream = File.OpenRead(imagePath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.PixelWidth > 512)                                                    // análise não precisa de mais que isso
            frame = new TransformedBitmap(frame, new ScaleTransform(512.0 / frame.PixelWidth, 512.0 / frame.PixelWidth));
        var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        var stride = gray.PixelWidth;
        var pixels = new byte[stride * gray.PixelHeight];
        gray.CopyPixels(pixels, stride, 0);
        return new GrayImage(pixels, gray.PixelWidth, gray.PixelHeight);
    }, cancellationToken);
}
