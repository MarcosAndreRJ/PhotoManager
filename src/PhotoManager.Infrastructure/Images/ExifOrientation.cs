using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoManager.Application.Catalog;

namespace PhotoManager.Infrastructure.Images;

/// <summary>
/// Orientação EXIF (tag 0x0112) de JPEG: o celular grava os pixels deitados e só anota "gire 90°" no EXIF.
/// Sem aplicar isso, a miniatura, o preview, a tela cheia e as dimensões saem de lado e o filtro de orientação erra.
/// Valores 1-8 como na especificação; 1 = normal.
/// </summary>
public static class ExifOrientation
{
    /// <summary>Lê só os metadados (não decodifica pixels). Qualquer problema devolve 1 (sem rotação).</summary>
    public static int Read(string path)
    {
        if (!ImageFormats.IsJpeg(path)) return 1;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var ifd0 = ImageMetadataReader.ReadMetadata(stream).OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 is not null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var value) && value is >= 1 and <= 8) return value;
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException or UnauthorizedAccessException) { }
        return 1;
    }

    /// <summary>Orientações 5-8 trocam largura e altura (giram 90° ou 270°).</summary>
    public static bool SwapsDimensions(int orientation) => orientation is >= 5 and <= 8;

    /// <summary>Aplica a orientação aos pixels (rotações de 90° e espelhamentos, sem perda). Devolve a própria imagem se não há o que fazer.</summary>
    public static BitmapSource Apply(BitmapSource source, int orientation)
    {
        var group = new TransformGroup();
        switch (orientation)
        {
            case 2: group.Children.Add(new ScaleTransform(-1, 1)); break;
            case 3: group.Children.Add(new RotateTransform(180)); break;
            case 4: group.Children.Add(new ScaleTransform(1, -1)); break;
            case 5: group.Children.Add(new RotateTransform(90)); group.Children.Add(new ScaleTransform(-1, 1)); break;   // transposta
            case 6: group.Children.Add(new RotateTransform(90)); break;
            case 7: group.Children.Add(new RotateTransform(90)); group.Children.Add(new ScaleTransform(1, -1)); break;   // transversa
            case 8: group.Children.Add(new RotateTransform(270)); break;
            default: return source;
        }
        var result = new TransformedBitmap(source, group);
        result.Freeze();
        return result;
    }

    /// <summary>Giro horário em múltiplos de 90° (usado pela rotação do arquivo de vídeo e pela rotação manual do usuário).</summary>
    public static BitmapSource Rotate(BitmapSource source, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (degrees == 0) return source;
        var result = new TransformedBitmap(source, new RotateTransform(degrees));
        result.Freeze();
        return result;
    }
}
