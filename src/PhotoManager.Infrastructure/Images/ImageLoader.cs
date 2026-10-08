using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoManager.Application.Catalog;
using PhotoManager.Infrastructure.Media;

namespace PhotoManager.Infrastructure.Images;

/// <summary>Abre imagens para miniatura/preview/dimensões, tratando RAW (JPEG embutido + orientação) e formatos comuns de forma uniforme.</summary>
public static class ImageLoader
{
    /// <summary>
    /// Quadro de um vídeo pelo Windows, já em pé. O Windows nem sempre aplica a rotação do contêiner (vídeos de drone e alguns de celular chegam deitados):
    /// só giramos quando o arquivo manda exibir em retrato (90° ou 270°) e o quadro ainda está deitado, para não girar duas vezes.
    /// </summary>
    private static BitmapSource? VideoFrame(string path, int size)
    {
        var frame = ShellThumbnail.TryGet(path, size);
        if (frame is null) return null;
        var rotation = Mp4Info.TryRead(path)?.RotationDegrees ?? 0;
        return rotation is 90 or 270 && frame.PixelWidth > frame.PixelHeight ? ExifOrientation.Rotate(frame, rotation) : frame;
    }

    /// <summary>Quadro completo (para gerar miniaturas), já girado conforme a orientação do RAW.</summary>
    public static BitmapSource LoadFrame(string path)
    {
        if (MediaFormats.IsVideo(path)) return VideoFrame(path, 512) ?? throw new NotSupportedException("O Windows não gerou uma miniatura para este vídeo.");
        if (ImageFormats.IsRaw(path))
        {
            var preview = RawPreview.TryRead(path) ?? throw new NotSupportedException("O arquivo RAW não contém uma visualização JPEG utilizável.");
            using var stream = new MemoryStream(preview.Jpeg);
            BitmapSource frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            if (preview.RotationDegrees != 0) frame = new TransformedBitmap(frame, new RotateTransform(preview.RotationDegrees));
            frame.Freeze();
            return frame;
        }
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return ExifOrientation.Apply(decoder.Frames[0], ExifOrientation.Read(path));
    }

    /// <summary>Imagem reduzida e congelada para o painel de preview; o arquivo é liberado ao final. Nulo se não puder ser decodificada.</summary>
    public static BitmapSource? LoadPreview(string path, int decodePixelWidth)
    {
        if (MediaFormats.IsVideo(path)) return VideoFrame(path, decodePixelWidth > 0 ? decodePixelWidth : 960);
        try
        {
            var bitmap = new BitmapImage();
            if (ImageFormats.IsRaw(path))
            {
                var preview = RawPreview.TryRead(path);
                if (preview is null) return null;
                bitmap.BeginInit();
                bitmap.StreamSource = new MemoryStream(preview.Jpeg);
                bitmap.Rotation = preview.RotationDegrees switch { 90 => Rotation.Rotate90, 180 => Rotation.Rotate180, 270 => Rotation.Rotate270, _ => Rotation.Rotate0 };
            }
            else
            {
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
            }
            if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return ImageFormats.IsRaw(path) ? bitmap : ExifOrientation.Apply(bitmap, ExifOrientation.Read(path));   // RAW já vem girado pelo EXIF do próprio RawPreview
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Resolução natural para zoom/100 % no modo de revisão. Imagens acima de <paramref name="maxPixels"/> são reduzidas para esse limite
    /// (evita centenas de MB por foto); nunca amplia. Nulo se não puder ser decodificada.
    /// </summary>
    public static BitmapSource? LoadFull(string path, long maxPixels = 40_000_000)
    {
        if (MediaFormats.IsVideo(path)) return VideoFrame(path, 1920);   // vídeo: quadro de pôster; a reprodução é do player
        try
        {
            // Só o cabeçalho é lido para saber o tamanho natural (CacheOption.None não decodifica os pixels).
            int width, height;
            if (ImageFormats.IsRaw(path))
            {
                if (RawPreview.TryRead(path) is not { } raw) return null;
                using var stream = new MemoryStream(raw.Jpeg);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None).Frames[0];
                (width, height) = (frame.PixelWidth, frame.PixelHeight);
            }
            else
            {
                var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.None).Frames[0];
                (width, height) = (frame.PixelWidth, frame.PixelHeight);
            }
            var pixels = (long)width * height;
            var decodeWidth = pixels > maxPixels ? (int)(width * Math.Sqrt((double)maxPixels / pixels)) : 0;
            return LoadPreview(path, decodeWidth);
        }
        catch (Exception) { return null; }
    }


    /// <summary>Dimensões como exibidas. Para RAW usa o tamanho real do sensor (EXIF) e, se ausente, o da visualização embutida.</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        if (!ImageFormats.IsRaw(path))
        {
            var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            return ExifOrientation.SwapsDimensions(ExifOrientation.Read(path)) ? (frame.PixelHeight, frame.PixelWidth) : (frame.PixelWidth, frame.PixelHeight);
        }
        var preview = RawPreview.TryRead(path) ?? throw new NotSupportedException("O arquivo RAW não contém uma visualização JPEG utilizável.");
        var swap = preview.RotationDegrees is 90 or 270;
        try
        {
            var sub = ImageMetadataReader.ReadMetadata(path).OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (sub is not null && sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w) && sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h) && w > 0 && h > 0)
                return swap ? (h, w) : (w, h);
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException) { }
        using var stream = new MemoryStream(preview.Jpeg);
        var fallback = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return swap ? (fallback.PixelHeight, fallback.PixelWidth) : (fallback.PixelWidth, fallback.PixelHeight);
    }
}
