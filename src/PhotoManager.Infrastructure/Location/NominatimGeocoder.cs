using System.Net.Http;
using System.Globalization;
using System.Text.Json;
using PhotoManager.Application.Location;

namespace PhotoManager.Infrastructure.Location;

/// <summary>
/// Geocodificação reversa pelo Nominatim (OpenStreetMap), gratuito. Respeita a política de uso: User-Agent identificável,
/// uma consulta por vez (o espaçamento de 1 s fica no <c>LocationService</c>) e só roda com autorização do usuário.
/// </summary>
public sealed class NominatimGeocoder(HttpClient client) : IGeocoder
{
    public const string UserAgent = "PhotoManager/1.0 (catalogo local de fotos; geocodificacao reversa sob demanda)";
    private const string BaseUrl = "https://nominatim.openstreetmap.org/reverse";

    public static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    public async Task<string?> ReverseAsync(GeoPoint point, CancellationToken cancellationToken = default)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"{BaseUrl}?format=jsonv2&addressdetails=1&zoom=14&accept-language=pt-BR&lat={point.Latitude}&lon={point.Longitude}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!request.Headers.UserAgent.Any()) request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return ParseName(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>"Cidade, Estado, País" a partir da resposta JSON; cai para o <c>display_name</c> quando o endereço vem incompleto.</summary>
    public static string? ParseName(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)) return null;
            if (root.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.Object)
            {
                string? Get(string key) => address.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var place = new[] { "city", "town", "village", "municipality", "suburb", "city_district", "county" }.Select(Get).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                var parts = new[] { place, Get("state"), Get("country") }.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
                if (parts.Count > 0) return string.Join(", ", parts);
            }
            return root.TryGetProperty("display_name", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
