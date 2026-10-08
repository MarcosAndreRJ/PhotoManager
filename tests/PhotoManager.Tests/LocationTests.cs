using System.Net.Http;
using System.Buffers.Binary;
using System.Text;
using PhotoManager.Application.Location;
using PhotoManager.Infrastructure.Location;
using PhotoManager.Infrastructure.Media;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

public sealed class LocationTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private sealed class FakeGps(Dictionary<string, GeoPoint?> byFileName) : IGpsReader
    {
        public int Reads;
        public Task<GeoPoint?> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(byFileName.GetValueOrDefault(Path.GetFileName(path)));
        }
    }

    private sealed class FakeGeocoder(Func<GeoPoint, string?> answer) : IGeocoder
    {
        public readonly List<(GeoPoint Point, DateTime At)> Calls = [];
        public Task<string?> ReverseAsync(GeoPoint point, CancellationToken cancellationToken = default)
        {
            Calls.Add((point, DateTime.UtcNow));
            return Task.FromResult(answer(point));
        }
    }

    private LocationService CreateService(FakeGps gps, FakeGeocoder geocoder, bool consent, Action<bool>? onConsent = null)
    {
        var granted = consent;
        return new LocationService(_env.Repository, gps, geocoder, () => granted, value => { granted = value; onConsent?.Invoke(value); }, TimeSpan.FromMilliseconds(150));
    }

    // ---------- partes puras ----------

    [Theory]
    [InlineData("-22.476429-42.187541+13.200/", -22.476429, -42.187541)]
    [InlineData("+48.8577+002.295/", 48.8577, 2.295)]
    [InlineData("+27.5916+086.5640+8850CRSWGS_84/", 27.5916, 86.564)]
    public void Iso6709_ParsesLatitudeAndLongitude(string text, double lat, double lon)
    {
        var point = Iso6709.TryParse(text);
        Assert.NotNull(point);
        Assert.Equal(lat, point!.Value.Latitude, 6);
        Assert.Equal(lon, point.Value.Longitude, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("+12.5/")]
    [InlineData("+95.0+010.0/")]            // latitude fora da faixa
    [InlineData("+0.0+0.0/")]               // 0,0 é o "GPS não fixado" dos aparelhos
    public void Iso6709_RejectsGarbage(string? text) => Assert.Null(Iso6709.TryParse(text));

    [Fact]
    public void GeoPoint_CacheKeyGroupsNearbyPoints_AndMapUrlUsesInvariantCulture()
    {
        Assert.Equal(new GeoPoint(-22.47643, -42.18754).CacheKey, new GeoPoint(-22.47601, -42.18781).CacheKey);
        Assert.NotEqual(new GeoPoint(-22.476, -42.187).CacheKey, new GeoPoint(-22.480, -42.187).CacheKey);
        Assert.Contains("mlat=-22.5&mlon=-42.25", new GeoPoint(-22.5, -42.25).MapUrl);
    }

    [Fact]
    public void Nominatim_ParseName_BuildsCityStateCountry_AndFallsBack()
    {
        Assert.Equal("Arraial do Cabo, Rio de Janeiro, Brasil",
            NominatimGeocoder.ParseName("""{"display_name":"x, y","address":{"town":"Arraial do Cabo","county":"Região","state":"Rio de Janeiro","country":"Brasil"}}"""));
        Assert.Equal("Paris, Île-de-France, França",
            NominatimGeocoder.ParseName("""{"address":{"city":"Paris","state":"Île-de-France","country":"França"}}"""));
        Assert.Equal("Oceano Atlântico", NominatimGeocoder.ParseName("""{"display_name":"Oceano Atlântico","address":{}}"""));
        Assert.Null(NominatimGeocoder.ParseName("""{"error":"Unable to geocode"}"""));
        Assert.Null(NominatimGeocoder.ParseName("não é json"));
    }

    [Fact]
    public async Task Nominatim_SendsIdentifyingUserAgent_AndNeverCallsWithoutRequest()
    {
        HttpRequestMessage? seen = null;
        var client = new HttpClient(new StubHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage { Content = new StringContent("""{"address":{"city":"Rio de Janeiro","state":"Rio de Janeiro","country":"Brasil"}}""") };
        }));
        var name = await new NominatimGeocoder(client).ReverseAsync(new GeoPoint(-22.9, -43.2));
        Assert.Equal("Rio de Janeiro, Brasil", name);
        Assert.Equal("nominatim.openstreetmap.org", seen!.RequestUri!.Host);
        Assert.Contains("lat=-22.9", seen.RequestUri.Query);
        Assert.Contains("lon=-43.2", seen.RequestUri.Query);
        Assert.Contains("accept-language=pt-BR", seen.RequestUri.Query);
        Assert.Contains("PhotoManager", seen.Headers.UserAgent.ToString());

        var failing = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)));
        Assert.Null(await new NominatimGeocoder(failing).ReverseAsync(new GeoPoint(1, 1)));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    // ---------- vídeo (átomo ©xyz) ----------

    private static byte[] Box(string type, byte[] payload)
    {
        var bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        Encoding.Latin1.GetBytes(type, 0, 4, bytes, 4);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Xyz(string iso)
    {
        var text = Encoding.Latin1.GetBytes(iso);
        var payload = new byte[4 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)text.Length);
        payload[2] = 0x15; payload[3] = 0xC7;                       // idioma
        text.CopyTo(payload, 4);
        return Box("©xyz", payload);
    }

    [Fact]
    public void Mp4_ReadsDjiStyleLocation_FromMoovAndFromUdta()
    {
        using var direct = new MemoryStream(Box("moov", Xyz("-22.476429-42.187541+13.200/")));
        Assert.Equal("-22.476429-42.187541+13.200/", Mp4Info.ReadLocation(direct));
        using var nested = new MemoryStream(Box("moov", Box("udta", Xyz("+48.8577+002.295/"))));
        Assert.Equal(new GeoPoint(48.8577, 2.295), Iso6709.TryParse(Mp4Info.ReadLocation(nested)));
        using var none = new MemoryStream(Box("moov", Box("free", new byte[8])));
        Assert.Null(Mp4Info.ReadLocation(none));
    }

    [Fact]
    public async Task GpsReader_ReadsVideoLocation_AndReturnsNullForPhotosWithoutGps()
    {
        var video = Path.Combine(_env.Photos, "gps.mp4");
        File.WriteAllBytes(video, Box("moov", Xyz("-22.476429-42.187541+13.200/")));
        var png = _env.CreatePng("plain.png");
        var reader = new GpsReader();
        Assert.Equal(new GeoPoint(-22.476429, -42.187541), await reader.ReadAsync(video));
        Assert.Null(await reader.ReadAsync(png));
    }

    // ---------- serviço ----------

    [Fact]
    public async Task ReadGps_StoresCoordinatesLocally_WithoutTheNetwork_AndOnlyChecksOnce()
    {
        _env.CreatePng("g.png"); _env.CreatePng("n.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = (await _env.Catalog.GetPhotosAsync()).OrderBy(p => p.FileName).ToList();
        var gps = new FakeGps(new() { ["g.png"] = new GeoPoint(-22.4, -42.1) });
        var geocoder = new FakeGeocoder(_ => "nunca");
        var service = CreateService(gps, geocoder, consent: false);

        var result = await service.ReadGpsAsync(photos);
        Assert.Equal((2, 1), (result.Processed, result.WithGps));
        Assert.Empty(geocoder.Calls);

        var saved = (await _env.Catalog.GetPhotosAsync()).OrderBy(p => p.FileName).ToList();
        Assert.True(saved[0].GpsChecked && saved[0].HasGps && saved[1].GpsChecked && !saved[1].HasGps);   // g.png = [0]
        Assert.Equal(-22.4, saved[0].Latitude);

        await service.ReadGpsAsync(photos);
        Assert.Equal(2, gps.Reads);                                                                      // não relê o que já foi verificado
    }

    [Fact]
    public async Task ResolvePlaces_WithoutConsent_NeverCallsTheGeocoder()
    {
        _env.CreatePng("a.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = (await _env.Catalog.GetPhotosAsync()).ToList();
        var geocoder = new FakeGeocoder(_ => "x");
        var service = CreateService(new FakeGps(new() { ["a.png"] = new GeoPoint(1, 2) }), geocoder, consent: false);

        var result = await service.ResolvePlacesAsync(photos);
        Assert.True(result.NeedsConsent);
        Assert.Empty(geocoder.Calls);
        Assert.Null(photos[0].PlaceName);
    }

    [Fact]
    public async Task ResolvePlaces_NamesPersist_NearbyPhotosShareOneRequest_AndRequestsAreSpaced()
    {
        _env.CreatePng("p1.png"); _env.CreatePng("p2.png"); _env.CreatePng("p3.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = (await _env.Catalog.GetPhotosAsync()).OrderBy(p => p.FileName).ToList();
        var gps = new FakeGps(new()
        {
            ["p1.png"] = new GeoPoint(-22.47643, -42.18754),
            ["p2.png"] = new GeoPoint(-22.47601, -42.18781),      // mesmo lugar (≈100 m)
            ["p3.png"] = new GeoPoint(-23.55052, -46.63331)
        });
        var geocoder = new FakeGeocoder(point => point.Latitude < -23 ? "São Paulo, SP, Brasil" : "Arraial do Cabo, RJ, Brasil");
        var service = CreateService(gps, geocoder, consent: true);

        var progress = new List<int>();
        var result = await service.ResolvePlacesAsync(photos, new Progress<int>(progress.Add));
        Assert.Equal((3, 3, 2, 1, 0), (result.Processed, result.WithGps, result.Named, result.FromCache, result.Failed));
        Assert.Equal(2, geocoder.Calls.Count);
        Assert.True(geocoder.Calls[1].At - geocoder.Calls[0].At >= TimeSpan.FromMilliseconds(120));
        Assert.Equal(["Arraial do Cabo, RJ, Brasil", "Arraial do Cabo, RJ, Brasil", "São Paulo, SP, Brasil"], (await _env.Catalog.GetPhotosAsync()).OrderBy(p => p.FileName).Select(p => p.PlaceName));

        // Segunda rodada: nada novo a perguntar.
        await service.ResolvePlacesAsync(photos);
        Assert.Equal(2, geocoder.Calls.Count);
    }

    [Fact]
    public async Task ResolvePlaces_ServiceFailure_IsCountedAndLeavesTheNameEmptyForALaterTry()
    {
        _env.CreatePng("f.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = (await _env.Catalog.GetPhotosAsync()).ToList();
        var service = CreateService(new FakeGps(new() { ["f.png"] = new GeoPoint(10, 10) }), new FakeGeocoder(_ => null), consent: true);
        var result = await service.ResolvePlacesAsync(photos);
        Assert.Equal((1, 1), (result.Failed, result.WithGps));
        Assert.Null(photos[0].PlaceName);
        Assert.True(photos[0].HasGps);
    }

    [Fact]
    public async Task PlaceName_IsSearchable()
    {
        _env.CreatePng("s1.png"); _env.CreatePng("s2.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = (await _env.Catalog.GetPhotosAsync()).OrderBy(p => p.FileName).ToList();
        await _env.Repository.UpdateGpsAsync(photos[0].Id, -22.4, -42.1, "Arraial do Cabo, Rio de Janeiro, Brasil", true);
        var found = await _env.Catalog.GetPhotosAsync(new PhotoManager.Application.Catalog.PhotoFilter(Search: "arraial"));
        Assert.Equal(["s1.png"], found.Select(p => p.FileName));
    }

    // ---------- tela ----------

    [Fact]
    public async Task Library_FindLocation_AsksConsentOnce_StoresIt_AndUpdatesCards()
    {
        _env.CreatePng("v1.png"); _env.CreatePng("v2.png");
        var granted = false;
        var gps = new FakeGps(new() { ["v1.png"] = new GeoPoint(-22.4, -42.1) });
        var geocoder = new FakeGeocoder(_ => "Búzios, Rio de Janeiro, Brasil");
        var service = CreateService(gps, geocoder, consent: false, onConsent: v => granted = v);
        var library = new LibraryViewModel(_env.Catalog, _env.Thumbnails, _env.Organization, _env.FileOperations, duplicateRepository: null, collectionService: _env.Collections, locationService: service);
        await library.ImportFolderAsync(_env.Photos);
        var cards = library.Photos.OrderBy(c => c.FileName).ToList();
        library.UpdateSelection([cards[0], cards[1]]);

        var asked = 0;
        library.ConfirmGeocoding = () => { asked++; return true; };
        await library.FindLocationAsync(cards[0]);
        Assert.Equal(1, asked);
        Assert.True(granted);
        Assert.True(cards[0].HasGps);
        Assert.Equal("Búzios, Rio de Janeiro, Brasil", cards[0].PlaceText);
        Assert.Equal("Sem GPS no arquivo", cards[1].CoordinatesText);
        Assert.Single(geocoder.Calls);                                           // o rodapé pode ser sobrescrito por tarefas de fundo; confere o efeito

        asked = 0;
        await library.FindLocationAsync(cards[0]);
        Assert.Equal(0, asked);                                                  // autorização lembrada; nada pendente
    }

    [Fact]
    public async Task Library_FindLocation_WhenTheUserDeclines_ReadsGpsButNeverUsesTheNetwork()
    {
        _env.CreatePng("d1.png");
        var geocoder = new FakeGeocoder(_ => "x");
        var service = CreateService(new FakeGps(new() { ["d1.png"] = new GeoPoint(-22.4, -42.1) }), geocoder, consent: false);
        var library = new LibraryViewModel(_env.Catalog, _env.Thumbnails, _env.Organization, _env.FileOperations, null, _env.Collections, service) { ConfirmGeocoding = () => false };
        await library.ImportFolderAsync(_env.Photos);
        var card = library.Photos.Single();
        await library.FindLocationAsync(card);
        Assert.True(card.HasGps);
        Assert.Equal("Nome não obtido", card.PlaceText);
        Assert.Empty(geocoder.Calls);
        Assert.False(service.HasConsent);
    }
}
