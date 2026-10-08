using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>Cobre os fluxos das Fases 2-4 pela API pública da LibraryViewModel; deve continuar verde após a refatoração visual.</summary>
public sealed class LibraryRegressionTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private async Task<LibraryViewModel> ImportedLibraryAsync(params string[] files)
    {
        foreach (var file in files) _env.CreatePng(file);
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        return library;
    }

    [Fact]
    public async Task Import_IsRecursive_AndIdempotent()
    {
        var library = await ImportedLibraryAsync("a.png", "sub/b.png");
        Assert.Equal(2, library.Photos.Count);
        await library.ImportFolderAsync(_env.Photos);
        Assert.Equal(2, library.Photos.Count);
        Assert.All(library.Photos, p => Assert.NotNull(p.Photo.Width));
    }

    [Fact]
    public async Task Import_SkipsCorruptFile_WithoutAbortingTheBatch()
    {
        File.WriteAllText(Path.Combine(_env.Photos, "broken.jpg"), "not an image");
        var library = await ImportedLibraryAsync("ok.png");
        Assert.Contains(library.Photos, p => p.FileName == "ok.png");
    }

    [Fact]
    public async Task Organization_PersistsAcrossReload()
    {
        var library = await ImportedLibraryAsync("a.png", "b.png");
        var card = library.Photos.First(p => p.FileName == "a.png");
        library.SelectedPhoto = card;
        var col = await _env.Collections.CreateAsync("Melhores", null);
        await library.AddPhotoToCollectionAsync(card, col.Id);
        card.Category = "Viagens"; card.TagsText = "praia, sol"; card.PersonalNote = "nota"; card.Rating = 4; card.IsFavorite = true;
        await library.SaveSelectedAsync();

        var reloaded = _env.CreateLibrary();
        await TestEnvironment.WaitUntilAsync(() => reloaded.Photos.Count == 2);
        var photo = reloaded.Photos.First(p => p.FileName == "a.png").Photo;
        Assert.Equal("Viagens", photo.CategoryName);
        Assert.Equal(["praia", "sol"], photo.Tags.Order());
        Assert.Equal(["Melhores"], photo.Collections);
        Assert.Equal("nota", photo.PersonalNote);
        Assert.Equal(4, photo.Rating);
        Assert.True(photo.IsFavorite);
    }

    [Fact]
    public async Task Filters_BySearchCategoryTagCollectionAndFavorites()
    {
        var library = await ImportedLibraryAsync("alpha.png", "beta.png", "gamma.png");
        var alpha = library.Photos.First(p => p.FileName == "alpha.png");
        library.SelectedPhoto = alpha; alpha.Category = "Cidades"; alpha.TagsText = "noite"; alpha.IsFavorite = true;
        var topCol = await _env.Collections.CreateAsync("Top", null);
        await library.AddPhotoToCollectionAsync(alpha, topCol.Id);
        await library.SaveSelectedAsync();

        library.SearchText = "bet"; await library.ApplyFiltersAsync(); Assert.Equal(["beta.png"], library.Photos.Select(p => p.FileName));
        library.SearchText = ""; library.CategoryFilter = "Cidades"; await library.ApplyFiltersAsync(); Assert.Equal(["alpha.png"], library.Photos.Select(p => p.FileName));
        library.CategoryFilter = ""; library.TagFilter = "noite"; await library.ApplyFiltersAsync(); Assert.Single(library.Photos);
        library.TagFilter = ""; library.CollectionFilter = "Top"; await library.ApplyFiltersAsync(); Assert.Single(library.Photos);
        library.CollectionFilter = ""; library.FavoritesOnly = true; await library.ApplyFiltersAsync(); Assert.Single(library.Photos);
        library.FavoritesOnly = false; await library.ApplyFiltersAsync(); Assert.Equal(3, library.Photos.Count);
    }

    [Fact]
    public async Task Batch_AppliesCategoryTagAndCollection_WithoutTouchingFiles()
    {
        var library = await ImportedLibraryAsync("a.png", "b.png");
        var before = library.Photos.Select(p => File.GetLastWriteTimeUtc(p.Photo.CurrentPath)).ToList();
        await _env.Collections.CreateAsync("c1", null);
        await library.ApplyBatchAsync(library.Photos.ToList(), "Lote", "t1", "c1", 0, false);
        Assert.All(library.Photos, p => { Assert.Equal("Lote", p.Photo.CategoryName); Assert.Contains("t1", p.Photo.Tags); Assert.Contains("c1", p.Photo.Collections); });
        Assert.Equal(before, library.Photos.Select(p => File.GetLastWriteTimeUtc(p.Photo.CurrentPath)).ToList());
    }

    [Fact]
    public async Task Move_KeepsIdentityAndOrganization()
    {
        var library = await ImportedLibraryAsync("a.png");
        var card = library.Photos.Single(); var id = card.Photo.Id;
        library.SelectedPhoto = card; card.Category = "Keep"; await library.SaveSelectedAsync();
        var destination = Path.Combine(_env.Root, "dest");
        await library.MoveSelectedAsync([library.Photos.Single()], destination);
        var moved = library.Photos.Single().Photo;
        Assert.Equal(id, moved.Id); Assert.Equal("Keep", moved.CategoryName);
        Assert.True(File.Exists(Path.Combine(destination, "a.png"))); Assert.False(File.Exists(Path.Combine(_env.Photos, "a.png")));
    }

    [Fact]
    public async Task Move_DoesNotOverwriteExistingFile()
    {
        var library = await ImportedLibraryAsync("a.png");
        var destination = Path.Combine(_env.Root, "dest"); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "a.png"), "keep me");
        await library.MoveSelectedAsync([library.Photos.Single()], destination);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(destination, "a.png")));
        Assert.True(File.Exists(Path.Combine(_env.Photos, "a.png")));
    }

    [Fact]
    public async Task Copy_WithAndWithoutCatalog()
    {
        var library = await ImportedLibraryAsync("a.png");
        var dest1 = Path.Combine(_env.Root, "c1"); var dest2 = Path.Combine(_env.Root, "c2");
        await library.CopySelectedAsync([library.Photos.Single()], dest1, false);
        Assert.True(File.Exists(Path.Combine(dest1, "a.png")));
        await library.ApplyFiltersAsync(); Assert.Single(library.Photos);
        await library.CopySelectedAsync([library.Photos.Single()], dest2, true);
        await library.ApplyFiltersAsync(); Assert.Equal(2, library.Photos.Count);
        Assert.Equal(2, library.Photos.Select(p => p.Photo.Id).Distinct().Count());
    }

    [Fact]
    public async Task Rename_Single_AndBatchTemplate()
    {
        var library = await ImportedLibraryAsync("a.png", "b.png");
        var a = library.Photos.First(p => p.FileName == "a.png"); var id = a.Photo.Id;
        await library.RenameSelectedAsync([a], "novo");
        Assert.True(File.Exists(Path.Combine(_env.Photos, "novo.png")));
        Assert.Equal(id, library.Photos.First(p => p.FileName == "novo.png").Photo.Id);
        await library.RenameBatchAsync(library.Photos.ToList(), "{year}-{sequence}");
        Assert.All(library.Photos, p => Assert.Matches(@"^\d{4}-00\d\.png$", p.FileName));
    }

    [Fact]
    public async Task Recycle_RemovesFile_AndMarksPhotoMissing()
    {
        var library = await ImportedLibraryAsync("a.png");
        var path = library.Photos.Single().Photo.CurrentPath;
        await library.RecycleSelectedAsync([library.Photos.Single()]);
        Assert.False(File.Exists(path));
        Assert.True(library.Photos.Single().IsMissing);
    }
}
