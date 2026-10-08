using PhotoManager.Infrastructure.Images;

namespace PhotoManager.Tests;

/// <summary>Usa as CR2 reais de docs/Fotos (somente leitura). Se a pasta não existir na máquina, os testes passam sem verificar nada.</summary>
public sealed class RawSupportTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private static string? FindRawFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "Fotos");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.CR2").Length > 0) return candidate;
        }
        return null;
    }

    [Fact]
    public void ExtractsEmbeddedJpegPreview_FromRealCr2()
    {
        if (FindRawFolder() is not { } folder) return;
        foreach (var file in Directory.GetFiles(folder, "*.CR2"))
        {
            var preview = RawPreview.TryRead(file);
            Assert.NotNull(preview);
            Assert.True(preview!.Jpeg.Length > 100_000);
            Assert.Equal(0xFF, preview.Jpeg[0]); Assert.Equal(0xD8, preview.Jpeg[1]);
            var (width, height) = ImageLoader.ReadSize(file);
            Assert.True(width >= 3000 && height >= 2000, $"dimensões do sensor esperadas, obtido {width}x{height}");
            Assert.NotNull(ImageLoader.LoadPreview(file, 800));
        }
    }

    [Fact]
    public async Task ImportingFolderWithCr2_CatalogsThem_WithThumbnailsAndReadOnlyMetadata()
    {
        if (FindRawFolder() is not { } folder) return;
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(folder);
        Assert.Equal(Directory.GetFiles(folder, "*.CR2").Length, library.Photos.Count);
        Assert.All(library.Photos, p => { Assert.Equal(".cr2", p.Photo.Extension); Assert.True(p.Photo.Width > 3000); Assert.Equal("CR2", p.StatusText); });
        await TestEnvironment.WaitUntilAsync(() => library.Photos.All(p => p.ThumbnailUri is not null));

        library.SelectedPhoto = library.Photos[0];
        await TestEnvironment.WaitUntilAsync(() => library.PreviewImage is not null);
        Assert.False(_env.MetadataEditing.CanEdit(library.Photos[0].Photo));
    }

    [Fact]
    public async Task ImportingCr2_IsIdempotent_AndTolerantToCorruptRaw()
    {
        File.WriteAllText(Path.Combine(_env.Photos, "broken.cr2"), "not a raw file");
        _env.CreatePng("ok.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        await library.ImportFolderAsync(_env.Photos);
        Assert.Contains(library.Photos, p => p.FileName == "ok.png");
        Assert.Equal(library.Photos.Count, library.Photos.Select(p => p.Photo.Id).Distinct().Count());
    }
}
