using System.Net.Http;
using PhotoManager.Application.Library;

namespace PhotoManager.Infrastructure.Location;

/// <summary>
/// Tiles do OpenStreetMap com cache em disco (cada tile é baixado uma única vez e reaproveitado por 30 dias), respeitando a política de uso:
/// identificação do aplicativo, poucas requisições simultâneas e nada de pré-download em massa.
/// Só é usado depois que o usuário permite o mapa online.
/// </summary>
public sealed class MapTileProvider(string cacheFolder, HttpMessageHandler? handler = null, string urlTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png") : IMapTileProvider
{
    private static readonly SemaphoreSlim Gate = new(2);
    private readonly HttpClient _http = CreateClient(handler);

    public async Task<string?> GetTileAsync(int zoom, int x, int y, CancellationToken cancellationToken = default)
    {
        var max = 1 << zoom;
        if (zoom is < 0 or > 19 || y < 0 || y >= max) return null;
        x = ((x % max) + max) % max;                                                   // o mapa dá a volta no eixo horizontal
        var path = Path.Combine(cacheFolder, zoom.ToString(), x.ToString(), y + ".png");
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-30)) return path;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var url = urlTemplate.Replace("{z}", zoom.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
            var bytes = await _http.GetByteArrayAsync(url, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path + ".part", bytes, cancellationToken);
            File.Move(path + ".part", path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return File.Exists(path) ? path : null;                                    // offline: usa o que já estiver em cache, mesmo antigo
        }
        finally { Gate.Release(); }
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PhotoManager/1.0 (desktop photo organizer)");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }
}
