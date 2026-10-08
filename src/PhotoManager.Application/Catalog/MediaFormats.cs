namespace PhotoManager.Application.Catalog;

/// <summary>Fotos e vídeos que o catálogo indexa. Vídeos nunca têm o arquivo alterado: metadados vão para um sidecar .xmp.</summary>
public static class MediaFormats
{
    public static readonly IReadOnlySet<string> Video = new HashSet<string>(StringComparer.OrdinalIgnoreCase)  // manter igual a Domain.Photos.MediaKind
    {
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".mts", ".m2ts"
    };

    public static bool IsVideo(string extensionOrPath) => Video.Contains(Path.GetExtension(extensionOrPath));
    public static bool IsSupported(string extensionOrPath) => ImageFormats.IsSupported(extensionOrPath) || IsVideo(extensionOrPath);
}

/// <summary>
/// Sidecar XMP ao lado da mídia (<c>IMG_001.CR2</c> → <c>IMG_001.xmp</c>), o mesmo padrão do Lightroom/Bridge.
/// Usado para o que não pode (ou não deve) ter metadados gravados dentro do arquivo: RAW, PNG, WebP e vídeos.
/// </summary>
public static class XmpSidecar
{
    public static string PathFor(string mediaPath) => Path.ChangeExtension(mediaPath, ".xmp");

    /// <summary>Formatos cujos metadados editáveis ficam no sidecar (JPEG grava dentro do arquivo).</summary>
    public static bool UsesSidecar(string path) => MediaFormats.IsSupported(path) && !ImageFormats.IsJpeg(path);
}

public static class MetadataFormats
{
    /// <summary>Todo formato catalogado aceita edição de metadados: JPEG grava no arquivo; RAW, PNG, WebP e vídeo gravam no sidecar .xmp.</summary>
    public static bool CanEdit(string extensionOrPath) => ImageFormats.IsJpeg(extensionOrPath) || XmpSidecar.UsesSidecar(extensionOrPath);
}
