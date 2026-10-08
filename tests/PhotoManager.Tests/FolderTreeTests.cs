using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

public sealed class FolderTreeTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private async Task<LibraryViewModel> ImportNestedAsync()
    {
        _env.CreatePng("Viagens/2026/Rio/a.png");
        _env.CreatePng("Viagens/2026/Rio/b.png");
        _env.CreatePng("Viagens/2026/c.png");
        _env.CreatePng("Viagens/2025/d.png");
        _env.CreatePng("Cidades/e.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        return library;
    }

    private static FolderNode Find(LibraryViewModel library, string label) =>
        library.Folders.SelectMany(r => r.SelfAndDescendants()).Single(n => n.Label == label);

    [Fact]
    public async Task ImportingAFolder_ShowsItsSubfoldersAsABranch_WithRecursiveCounts()
    {
        var library = await ImportNestedAsync();
        var root = Assert.Single(library.Folders);             // cadeia de pastas vazias acima de "photos" é recolhida
        Assert.Equal(_env.Photos, root.Path);
        Assert.Equal(5, root.Count);
        Assert.Equal(["Cidades", "Viagens"], root.Children.Select(c => c.Label));
        var viagens = Find(library, "Viagens");
        Assert.Equal(4, viagens.Count); Assert.Equal(0, viagens.OwnCount);
        Assert.Equal(["2025", "2026"], viagens.Children.Select(c => c.Label));
        Assert.Equal(3, Find(library, "2026").Count);
        Assert.Equal(2, Find(library, "Rio").Count);
    }

    [Fact]
    public async Task SelectingAFolder_ShowsItsPhotosAndThoseOfSubfolders()
    {
        var library = await ImportNestedAsync();
        library.SelectFolderCommand.Execute(Find(library, "2026"));
        Assert.Equal(["a.png", "b.png", "c.png"], library.Photos.Select(p => p.FileName).Order());
        Assert.True(Find(library, "2026").IsSelected); Assert.False(Find(library, "2025").IsSelected);

        library.SelectFolderCommand.Execute(Find(library, "Rio"));
        Assert.Equal(2, library.Photos.Count);

        library.SelectFolderCommand.Execute(library.Folders[0]);
        Assert.Equal(5, library.Photos.Count);
    }

    [Fact]
    public async Task FolderPrefixMatching_DoesNotConfuseSiblingsWithSimilarNames()
    {
        _env.CreatePng("Foto/a.png"); _env.CreatePng("Foto2/b.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        library.SelectFolderCommand.Execute(Find(library, "Foto"));
        Assert.Equal(["a.png"], library.Photos.Select(p => p.FileName));
    }

    [Fact]
    public async Task OtherFiltersClearTheFolderSelection_AndExpansionSurvivesReload()
    {
        var library = await ImportNestedAsync();
        library.SelectFolderCommand.Execute(Find(library, "Rio"));
        library.SelectSidebarCommand.Execute(library.SmartLists.Single(e => e.Key == "all"));
        Assert.Equal(5, library.Photos.Count);
        Assert.All(library.Folders.SelectMany(r => r.SelfAndDescendants()), n => Assert.False(n.IsSelected));

        Find(library, "Viagens").IsExpanded = false;      // o usuário recolhe um ramo
        await library.ApplyFiltersAsync();                // recarrega do banco: a árvore é reconstruída
        Assert.False(Find(library, "Viagens").IsExpanded);
        Assert.True(library.Folders[0].IsExpanded);
    }

    [Fact]
    public async Task MovingPhotosToANewFolder_UpdatesTheTree()
    {
        var library = await ImportNestedAsync();
        await library.MoveSelectedAsync([library.Photos.First(p => p.FileName == "e.png")], Path.Combine(_env.Photos, "Novas"));
        Assert.Contains(library.Folders[0].Children, n => n.Label == "Novas" && n.Count == 1);
        Assert.DoesNotContain(library.Folders[0].Children, n => n.Label == "Cidades");
    }
}
