using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Xmp;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Metadata;

namespace PhotoManager.Infrastructure.Metadata;

/// <summary>Leitura de EXIF, IPTC e XMP com a biblioteca MetadataExtractor. Nunca escreve no arquivo.</summary>
public sealed partial class MetadataExtractorReader : IMetadataReader
{
    public Task<PhotoMetadata> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(path), cancellationToken);

    /// <param name="includeExifTextFallback">false lê só XMP + IPTC (o que a Fase 6 grava); usado na validação pós-escrita.</param>
    public static PhotoMetadata Read(string path, bool includeExifTextFallback = true)
    {
        var embedded = ReadEmbedded(path, includeExifTextFallback);
        if (!XmpSidecar.UsesSidecar(path)) return embedded;

        // RAW/PNG/WebP/vídeo: título, descrição, palavras-chave, autor e copyright vêm do sidecar .xmp (a fonte única desses campos quando ele existe).
        var sidecar = SidecarXmpReader.ReadFor(path);
        if (sidecar is null) return MediaFormats.IsVideo(path) && embedded.Error is not null ? embedded with { Error = null } : embedded;
        return embedded with
        {
            Title = sidecar.Title, Description = sidecar.Description, Keywords = sidecar.Keywords, Author = sidecar.Author, Copyright = sidecar.Copyright,
            Sources = embedded.Sources.Append("XMP (sidecar)").ToList(), Error = null
        };
    }

    private static PhotoMetadata ReadEmbedded(string path, bool includeExifTextFallback)
    {
        if (!File.Exists(path)) return new PhotoMetadata { Error = "Arquivo não encontrado." };
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            directories = ImageMetadataReader.ReadMetadata(stream);
        }
        catch (ImageProcessingException) { return new PhotoMetadata { Error = "Formato sem metadados reconhecíveis." }; }
        catch (IOException ex) { return new PhotoMetadata { Error = $"Não foi possível ler o arquivo: {ex.Message}" }; }

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var exifText = includeExifTextFallback ? ifd0 : null;
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var iptc = directories.OfType<IptcDirectory>().FirstOrDefault();
        var xmp = directories.OfType<XmpDirectory>().FirstOrDefault();
        var xmpProps = xmp?.GetXmpProperties() ?? new Dictionary<string, string>();

        var make = Clean(ifd0?.GetDescription(ExifDirectoryBase.TagMake));
        var model = Clean(ifd0?.GetDescription(ExifDirectoryBase.TagModel));
        DateTime? taken = null;
        if (subIfd is not null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt)) taken = dt;
        int? iso = null;
        if (subIfd is not null && subIfd.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var isoValue)) iso = isoValue;
        double? latitude = null, longitude = null;
        if (gps?.GetGeoLocation() is { } location) { latitude = location.Latitude; longitude = location.Longitude; }

        // Palavras-chave: primeira fonte que tiver valores (XMP → IPTC → EXIF XP). Misturar fontes ressuscitaria palavras apagadas.
        var keywords = new[]
        {
            XmpValues(xmpProps, KeywordsKey()),
            iptc?.GetStringValueArray(IptcDirectory.TagKeywords)?.Select(v => v.ToString()) ?? [],
            SplitKeywords(exifText?.GetDescription(ExifDirectoryBase.TagWinKeywords))
        }.Select(source => source.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
         .FirstOrDefault(list => list.Count > 0) ?? [];

        var sources = new List<string>();
        if (ifd0 is not null || subIfd is not null) sources.Add("EXIF");
        if (iptc is not null) sources.Add("IPTC");
        if (xmpProps.Count > 0) sources.Add("XMP");
        if (latitude.HasValue) sources.Add("GPS");

        // Prioridade: XMP → IPTC → EXIF.
        return new PhotoMetadata
        {
            Title = First(XmpValues(xmpProps, TitleKey()).FirstOrDefault(), iptc?.GetString(IptcDirectory.TagObjectName), exifText?.GetDescription(ExifDirectoryBase.TagWinTitle)),
            Description = First(XmpValues(xmpProps, DescriptionKey()).FirstOrDefault(), iptc?.GetString(IptcDirectory.TagCaption), exifText?.GetDescription(ExifDirectoryBase.TagImageDescription), exifText?.GetDescription(ExifDirectoryBase.TagWinComment)),
            Keywords = keywords,
            Author = First(XmpValues(xmpProps, CreatorKey()).FirstOrDefault(), iptc?.GetString(IptcDirectory.TagByLine), exifText?.GetDescription(ExifDirectoryBase.TagArtist), exifText?.GetDescription(ExifDirectoryBase.TagWinAuthor)),
            Copyright = First(XmpValues(xmpProps, RightsKey()).FirstOrDefault(), iptc?.GetString(IptcDirectory.TagCopyrightNotice), exifText?.GetDescription(ExifDirectoryBase.TagCopyright)),
            Camera = CombineCamera(make, model),
            Lens = Clean(subIfd?.GetDescription(ExifDirectoryBase.TagLensModel)),
            Iso = iso,
            ExposureTime = Clean(subIfd?.GetDescription(ExifDirectoryBase.TagExposureTime)),
            Aperture = Clean(subIfd?.GetDescription(ExifDirectoryBase.TagFNumber)),
            FocalLength = Clean(subIfd?.GetDescription(ExifDirectoryBase.TagFocalLength)),
            DateTaken = taken,
            Latitude = latitude,
            Longitude = longitude,
            Sources = sources
        };
    }

    private static string? CombineCamera(string? make, string? model)
    {
        if (model is null) return make;
        // Muitos modelos já incluem a marca ("Canon EOS R5"); evita "Canon Canon EOS R5".
        return make is null || model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
    }

    private static string? First(params string?[] candidates) => candidates.Select(Clean).FirstOrDefault(c => c is not null);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('\0');

    private static IEnumerable<string> SplitKeywords(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Valores de propriedades XMP em lista/idioma (dc:title[1], dc:subject[2]…), ignorando qualificadores como /xml:lang.</summary>
    private static IEnumerable<string> XmpValues(IDictionary<string, string> properties, Regex key) =>
        properties.Where(p => key.IsMatch(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value);

    [GeneratedRegex(@"^dc:title\[\d+\]$")] private static partial Regex TitleKey();
    [GeneratedRegex(@"^dc:description\[\d+\]$")] private static partial Regex DescriptionKey();
    [GeneratedRegex(@"^dc:subject\[\d+\]$")] private static partial Regex KeywordsKey();
    [GeneratedRegex(@"^dc:creator\[\d+\]$")] private static partial Regex CreatorKey();
    [GeneratedRegex(@"^dc:rights\[\d+\]$")] private static partial Regex RightsKey();
}
