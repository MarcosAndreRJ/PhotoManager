using System.Net.Http;
using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Location;

public sealed class LocationService(ICatalogRepository repository, IGpsReader gpsReader, IGeocoder geocoder, Func<bool> getConsent, Action<bool> setConsent, TimeSpan? minimumInterval = null) : ILocationService
{
    // Política de uso do Nominatim: no máximo 1 requisição por segundo.
    private readonly TimeSpan _interval = minimumInterval ?? TimeSpan.FromMilliseconds(1100);
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private DateTime _lastRequest = DateTime.MinValue;

    public bool HasConsent => getConsent();
    public void SetConsent(bool granted) => setConsent(granted);

    public async Task<LocationResult> ReadGpsAsync(IReadOnlyList<Photo> photos, CancellationToken cancellationToken = default)
    {
        var withGps = 0;
        foreach (var photo in photos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await EnsureGpsAsync(photo, cancellationToken)) withGps++;
        }
        return new LocationResult(photos.Count, withGps, 0, 0, 0);
    }

    public async Task<LocationResult> ResolvePlacesAsync(IReadOnlyList<Photo> photos, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!HasConsent) return new LocationResult(0, 0, 0, 0, 0, NeedsConsent: true);
        // Nomes que o catálogo já conhece servem de cache entre sessões.
        foreach (var known in photos.Where(p => p.HasGps && !string.IsNullOrEmpty(p.PlaceName)))
            _cache.TryAdd(new GeoPoint(known.Latitude!.Value, known.Longitude!.Value).CacheKey, known.PlaceName!);

        int withGps = 0, named = 0, fromCache = 0, failed = 0, done = 0;
        foreach (var photo in photos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(++done);
            if (!await EnsureGpsAsync(photo, cancellationToken)) continue;
            withGps++;
            if (!string.IsNullOrEmpty(photo.PlaceName)) continue;
            var point = new GeoPoint(photo.Latitude!.Value, photo.Longitude!.Value);
            if (_cache.TryGetValue(point.CacheKey, out var cached))
            {
                await SaveAsync(photo, point, cached, cancellationToken);
                fromCache++;
                continue;
            }
            var wait = _interval - (DateTime.UtcNow - _lastRequest);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            string? name = null;
            try { name = await geocoder.ReverseAsync(point, cancellationToken); }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested) { }
            _lastRequest = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(name)) { failed++; continue; }
            _cache[point.CacheKey] = name;
            await SaveAsync(photo, point, name, cancellationToken);
            named++;
        }
        return new LocationResult(photos.Count, withGps, named, fromCache, failed);
    }

    private async Task<bool> EnsureGpsAsync(Photo photo, CancellationToken cancellationToken)
    {
        if (photo.HasGps || photo.GpsChecked) return photo.HasGps;
        // Arquivo ausente: não marca como verificado, para tentar de novo quando ele voltar.
        if (!File.Exists(photo.CurrentPath)) return false;
        GeoPoint? point = null;
        try { point = await gpsReader.ReadAsync(photo.CurrentPath, cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        await repository.UpdateGpsAsync(photo.Id, point?.Latitude, point?.Longitude, null, true, cancellationToken);
        photo.Latitude = point?.Latitude;
        photo.Longitude = point?.Longitude;
        photo.GpsChecked = true;
        return point is not null;
    }

    private async Task SaveAsync(Photo photo, GeoPoint point, string name, CancellationToken cancellationToken)
    {
        await repository.UpdateGpsAsync(photo.Id, point.Latitude, point.Longitude, name, true, cancellationToken);
        photo.PlaceName = name;
    }
}
