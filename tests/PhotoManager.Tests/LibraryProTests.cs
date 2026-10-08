using System.IO;
using System.Net;
using System.Net.Http;
using PhotoManager.Application.Ai;
using PhotoManager.Application.Library;
using PhotoManager.Domain.Photos;
using PhotoManager.Infrastructure.Ai;
using PhotoManager.Infrastructure.Files;
using PhotoManager.Infrastructure.Images;
using PhotoManager.Infrastructure.Location;

namespace PhotoManager.Tests;

/// <summary>Recursos "pró" da Biblioteca: busca com sintaxe, pilhas, Shorts, parecidas/desfocadas, armazenamento, cortes, exportação, backup, IA local e mapa.</summary>
[Collection("WpfUi")]
public sealed class LibraryProTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private static Photo P(string name, int? w = 1920, int? h = 1080, double? seconds = null, DateTime? taken = null, int rating = 0, string folder = @"D:\F")
        => new()
        {
            Id = Math.Abs(name.GetHashCode()), FileName = name, CurrentPath = Path.Combine(folder, name), Extension = Path.GetExtension(name).ToLowerInvariant(),
            Width = w, Height = h, DurationSeconds = seconds, DateTaken = taken, Rating = rating, CreatedAt = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc), FileSize = 1000
        };

    // ---------- busca com sintaxe ----------

    [Fact]
    public void Query_ParsesKeysOperatorsNegationAndQuotes()
    {
        var shortVertical = P("short.mp4", 1080, 1920, 45);
        var longVertical = P("long.mp4", 1080, 1920, 600);
        var landscape = P("land.jpg", rating: 4);
        landscape.Tags.Add("pôr do sol");
        landscape.ColorLabel = PhotoColor.Green;
        shortVertical.Pick = PickFlag.Picked;
        longVertical.Usage = UsageStatus.Published;

        bool M(string q, Photo p) => LibraryQuery.Parse(q).Matches(p);
        Assert.True(M("tipo:video duração:<60 orientação:vertical", shortVertical));
        Assert.False(M("tipo:video duração:<60", longVertical));
        Assert.True(M("duracao:>=1m30", longVertical));
        Assert.True(M("cor:verde nota:>=3 tag:\"pôr do sol\"", landscape));
        Assert.False(M("-cor:verde", landscape));
        Assert.True(M("shorts:sim", shortVertical));
        Assert.False(M("shorts:sim", longVertical));                   // 10 min não é Shorts
        Assert.True(M("bandeira:escolhida", shortVertical));
        Assert.True(M("uso:usado", longVertical));                     // publicado também conta como usado
        Assert.True(M("-uso:publicado tipo:video", shortVertical));
        Assert.True(M("LAND", landscape));                             // texto livre, sem acento/maiúscula
        Assert.True(M("tamanho:<1KB", landscape));

        var invalid = LibraryQuery.Parse("nota:9 cor:rosa duração:abc tipo:foto");
        Assert.Equal(3, invalid.Errors.Count);
        Assert.Single(invalid.Terms);
        Assert.Equal("tipo:video", LibraryQuery.Without("tipo:video cor:verde", LibraryQuery.Parse("tipo:video cor:verde").Terms[1]));
    }

    [Fact]
    public void Query_SuggestsKeysAndValues_AndUsesContext()
    {
        Assert.Contains("duração:", LibraryQuery.Suggest("dur"));
        Assert.Contains("tipo:video duração:<60 ", LibraryQuery.Suggest("tipo:video duração:<"));
        Assert.Contains("cor:verde ", LibraryQuery.Suggest("cor:ve"));
        Assert.Contains("tag:viagem ", LibraryQuery.Suggest("tag:vi", key => key == "tag" ? ["viagem", "praia"] : []));
        var photo = P("a.jpg");
        var context = new FakeContext { Transcript = "bom dia pessoal", Semantic = [photo.Id] };
        Assert.True(LibraryQuery.Parse("fala:pessoal").Matches(photo, context));
        Assert.True(LibraryQuery.Parse("ia:\"praia ao pôr do sol\"").Matches(photo, context));
        Assert.False(LibraryQuery.Parse("ia:praia").Matches(photo));                // sem IA: não casa
        Assert.Equal(90, LibraryQuery.ParseDuration("1m30"));
        Assert.Equal(120, LibraryQuery.ParseDuration("2min"));
        Assert.Equal(75, LibraryQuery.ParseDuration("1:15"));
    }

    private sealed class FakeContext : ILibraryQueryContext
    {
        public string Transcript { get; init; } = string.Empty;
        public HashSet<long> Semantic { get; init; } = [];
        public bool TranscriptContains(long photoId, string text) => Transcript.Contains(text, StringComparison.OrdinalIgnoreCase);
        public IReadOnlySet<long>? SemanticMatches(string text) => Semantic;
    }

    // ---------- Shorts, pilhas, parecidas ----------

    [Fact]
    public void ShortForm_IsVertical916_UpTo3Minutes()
    {
        Assert.True(ShortFormRules.IsShortForm(P("a.mp4", 1080, 1920, 59)));
        Assert.True(ShortFormRules.IsShortForm(P("b.mp4", 720, 1280, 170)));
        Assert.False(ShortFormRules.IsShortForm(P("c.mp4", 1080, 1920, 200)));
        Assert.False(ShortFormRules.IsShortForm(P("d.mp4", 1920, 1080, 30)));       // horizontal
        Assert.False(ShortFormRules.IsShortForm(P("e.mp4", 1080, 1440, 30)));       // 3:4 não é 9:16
        var rotated = P("f.mp4", 1920, 1080, 30); rotated.UserRotation = 90;
        Assert.True(ShortFormRules.IsShortForm(rotated));
    }

    [Fact]
    public void Stacks_GroupRawJpegAndBursts()
    {
        var t = new DateTime(2026, 9, 27, 10, 0, 0);
        var raw = P("IMG_1.CR2"); var jpg = P("IMG_1.JPG");
        var b1 = P("B1.jpg", taken: t); var b2 = P("B2.jpg", taken: t.AddSeconds(0.5), rating: 3); var b3 = P("B3.jpg", taken: t.AddSeconds(1));
        var lone = P("L.jpg", taken: t.AddSeconds(10));
        var stacks = StackBuilder.Build([raw, jpg, b1, b2, b3, lone]);
        Assert.Equal(2, stacks.Count);
        var rawStack = stacks.Single(s => s.Kind == StackKind.RawJpeg);
        Assert.Equal(jpg, rawStack.Top);
        var burst = stacks.Single(s => s.Kind == StackKind.Burst);
        Assert.Equal(3, burst.Photos.Count);
        Assert.Equal(b2, burst.Top);                                                // mais estrelas
    }

    [Fact]
    public void Analysis_HashFindsSimilar_AndSharpnessSpotsBlur()
    {
        byte[] Pattern(int w, int h, Func<int, int, byte> f) { var px = new byte[w * h]; for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) px[y * w + x] = f(x, y); return px; }
        var sharp = Pattern(128, 96, (x, y) => (byte)((x / 8 + y / 8) % 2 == 0 ? 230 : 20));
        var sameSmaller = Pattern(64, 48, (x, y) => (byte)((x / 4 + y / 4) % 2 == 0 ? 225 : 25));
        var blurred = Pattern(128, 96, (x, y) => (byte)(120 + 10 * Math.Sin(x / 40.0)));
        var gradient = Pattern(128, 96, (x, _) => (byte)(x * 2));
        var hSharp = ImageAnalysis.DifferenceHash(sharp, 128, 96);
        Assert.True(ImageAnalysis.Distance(hSharp, ImageAnalysis.DifferenceHash(sameSmaller, 64, 48)) <= SimilarityFinder.DefaultThreshold);
        Assert.True(ImageAnalysis.Distance(hSharp, ImageAnalysis.DifferenceHash(gradient, 128, 96)) > SimilarityFinder.DefaultThreshold);
        Assert.True(ImageAnalysis.Sharpness(sharp, 128, 96) > 150);
        Assert.True(ImageAnalysis.Sharpness(blurred, 128, 96) < LibraryQuery.BlurThreshold);

        var a = P("a.jpg", rating: 1); a.PerceptualHash = hSharp; a.Sharpness = 300;
        var b = P("b.jpg"); b.PerceptualHash = hSharp ^ 0b11; b.Sharpness = 500;
        var c = P("c.jpg"); c.PerceptualHash = ImageAnalysis.DifferenceHash(gradient, 128, 96);
        var groups = SimilarityFinder.Find([a, b, c]);
        Assert.Single(groups);
        Assert.Equal(a, groups[0].Best);                                            // 1 estrela vence nitidez
    }

    [Fact]
    public void Stats_CountSpaceAndReclaimable()
    {
        var v = P("v.mp4", 1080, 1920, 3600); v.FileSize = 3L << 30;
        var r = P("r.jpg"); r.Pick = PickFlag.Rejected; r.FileSize = 5000;
        var d1 = P("d1.jpg"); d1.ContentHash = "X"; d1.FileSize = 2000;
        var d2 = P("d2.jpg"); d2.ContentHash = "X"; d2.FileSize = 2000;
        var stats = LibraryStats.Compute([v, r, d1, d2]);
        Assert.Equal(1, stats.Videos);
        Assert.Equal(3600, stats.VideoSeconds);
        Assert.Equal(5000, stats.RejectedBytes);
        Assert.Equal(2000, stats.DuplicateReclaimableBytes);
        Assert.Equal(7000, stats.ReclaimableBytes);
        Assert.Single(stats.LargestVideos);
        Assert.Equal("1 h 00 min", LibraryStats.FormatHours(3600));
    }

    // ---------- cortes para o editor ----------

    [Fact]
    public void ClipExport_WritesEdlAndFcpxml()
    {
        var clip = P("20260927_055939.mp4", seconds: 28);
        var items = new[] { (new ClipMarker(1, clip.Id, 2.5, 7.5, "Chegada & feira", 4), clip), (new ClipMarker(2, clip.Id, 10, 12, "Detalhe", 0), clip) };
        var edl = ClipExport.ToEdl("Vlog 12", items);
        Assert.Contains("001  AX001    V     C        00:00:02:15 00:00:07:15 01:00:00:00 01:00:05:00", edl);
        Assert.Contains("002  AX002    V     C        00:00:10:00 00:00:12:00 01:00:05:00 01:00:07:00", edl);
        Assert.Contains("* FROM CLIP NAME: 20260927_055939.mp4", edl);
        var xml = ClipExport.ToFcpxml("Vlog 12", items);
        var doc = System.Xml.Linq.XDocument.Parse(xml);                             // XML válido (o & foi escapado)
        Assert.Equal(2, doc.Descendants("asset-clip").Count());
        Assert.Single(doc.Descendants("asset"));
        Assert.Equal("150/30s", doc.Descendants("asset-clip").First().Attribute("duration")!.Value);
    }

    // ---------- repositório ----------

    [Fact]
    public async Task Repository_StoresPickUsageMarkersSmartCollectionsBackupsAndAnalysis()
    {
        var png = _env.CreatePng("r/a.png");
        await _env.Catalog.ImportPathsAsync([png]);
        var photo = (await _env.Repository.FindByPathAsync(Path.GetFullPath(png)))!;
        ILibraryRepository repo = _env.Repository;
        await repo.UpdatePickAsync([photo.Id], PickFlag.Rejected);
        await repo.UpdateUsageAsync([photo.Id], UsageStatus.Published, "Vlog #12");
        await repo.UpdateAnalysisAsync(photo.Id, 12345, 88.5);
        var reloaded = (await _env.Repository.FindByPathAsync(photo.CurrentPath))!;
        Assert.Equal((PickFlag.Rejected, UsageStatus.Published, "Vlog #12", 12345L, 88.5), (reloaded.Pick, reloaded.Usage, reloaded.UsageNote, reloaded.PerceptualHash!.Value, reloaded.Sharpness!.Value));

        var marker = await repo.AddMarkerAsync(photo.Id, 9, 3, "Trecho", 2);
        Assert.Equal((3d, 9d), (marker.InSeconds, marker.OutSeconds));             // entrada/saída invertidas são corrigidas
        await repo.UpdateMarkerAsync(marker with { Name = "Abertura" });
        Assert.Equal("Abertura", (await repo.GetMarkersAsync(photo.Id)).Single().Name);
        await repo.DeleteMarkerAsync(marker.Id);
        Assert.Empty(await repo.GetMarkersAsync());

        var smart = await repo.SaveSmartCollectionAsync(null, "Shorts sem uso", "shorts:sim -uso:usado");
        await repo.SaveSmartCollectionAsync(smart.Id, "Shorts livres", smart.Query);
        Assert.Equal("Shorts livres", (await repo.GetSmartCollectionsAsync()).Single().Name);

        var backup = await repo.SaveBackupTargetAsync(null, @"D:\Fotos", @"E:\Backup");
        await repo.UpdateBackupStatusAsync(backup.Id, DateTime.UtcNow, 100, 2, 1);
        var stored = (await repo.GetBackupTargetsAsync()).Single();
        Assert.Equal((100, 2, 1), (stored.CheckedCount!.Value, stored.MissingCount!.Value, stored.DifferentCount!.Value));
        Assert.False(stored.IsHealthy);
    }

    // ---------- serviços de disco ----------

    [Fact]
    public async Task Export_ResizesWatermarksRenamesAndCopiesVideos()
    {
        var png = _env.CreatePng("e/foto.png", width: 400, height: 200);
        var video = Path.Combine(_env.Photos, "e", "clip.mp4");
        File.WriteAllBytes(video, new byte[10]);
        var photos = new[]
        {
            new Photo { Id = 1, FileName = "foto.png", CurrentPath = png, Extension = ".png", CreatedAt = new DateTime(2026, 9, 27, 12, 0, 0) },
            new Photo { Id = 2, FileName = "clip.mp4", CurrentPath = video, Extension = ".mp4", CreatedAt = new DateTime(2026, 9, 27, 12, 0, 0) }
        };
        var dest = Path.Combine(_env.Root, "export");
        var result = await new ExportService().ExportAsync(photos, new ExportPreset("t", 100, "jpg", 80, "© Teste", NameTemplate: "{data}_{seq}"), dest);
        Assert.Empty(result.Failed);
        Assert.Equal(["2026-09-27_001.jpg", "2026-09-27_002.mp4"], result.Created.Select(Path.GetFileName).OrderBy(n => n));
        var size = ImageLoader.ReadSize(Path.Combine(dest, "2026-09-27_001.jpg"));
        Assert.Equal((100, 50), size);
        var again = await new ExportService().ExportAsync(photos.Take(1).ToList(), new ExportPreset("t", NameTemplate: "{data}_{seq}"), dest);
        Assert.Equal("2026-09-27_001 (2).jpg", Path.GetFileName(again.Created.Single()));   // nunca sobrescreve
    }

    [Fact]
    public async Task Backup_FindsMissingAndDifferent_AndCopiesThem()
    {
        var source = Directory.CreateDirectory(Path.Combine(_env.Root, "src")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(_env.Root, "bkp")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "2026"));
        File.WriteAllText(Path.Combine(source, "2026", "a.jpg"), "aaa");
        File.WriteAllText(Path.Combine(source, "b.jpg"), "bbbb");
        File.WriteAllText(Path.Combine(source, "c.jpg"), "c");
        File.WriteAllText(Path.Combine(target, "b.jpg"), "bb");                     // tamanho diferente
        File.WriteAllText(Path.Combine(target, "c.jpg"), "c");
        var verifier = new BackupVerifier();
        var spec = new BackupTarget(1, source, target, null, null, null, null);
        var report = await verifier.VerifyAsync(spec);
        Assert.Equal(3, report.Checked);
        Assert.Equal([Path.Combine("2026", "a.jpg")], report.Missing);
        Assert.Equal(["b.jpg"], report.Different);
        Assert.Equal(2, await verifier.CopyMissingAsync(spec, report));
        Assert.True((await verifier.VerifyAsync(spec)).IsComplete);
        await Assert.ThrowsAsync<IOException>(() => verifier.VerifyAsync(spec with { TargetFolder = Path.Combine(_env.Root, "desconectado") }));
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task ModelManager_DownloadsVerifiesAndRemoves_AndTilesAreCached()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var manager = new AiModelManager(Path.Combine(_env.Root, "models"), http);
        var model = AiModelCatalog.Models[0] with { Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)) };
        Assert.False(manager.IsInstalled(model));
        await manager.DownloadAsync(model);
        Assert.True(manager.IsInstalled(model));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadAsync(model with { Id = "x", FileName = "x.onnx", Sha256 = "00" }));
        Assert.False(File.Exists(Path.Combine(manager.ModelsFolder, "x.onnx")));
        manager.Remove(model);
        Assert.False(manager.IsInstalled(model));

        var tiles = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var provider = new MapTileProvider(Path.Combine(_env.Root, "tiles"), tiles);
        var first = await provider.GetTileAsync(3, 9, 2);                           // x dá a volta: 9 → 1
        Assert.EndsWith(Path.Combine("3", "1", "2.png"), first);
        await provider.GetTileAsync(3, 1, 2);
        Assert.Single(tiles.Urls);                                                 // segunda vez veio do cache
        Assert.Null(await provider.GetTileAsync(3, 1, 9));                         // fora do mapa
        var (lat, lon) = MapMath.ToLatLon(MapMath.ToPixel(-23.5, -46.6, 10).X, MapMath.ToPixel(-23.5, -46.6, 10).Y, 10);
        Assert.Equal(-23.5, lat, 6); Assert.Equal(-46.6, lon, 6);
    }

    // ---------- IA local (motores falsos: a infraestrutura funciona de ponta a ponta) ----------

    private sealed class FakeEmbedder : IImageTextEmbedder
    {
        public AiCapability Capability => AiCapability.SemanticSearch;
        public bool IsReady => true;
        public string ModelId => "fake";
        public Task<float[]> EmbedImageAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Path.GetFileName(path).Contains("praia") ? new[] { 1f, 0f, 0f } : new[] { 0f, 1f, 0f });
        public Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(text.Contains("beach") || text.Contains("praia") ? new[] { 1f, 0f, 0f } : new[] { 0f, 0.2f, 1f });
    }

    private sealed class FakeFaces : IFaceAnalyzer
    {
        public AiCapability Capability => AiCapability.Faces;
        public bool IsReady => true;
        public Task<IReadOnlyList<DetectedFace>> DetectAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DetectedFace>>(Path.GetFileName(path).Contains("ana") ? [new DetectedFace(0.1f, 0.1f, 0.2f, 0.2f, [1f, 0f])] : [new DetectedFace(0.5f, 0.5f, 0.2f, 0.2f, [0f, 1f])]);
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public AiCapability Capability => AiCapability.Transcription;
        public bool IsReady => true;
        public Task<Transcript?> TranscribeAsync(string videoPath, CancellationToken cancellationToken = default) => Task.FromResult<Transcript?>(new("pt", "fala do vídeo " + Path.GetFileName(videoPath)));
    }

    [Fact]
    public async Task AiIndex_EmbedsTagsFacesAndTranscripts_IncrementallyAndSearches()
    {
        var files = new[] { "praia1.png", "praia2.png", "ana_casa.png", "ana_rua.png", "beto.png" }.Select(n => _env.CreatePng("ai/" + n)).ToList();
        var video = Path.Combine(_env.Photos, "ai", "vlog.mp4");
        File.WriteAllBytes(video, new byte[10]);
        await _env.Catalog.ImportPathsAsync(files);
        var photos = await _env.Catalog.GetPhotosAsync();
        photos = [.. photos, new Photo { Id = 999, FileName = "vlog.mp4", CurrentPath = video, Extension = ".mp4" }];
        IAiRepository repo = _env.Repository;
        var service = new AiIndexService(new AiEngineRegistry([new FakeEmbedder(), new FakeFaces(), new FakeTranscriber()]), repo);
        var all = new[] { AiCapability.SemanticSearch, AiCapability.AutoTags, AiCapability.Faces, AiCapability.Transcription };
        var catalogPhotos = photos.Where(p => p.Id != 999).ToList();

        var processed = await service.IndexAsync(catalogPhotos, all, p => Task.FromResult<string?>(p.CurrentPath));
        Assert.Equal(15, processed);                                               // 5 × (busca, etiquetas, rostos); transcrição só vídeo
        Assert.Equal(0, await service.IndexAsync(catalogPhotos, all, p => Task.FromResult<string?>(p.CurrentPath)));   // nada de novo

        var hits = await service.SearchAsync("praia ao pôr do sol");
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Contains("praia", catalogPhotos.Single(p => p.Id == h.PhotoId).FileName));
        Assert.Contains("praia", (await repo.GetTagsAsync())[hits[0].PhotoId]);

        var people = await repo.GetPeopleAsync();
        Assert.Equal(2, people.Count);
        Assert.Equal(3, people[0].FaceCount);                                      // ana (2) e o resto (3: praias + beto) em grupos diferentes
        await repo.RenamePersonAsync(people[1].Id, "Ana");
        Assert.Contains(await repo.GetPeopleAsync(), p => p.DisplayName == "Ana" && p.FaceCount == 2);

        var registryWithout = new AiEngineRegistry();
        Assert.False(registryWithout.IsReady(AiCapability.SemanticSearch));
        Assert.Empty(await new AiIndexService(registryWithout, repo).SearchAsync("praia"));
    }
}
