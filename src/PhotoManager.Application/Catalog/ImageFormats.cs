namespace PhotoManager.Application.Catalog;

/// <summary>Formatos que o catálogo indexa. RAW é somente leitura: miniatura/preview vêm do JPEG embutido e metadados nunca são gravados no arquivo.</summary>
public static class ImageFormats
{
    public static readonly IReadOnlySet<string> Raw = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cr2" };
    public static readonly IReadOnlySet<string> Standard = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    public static bool IsRaw(string extensionOrPath) => Raw.Contains(Path.GetExtension(extensionOrPath));
    public static bool IsJpeg(string extensionOrPath) => Path.GetExtension(extensionOrPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(extensionOrPath).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    public static bool IsSupported(string extensionOrPath) => IsRaw(extensionOrPath) || Standard.Contains(Path.GetExtension(extensionOrPath));
}
