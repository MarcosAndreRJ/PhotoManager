using System.Globalization;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Location;

/// <summary>Coordenadas decimais (graus).</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public bool IsValid => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180 && !(Latitude == 0 && Longitude == 0);

    /// <summary>Chave de cache (~100 m): fotos tiradas no mesmo lugar reaproveitam o mesmo nome.</summary>
    public string CacheKey => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Latitude, 3):0.000},{Math.Round(Longitude, 3):0.000}");

    public string MapUrl => string.Create(CultureInfo.InvariantCulture, $"https://www.openstreetmap.org/?mlat={Latitude}&mlon={Longitude}#map=15/{Latitude}/{Longitude}");
}

/// <summary>Interpreta coordenadas no formato ISO 6709 (ex.: <c>-22.476429-42.187541+13.200/</c>), usado pelos vídeos de drone e celular.</summary>
public static class Iso6709
{
    public static GeoPoint? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = new List<string>();
        var current = "";
        foreach (var c in text.Trim().TrimEnd('/'))
        {
            if ((c == '+' || c == '-') && current.Length > 0) { parts.Add(current); current = ""; }
            current += c;
        }
        if (current.Length > 0) parts.Add(current);
        if (parts.Count < 2) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return null;
        var point = new GeoPoint(lat, lon);
        return point.IsValid ? point : null;
    }
}

/// <summary>Lê as coordenadas gravadas no arquivo (EXIF GPS em fotos; átomo ©xyz em vídeos). Só leitura, sem rede.</summary>
public interface IGpsReader
{
    Task<GeoPoint?> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Converte coordenadas em nome de lugar (geocodificação reversa). Envia as coordenadas a um serviço externo.</summary>
public interface IGeocoder
{
    Task<string?> ReverseAsync(GeoPoint point, CancellationToken cancellationToken = default);
}

public sealed record LocationResult(int Processed, int WithGps, int Named, int FromCache, int Failed, bool NeedsConsent = false);

public interface ILocationService
{
    /// <summary>O usuário autorizou enviar coordenadas ao serviço público de mapas (OpenStreetMap/Nominatim)?</summary>
    bool HasConsent { get; }
    void SetConsent(bool granted);

    /// <summary>Só lê o GPS gravado nos arquivos e guarda no catálogo (sem rede). Itens já verificados são pulados.</summary>
    Task<LocationResult> ReadGpsAsync(IReadOnlyList<Photo> photos, CancellationToken cancellationToken = default);

    /// <summary>Lê o GPS e, com autorização, descobre o nome do lugar (1 consulta por segundo, com cache por local).</summary>
    Task<LocationResult> ResolvePlacesAsync(IReadOnlyList<Photo> photos, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
}
