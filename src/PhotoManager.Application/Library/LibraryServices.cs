using PhotoManager.Application.Ai;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Library;

// ---------- exportação com predefinições ----------

public enum WatermarkPosition { BottomRight, BottomLeft, TopRight, TopLeft, Center }

/// <summary>Como exportar: tamanho (lado maior), formato, qualidade, marca d'água e modelo de nome. Vídeos são copiados como estão.</summary>
public sealed record ExportPreset(
    string Name,
    int? LongEdge = null,
    string Format = "jpg",
    int Quality = 90,
    string? WatermarkText = null,
    WatermarkPosition WatermarkPosition = WatermarkPosition.BottomRight,
    double WatermarkOpacity = 0.6,
    string NameTemplate = "{nome}",
    bool IncludeVideos = true)
{
    public string Summary => string.Join(" · ", new[]
    {
        LongEdge is { } edge ? $"{edge} px" : "tamanho original",
        Format == "original" ? "formato original" : Format.ToUpperInvariant() + (Format == "jpg" ? $" {Quality}%" : string.Empty),
        string.IsNullOrWhiteSpace(WatermarkText) ? null : $"marca “{WatermarkText}”",
        IncludeVideos ? "vídeos copiados" : "sem vídeos"
    }.Where(s => s is not null));

    public static IReadOnlyList<ExportPreset> BuiltIn { get; } =
    [
        new("Instagram / Reels (1080 px)", 1080, "jpg", 90),
        new("Web e blog (2048 px)", 2048, "jpg", 85),
        new("Cliente com marca d'água (2048 px)", 2048, "jpg", 85, "© Seu nome", WatermarkPosition.BottomRight, 0.55),
        new("Microstock (JPEG original, sem marca)", null, "jpg", 100, IncludeVideos: true),
        new("Arquivo (cópia original)", null, "original", 100)
    ];
}

public sealed record ExportProgress(int Done, int Total, string Current);
public sealed record ExportResult(IReadOnlyList<string> Created, IReadOnlyList<(string Path, string Error)> Failed, bool Cancelled);

public interface IExportService
{
    /// <summary>Exporta para <paramref name="destination"/>. Nunca sobrescreve: nome repetido ganha número. Cancelar não deixa arquivo pela metade.</summary>
    Task<ExportResult> ExportAsync(IReadOnlyList<Photo> photos, ExportPreset preset, string destination, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default);
}

// ---------- backup verificado ----------

public sealed record BackupReport(int Checked, IReadOnlyList<string> Missing, IReadOnlyList<string> Different, long MissingBytes)
{
    public bool IsComplete => Missing.Count == 0 && Different.Count == 0;
}

public interface IBackupVerifier
{
    /// <summary>Confere se cada arquivo da origem existe no backup com o mesmo caminho relativo e o mesmo tamanho.</summary>
    Task<BackupReport> VerifyAsync(BackupTarget target, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Copia para o backup o que falta (e o que está diferente), preservando datas. Devolve quantos foram copiados.</summary>
    Task<int> CopyMissingAsync(BackupTarget target, BackupReport report, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
}

// ---------- preferências da Biblioteca ----------

public enum LibrarySort { Name, DateNewest, DateOldest, Size, Rating, Duration }
public enum GridStyle { Justified, Uniform }
public enum AppTheme { Light, Dark, System }

public sealed record LibraryPreferences(
    LibrarySort Sort = LibrarySort.DateNewest,
    GridStyle Grid = GridStyle.Justified,
    double RowHeight = 200,
    bool ShowFileNames = false,
    bool HoverPreview = true,
    bool AutoAdvance = true,
    bool ShowRightPanel = true,
    bool GroupByDay = true,
    bool StackPhotos = true,
    AppTheme Theme = AppTheme.Light,
    bool MapAllowed = false,
    IReadOnlyList<ExportPreset>? ExportPresets = null,
    IReadOnlyList<AiCapability>? AiEnabled = null)
{
    public static LibraryPreferences Default { get; } = new();
    public IReadOnlyList<ExportPreset> Presets => ExportPresets is { Count: > 0 } presets ? presets : ExportPreset.BuiltIn;
}

public interface ILibrarySettings
{
    LibraryPreferences Load();
    void Save(LibraryPreferences preferences);
}

// ---------- mapa (tiles do OpenStreetMap) ----------

/// <summary>Conversões Web Mercator (as mesmas do OpenStreetMap): latitude/longitude ↔ pixel no zoom z.</summary>
public static class MapMath
{
    public const int TileSize = 256;
    public const double MaxLatitude = 85.05112878;

    public static (double X, double Y) ToPixel(double latitude, double longitude, double zoom)
    {
        latitude = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
        var scale = TileSize * Math.Pow(2, zoom);
        var x = (longitude + 180) / 360 * scale;
        var sin = Math.Sin(latitude * Math.PI / 180);
        var y = (0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI)) * scale;
        return (x, y);
    }

    public static (double Latitude, double Longitude) ToLatLon(double x, double y, double zoom)
    {
        var scale = TileSize * Math.Pow(2, zoom);
        var longitude = x / scale * 360 - 180;
        var n = Math.PI - 2 * Math.PI * y / scale;
        var latitude = 180 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
        return (latitude, longitude);
    }

    /// <summary>Zoom e centro que enquadram todos os pontos numa área de <paramref name="width"/>×<paramref name="height"/> px.</summary>
    public static (double Latitude, double Longitude, int Zoom) Fit(IReadOnlyList<(double Lat, double Lon)> points, double width, double height)
    {
        if (points.Count == 0) return (-15.8, -47.9, 4);                       // Brasil
        double minLat = points.Min(p => p.Lat), maxLat = points.Max(p => p.Lat), minLon = points.Min(p => p.Lon), maxLon = points.Max(p => p.Lon);
        for (var zoom = 17; zoom >= 1; zoom--)
        {
            var (x1, y1) = ToPixel(maxLat, minLon, zoom);
            var (x2, y2) = ToPixel(minLat, maxLon, zoom);
            if (x2 - x1 <= width * 0.85 && y2 - y1 <= height * 0.85) return ((minLat + maxLat) / 2, (minLon + maxLon) / 2, zoom);
        }
        return ((minLat + maxLat) / 2, (minLon + maxLon) / 2, 1);
    }
}

public interface IMapTileProvider
{
    /// <summary>Caminho local do tile (baixado uma vez e guardado em cache); nulo se offline/indisponível.</summary>
    Task<string?> GetTileAsync(int zoom, int x, int y, CancellationToken cancellationToken = default);
}
