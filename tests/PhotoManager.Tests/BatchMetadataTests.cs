using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoManager.Application.Metadata;
using PhotoManager.Infrastructure.Metadata;

namespace PhotoManager.Tests;

public sealed class BatchMetadataTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private static MetadataEdit Current() => new() { Title = "Antigo", Description = "Texto", Keywords = ["a", "b", "c"], Author = "Ana", Copyright = "© Ana" };

    // ---------- lógica pura do plano

    [Fact]
    public void Keep_NeverTouchesAField()
    {
        var plan = new BatchMetadataPlan { Title = "ignorado", Description = "ignorado", Keywords = ["x"], Author = "ignorado", Copyright = "ignorado" };
        Assert.True(plan.IsNoOp);
        Assert.True(plan.Apply(Current()).SameAs(Current()));
    }

    [Fact]
    public void EachFieldOperatesIndependently()
    {
        var plan = new BatchMetadataPlan { TitleOperation = BatchOperation.Replace, Title = "Novo", AuthorOperation = BatchOperation.Replace, Author = "Bia" };
        var result = plan.Apply(Current());
        Assert.Equal("Novo", result.Title); Assert.Equal("Bia", result.Author);
        Assert.Equal("Texto", result.Description); Assert.Equal(["a", "b", "c"], result.Keywords); Assert.Equal("© Ana", result.Copyright);
    }

    [Fact]
    public void Description_ReplaceAndAppend()
    {
        Assert.Equal("Outro", new BatchMetadataPlan { DescriptionOperation = BatchOperation.Replace, Description = "Outro" }.Apply(Current()).Description);
        Assert.Equal("Texto extra", new BatchMetadataPlan { DescriptionOperation = BatchOperation.Append, Description = "extra" }.Apply(Current()).Description);
        Assert.Equal("extra", new BatchMetadataPlan { DescriptionOperation = BatchOperation.Append, Description = "extra" }.Apply(new MetadataEdit()).Description);
    }

    [Fact]
    public void Keywords_ReplaceAddRemoveClear()
    {
        MetadataEdit Run(BatchOperation op, params string[] words) => new BatchMetadataPlan { KeywordsOperation = op, Keywords = words }.Apply(Current());
        Assert.Equal(["x", "y"], Run(BatchOperation.Replace, "x", "y").Keywords);
        Assert.Equal(["a", "b", "c", "d"], Run(BatchOperation.Add, "B", "d", "d").Keywords);       // sem duplicar, ignorando maiúsculas
        Assert.Equal(["a", "c"], Run(BatchOperation.Remove, "B", "zzz").Keywords);                  // ignora maiúsculas e ausentes
        Assert.Empty(Run(BatchOperation.Clear).Keywords);
    }

    [Fact]
    public void Validation_RejectsMissingValuesAndForbiddenOperations()
    {
        Assert.Empty(new BatchMetadataPlan().Validate());
        Assert.Contains(new BatchMetadataPlan { TitleOperation = BatchOperation.Replace }.Validate(), p => p.StartsWith("Título"));
        Assert.Contains(new BatchMetadataPlan { TitleOperation = BatchOperation.Append, Title = "x" }.Validate(), p => p.Contains("não permitida"));
        Assert.Contains(new BatchMetadataPlan { KeywordsOperation = BatchOperation.Add }.Validate(), p => p.StartsWith("Palavras"));
        Assert.Empty(new BatchMetadataPlan { KeywordsOperation = BatchOperation.Clear }.Validate());   // limpar não precisa de valor
    }

    [Fact]
    public void Plan_RoundTripsThroughJson()
    {
        var plan = new BatchMetadataPlan { AuthorOperation = BatchOperation.Replace, Author = "Marcos André", CopyrightOperation = BatchOperation.Replace, Copyright = "© Marcos André", KeywordsOperation = BatchOperation.Add, Keywords = ["Brazil", "travel"] };
        var json = plan.ToJson();
        Assert.Contains("\"Add\"", json);
        Assert.Equal(plan.ToJson(), BatchMetadataPlan.FromJson(json).ToJson());
        Assert.Equal(["Brazil", "travel"], BatchMetadataPlan.FromJson(json).Keywords);
    }

    // ---------- presets

    [Fact]
    public async Task Presets_SaveUpdateListDelete_PersistentAndCaseInsensitiveByName()
    {
        IMetadataPresetRepository presets = _env.Repository;
        var plan = new BatchMetadataPlan { AuthorOperation = BatchOperation.Replace, Author = "Marcos André" };
        var saved = await presets.SaveAsync("Microstock Marcos", plan);
        var updated = await presets.SaveAsync("microstock marcos", plan with { Author = "Outro" });
        Assert.Equal(saved.Id, updated.Id);
        var all = await presets.GetAllAsync();
        var preset = Assert.Single(all);
        Assert.Equal("Outro", preset.Plan.Author);
        await Assert.ThrowsAsync<ArgumentException>(() => presets.SaveAsync("  ", plan));
        await presets.DeleteAsync(preset.Id);
        Assert.Empty(await presets.GetAllAsync());
    }

    // ---------- serviço em arquivos reais

    private async Task<IReadOnlyList<Domain.Photos.Photo>> ImportAsync(int jpegs, bool withPng = false)
    {
        for (var i = 1; i <= jpegs; i++)
        {
            var encoder = new JpegBitmapEncoder();
            var metadata = new BitmapMetadata("jpg") { CameraModel = "Cam" + i };
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 16 * 4], 16 * 4), null, metadata, null));
            using var stream = File.Create(Path.Combine(_env.Photos, $"p{i}.jpg"));
            encoder.Save(stream);
        }
        if (withPng) _env.CreatePng("x.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        return (await _env.Catalog.GetPhotosAsync()).OrderBy(p => p.FileName).ToList();
    }

    [Fact]
    public async Task Apply_WritesEveryPhoto_KeepingEachPhotosOwnValues_AndVersioning()
    {
        var photos = await ImportAsync(3);
        await _env.MetadataEditing.SaveAsync(photos[0], new MetadataEdit { Title = "Foto 1", Keywords = ["rio", "sol"] });
        await _env.MetadataEditing.SaveAsync(photos[1], new MetadataEdit { Title = "Foto 2", Keywords = ["mar"] });

        var plan = new BatchMetadataPlan
        {
            AuthorOperation = BatchOperation.Replace, Author = "Marcos André", CopyrightOperation = BatchOperation.Replace, Copyright = "© Marcos André",
            KeywordsOperation = BatchOperation.Add, Keywords = ["Brazil", "travel"]
        };
        var result = await _env.BatchMetadata.ApplyAsync(photos, plan);
        Assert.Equal(3, result.Changed);

        var r0 = MetadataExtractorReader.Read(photos[0].CurrentPath); var r1 = MetadataExtractorReader.Read(photos[1].CurrentPath); var r2 = MetadataExtractorReader.Read(photos[2].CurrentPath);
        Assert.Equal("Foto 1", r0.Title); Assert.Equal("Foto 2", r1.Title); Assert.Null(r2.Title);               // título mantido por foto
        Assert.Equal(["rio", "sol", "Brazil", "travel"], r0.Keywords); Assert.Equal(["mar", "Brazil", "travel"], r1.Keywords); Assert.Equal(["Brazil", "travel"], r2.Keywords);
        Assert.All(new[] { r0, r1, r2 }, r => { Assert.Equal("Marcos André", r.Author); Assert.Equal("© Marcos André", r.Copyright); });
        Assert.Equal(["Cam1", "Cam2", "Cam3"], new[] { r0, r1, r2 }.Select(r => r.Camera));                       // EXIF intacto
        Assert.Equal([2, 2, 1], photos.Select(p => p.MetadataVersion));
    }

    [Fact]
    public async Task Apply_IsIdempotent_SecondRunChangesNothing_AndCreatesNoVersions()
    {
        var photos = await ImportAsync(2);
        var plan = new BatchMetadataPlan { AuthorOperation = BatchOperation.Replace, Author = "Ana" };
        await _env.BatchMetadata.ApplyAsync(photos, plan);
        var bytes = photos.Select(p => File.ReadAllBytes(p.CurrentPath)).ToList();
        var second = await _env.BatchMetadata.ApplyAsync(photos, plan);
        Assert.Equal(2, second.Unchanged); Assert.Equal(0, second.Changed);
        Assert.Equal(bytes, photos.Select(p => File.ReadAllBytes(p.CurrentPath)).ToList());
        Assert.All(photos, p => Assert.Equal(1, p.MetadataVersion));
    }

    [Fact]
    public async Task Apply_SkipsReadOnlyAndMissing_ReportsFailures_AndContinues()
    {
        var photos = await ImportAsync(3, withPng: true);
        File.Delete(photos.First(p => p.FileName == "p3.jpg").CurrentPath);                       // some do disco
        File.WriteAllText(photos.First(p => p.FileName == "p2.jpg").CurrentPath, "corrompido");   // não é JPEG: a gravação falha
        var result = await _env.BatchMetadata.ApplyAsync(photos.ToList(), new BatchMetadataPlan { TitleOperation = BatchOperation.Replace, Title = "T" });

        BatchItemResult Of(string name) => result.Items.Single(i => i.Photo.FileName == name);
        Assert.Equal(BatchItemStatus.Changed, Of("p1.jpg").Status);
        Assert.Equal(BatchItemStatus.Failed, Of("p2.jpg").Status);
        Assert.Equal(BatchItemStatus.Changed, Of("x.png").Status);   // PNG agora grava no sidecar .xmp
        Assert.Equal(BatchItemStatus.Skipped, Of("p3.jpg").Status);   // ausente
        Assert.Equal("corrompido", File.ReadAllText(Of("p2.jpg").Photo.CurrentPath));
        Assert.Equal("T", MetadataExtractorReader.Read(Of("p1.jpg").Photo.CurrentPath).Title);
        Assert.Equal((2, 1, 1), (result.Changed, result.Failed, result.Skipped));
    }

    [Fact]
    public async Task Preview_ReportsWhatWouldChange_WithoutWriting()
    {
        var photos = await ImportAsync(2);
        await _env.MetadataEditing.SaveAsync(photos[0], new MetadataEdit { Author = "Ana" });
        var before = photos.Select(p => File.ReadAllBytes(p.CurrentPath)).ToList();
        var items = await _env.BatchMetadata.PreviewAsync(photos, new BatchMetadataPlan { AuthorOperation = BatchOperation.Replace, Author = "Ana", CopyrightOperation = BatchOperation.Replace, Copyright = "©" });
        Assert.Equal([BatchItemStatus.WillChange, BatchItemStatus.WillChange], items.Select(i => i.Status));
        Assert.Equal(["Copyright"], items[0].ChangedFields);            // autor já era "Ana"
        Assert.Equal(["Autor", "Copyright"], items[1].ChangedFields);
        Assert.Equal(before, photos.Select(p => File.ReadAllBytes(p.CurrentPath)).ToList());
    }

    [Fact]
    public async Task Apply_RejectsInvalidPlan_BeforeTouchingAnyFile()
    {
        var photos = await ImportAsync(1);
        var before = File.ReadAllBytes(photos[0].CurrentPath);
        await Assert.ThrowsAsync<ArgumentException>(() => _env.BatchMetadata.ApplyAsync(photos, new BatchMetadataPlan { TitleOperation = BatchOperation.Replace }));
        Assert.Equal(before, File.ReadAllBytes(photos[0].CurrentPath));
    }

    [Fact]
    public async Task Apply_ReportsProgress_AndHonoursCancellation()
    {
        var photos = await ImportAsync(3);
        var reports = new List<BatchProgress>();
        await _env.BatchMetadata.ApplyAsync(photos, new BatchMetadataPlan { AuthorOperation = BatchOperation.Replace, Author = "A" }, new SyncProgress<BatchProgress>(reports.Add));
        Assert.Equal(4, reports.Count); Assert.Equal(3, reports[^1].Processed);

        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _env.BatchMetadata.ApplyAsync(photos, new BatchMetadataPlan { AuthorOperation = BatchOperation.Replace, Author = "B" }, null, cts.Token));
        Assert.Equal("A", MetadataExtractorReader.Read(photos[0].CurrentPath).Author);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T> { public void Report(T value) => handler(value); }
}
