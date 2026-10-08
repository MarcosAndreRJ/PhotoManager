using PhotoManager.Application.Catalog;
using PhotoManager.Application.Location;
using PhotoManager.Infrastructure.Media;
using PhotoManager.Infrastructure.Metadata;

namespace PhotoManager.Infrastructure.Location;

/// <summary>Coordenadas gravadas no arquivo: EXIF GPS (fotos) ou átomo ©xyz do contêiner (vídeos). Nunca usa a rede.</summary>
public sealed class GpsReader : IGpsReader
{
    public Task<GeoPoint?> ReadAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() => Read(path), cancellationToken);

    public static GeoPoint? Read(string path)
    {
        GeoPoint? point;
        if (MediaFormats.IsVideo(path)) point = Iso6709.TryParse(Mp4Info.TryReadLocation(path));
        else
        {
            var metadata = MetadataExtractorReader.Read(path);
            point = metadata is { Latitude: { } lat, Longitude: { } lon } ? new GeoPoint(lat, lon) : null;
        }
        return point is { IsValid: true } ? point : null;
    }
}
