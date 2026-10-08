using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>Comportamento novo da Biblioteca após a refatoração visual: sidebar, filtros em memória, favorito no cartão e navegação no preview.</summary>
public sealed class LibraryViewTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private async Task<LibraryViewModel> LibraryWithDataAsync()
    {
        _env.CreatePng("Viagens/a.png"); _env.CreatePng("Viagens/b.png"); _env.CreatePng("Cidades/c.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var a = library.Photos.First(p => p.FileName == "a.png");
        library.SelectedPhoto = a; a.Category = "Viagens"; a.TagsText = "praia"; a.IsFavorite = true;
        var c = library.Photos.First(p => p.FileName == "c.png");
        library.SelectedPhoto = c; c.Category = "Cidades"; c.TagsText = "praia, noite";
        await library.SaveSelectedAsync();
        var colTop = await _env.Collections.CreateAsync("Top", null);
        await library.AddPhotoToCollectionAsync(c, colTop.Id);
        return library;
    }

    [Fact]
    public async Task Sidebar_ShowsCountsForSmartListsFoldersCategoriesTagsAndCollections()
    {
        var library = await LibraryWithDataAsync();
        int Count(IEnumerable<SidebarEntry> entries, string key) => entries.Single(e => e.Key == key).Count;
        Assert.Equal(3, Count(library.SmartLists, "all"));
        Assert.Equal(1, Count(library.SmartLists, "favorites"));
        Assert.Equal(1, Count(library.SmartLists, "uncategorized"));
        Assert.Equal(3, Count(library.SmartLists, "recent"));
        var root = Assert.Single(library.Folders);
        Assert.Equal(3, root.Count); Assert.Equal(["Cidades", "Viagens"], root.Children.Select(c => c.Label));
        Assert.Equal(["Cidades", "Viagens"], library.CategoryEntries.Select(e => e.Key));
        Assert.Equal(2, Count(library.TagEntries, "praia"));
        Assert.Equal(1, Count(library.CollectionEntries, "Top"));
        Assert.Equal(2, Count(library.CollectionEntries, LibraryViewModel.NoCollectionKey));
    }

    [Fact]
    public async Task SelectingSidebarEntries_FiltersInMemory_AndMarksSelection()
    {
        var library = await LibraryWithDataAsync();
        library.SelectSidebarCommand.Execute(library.TagEntries.Single(e => e.Key == "praia"));
        Assert.Equal(2, library.Photos.Count);
        Assert.True(library.TagEntries.Single(e => e.Key == "praia").IsSelected);
        Assert.True(library.HasActiveFilters);

        // Os filtros definidos pelo usuário (aqui, a tag) são mantidos ao escolher outro item da barra lateral.
        library.SelectSidebarCommand.Execute(library.SmartLists.Single(e => e.Key == "favorites"));
        Assert.Equal(["a.png"], library.Photos.Select(p => p.FileName));
        Assert.True(library.FavoritesChoice);
        Assert.True(library.TagEntries.Single(e => e.Key == "praia").IsSelected);

        library.FavoritesChoice = false;
        library.SelectFolderCommand.Execute(library.Folders[0].Children.Single(n => n.Label == "Cidades"));
        Assert.Equal(["c.png"], library.Photos.Select(p => p.FileName));            // pasta + tag
        Assert.Equal("praia", library.TagChoice);                                   // mudar de pasta não limpou a tag

        library.SelectSidebarCommand.Execute(library.SmartLists.Single(e => e.Key == "uncategorized"));
        Assert.Empty(library.Photos);                                               // sem categoria + tag praia (a e c têm categoria)
        Assert.Equal("praia", library.TagChoice);

        // "Todas as fotos" volta ao início: limpa tudo.
        library.SelectSidebarCommand.Execute(library.SmartLists.Single(e => e.Key == "all"));
        Assert.Equal(3, library.Photos.Count);
        Assert.False(library.HasActiveFilters);

        library.SelectSidebarCommand.Execute(library.TagEntries.Single(e => e.Key == "praia"));
        library.ClearFiltersCommand.Execute(null);
        Assert.Equal(3, library.Photos.Count);
        Assert.False(library.HasActiveFilters);
    }

    [Fact]
    public async Task ChangingFolder_KeepsTheFilterBarFilters_ButClearFiltersResetsThem()
    {
        _env.CreatePng("A/land.png", width: 80, height: 40); _env.CreatePng("A/port.png", width: 40, height: 80);
        _env.CreatePng("B/land2.png", width: 80, height: 40); _env.CreatePng("B/port2.png", width: 40, height: 80);
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        library.OrientationChoice = "Retrato";
        library.MinimumRatingChoice = 0;
        library.SearchText = "port";

        library.SelectFolderCommand.Execute(library.Folders[0].Children.Single(n => n.Label == "A"));
        Assert.Equal(["port.png"], library.Photos.Select(p => p.FileName));
        Assert.Equal("Retrato", library.OrientationChoice);

        library.SelectFolderCommand.Execute(library.Folders[0].Children.Single(n => n.Label == "B"));
        Assert.Equal(["port2.png"], library.Photos.Select(p => p.FileName));        // continua filtrando por retrato + busca
        Assert.Equal("Retrato", library.OrientationChoice);
        Assert.Equal("port", library.SearchText);

        library.ClearFiltersCommand.Execute(null);
        Assert.Equal("Qualquer", library.OrientationChoice);
        Assert.Equal(string.Empty, library.SearchText);
    }

    [Fact]
    public async Task NoCollection_IsAVirtualFilter_AndUpdatesWhenPhotoJoinsACollection()
    {
        var library = await LibraryWithDataAsync();
        var noCollection = library.CollectionEntries.Single(entry => entry.Key == LibraryViewModel.NoCollectionKey);
        library.SelectSidebarCommand.Execute(noCollection);
        Assert.Equal(["a.png", "b.png"], library.Photos.Select(photo => photo.FileName));

        var selected = library.Photos.First(photo => photo.FileName == "b.png");
        library.SelectedPhoto = selected;
        var colFav = await _env.Collections.CreateAsync("Favoritas", null);
        await library.AddPhotoToCollectionAsync(selected, colFav.Id);

        Assert.Single(library.Photos);
        Assert.Equal("a.png", library.Photos[0].FileName);
        Assert.Equal(1, library.CollectionEntries.Single(entry => entry.Key == LibraryViewModel.NoCollectionKey).Count);
    }

    [Fact]
    public async Task FilterChoices_RefilterWithoutReloadingTheCatalog()
    {
        var library = await LibraryWithDataAsync();
        library.CategoryChoice = "Cidades";
        Assert.Equal(["c.png"], library.Photos.Select(p => p.FileName));
        library.CategoryChoice = LibraryViewModel.AllChoice;
        library.TagChoice = "noite";
        Assert.Equal(["c.png"], library.Photos.Select(p => p.FileName));
        library.TagChoice = LibraryViewModel.AllChoice;
        library.MinimumRatingChoice = 1;
        Assert.Empty(library.Photos);
        library.CategoryChoice = null!; // um ComboBox reconstruído envia null: deve ser ignorado, não limpar o filtro
        Assert.Equal(1, library.MinimumRatingChoice);
    }

    [Fact]
    public async Task ToggleFavorite_PersistsWithoutExplicitSave()
    {
        var library = await LibraryWithDataAsync();
        var b = library.Photos.First(p => p.FileName == "b.png");
        await library.ToggleFavoriteAsync(b);
        Assert.Equal(2, library.SmartLists.Single(e => e.Key == "favorites").Count);

        var reloaded = _env.CreateLibrary();
        await TestEnvironment.WaitUntilAsync(() => reloaded.Photos.Count == 3);
        Assert.True(reloaded.Photos.First(p => p.FileName == "b.png").IsFavorite);
    }

    [Fact]
    public async Task PreviousNext_WalkTheVisibleList_AndPreviewLoads()
    {
        var library = await LibraryWithDataAsync();
        library.SelectedPhoto = library.Photos[0];
        Assert.False(library.PreviousCommand.CanExecute(null));
        Assert.Equal("1 de 3", library.PositionText);
        library.NextCommand.Execute(null);
        Assert.Same(library.Photos[1], library.SelectedPhoto);
        library.NextCommand.Execute(null);
        Assert.False(library.NextCommand.CanExecute(null));
        library.PreviousCommand.Execute(null);
        Assert.Equal("2 de 3", library.PositionText);
        await TestEnvironment.WaitUntilAsync(() => library.PreviewImage is not null);
        Assert.True(library.PreviewImage!.IsFrozen);
    }

    [Fact]
    public async Task Refilter_KeepsSelectionWhenPhotoStillVisible_AndClearsItOtherwise()
    {
        var library = await LibraryWithDataAsync();
        library.SelectedPhoto = library.Photos.First(p => p.FileName == "c.png");
        library.CategoryChoice = "Cidades";
        Assert.Equal("c.png", library.SelectedPhoto?.FileName);
        library.CategoryChoice = "Viagens";
        Assert.Null(library.SelectedPhoto);
    }

    [Fact]
    public async Task Thumbnails_AreFilledInTheBackground()
    {
        var library = await LibraryWithDataAsync();
        await TestEnvironment.WaitUntilAsync(() => library.Photos.All(p => p.ThumbnailUri is not null));
        Assert.All(library.Photos, p => Assert.True(File.Exists(p.ThumbnailUri!.LocalPath)));
    }
}
