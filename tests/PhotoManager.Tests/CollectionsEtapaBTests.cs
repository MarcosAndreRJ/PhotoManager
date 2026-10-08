using System.Windows;
using System.Windows.Threading;
using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>
/// Testes da Etapa B: itens virtuais "Todas" e "Sem coleção", contadores exatos e correção do combo de coleção.
/// </summary>
[Collection("WpfUi")]
public sealed class CollectionsEtapaBTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private async Task<(LibraryViewModel Library, Photo Photo1, Photo Photo2, Photo Photo3, Photo Photo4)> SetupPhotosAsync()
    {
        _env.CreatePng("Viagens/p1.png");
        _env.CreatePng("Viagens/p2.png");
        _env.CreatePng("Cidades/p3.png");
        _env.CreatePng("Cidades/p4.png");

        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        // p1 está em duas coleções: "Favoritas" e "Top"
        var card1 = library.Photos.First(p => p.FileName == "p1.png");
        library.SelectedPhoto = card1;
        var colFav = await _env.Collections.CreateAsync("Favoritas", null);
        var colTop = await _env.Collections.CreateAsync("Top", null);
        var colCid = await _env.Collections.CreateAsync("Cidades2026", null);
        await library.AddPhotoToCollectionAsync(card1, colFav.Id);
        await library.AddPhotoToCollectionAsync(card1, colTop.Id);

        // p2 está em uma coleção: "Top"
        var card2 = library.Photos.First(p => p.FileName == "p2.png");
        library.SelectedPhoto = card2;
        await library.AddPhotoToCollectionAsync(card2, colTop.Id);

        // p3 está em "Cidades2026"
        var card3 = library.Photos.First(p => p.FileName == "p3.png");
        library.SelectedPhoto = card3;
        await library.AddPhotoToCollectionAsync(card3, colCid.Id);

        // p4 não tem nenhuma coleção
        var card4 = library.Photos.First(p => p.FileName == "p4.png");

        return (library, card1.Photo, card2.Photo, card3.Photo, card4.Photo);
    }

    [Fact]
    public async Task Todas_ReturnsPhotosInAtLeastOneCollection_WithoutDuplicates()
    {
        var (library, p1, p2, p3, _) = await SetupPhotosAsync();

        var todasEntry = library.CollectionEntries.Single(e => e.Key == LibraryViewModel.AllCollectionsKey);
        Assert.Equal("Todas", todasEntry.Label);
        Assert.True(todasEntry.IsVirtual);
        Assert.Equal("Fotos que pertencem a pelo menos uma coleção", todasEntry.ToolTip);

        library.SelectSidebarCommand.Execute(todasEntry);

        // p1 está em 2 coleções ("Favoritas" e "Top"), mas deve aparecer apenas UMA vez em Todas
        var visibleNames = library.Photos.Select(p => p.FileName).OrderBy(n => n).ToList();
        Assert.Equal(["p1.png", "p2.png", "p3.png"], visibleNames);
        Assert.Equal(3, todasEntry.Count);
        Assert.True(todasEntry.IsSelected);
        Assert.True(library.HasActiveFilters);
    }

    [Fact]
    public async Task Todas_IsNotTheWholeLibrary()
    {
        var (library, _, _, _, p4) = await SetupPhotosAsync();

        var todasEntry = library.CollectionEntries.Single(e => e.Key == LibraryViewModel.AllCollectionsKey);
        library.SelectSidebarCommand.Execute(todasEntry);

        Assert.Equal(3, library.Photos.Count);
        Assert.DoesNotContain(library.Photos, p => p.FileName == p4.FileName);
    }

    [Fact]
    public async Task SemColecao_ReturnsPhotosWithoutAnyAssociation_AndCreatesNoPhysicalCollection()
    {
        var (library, _, _, _, p4) = await SetupPhotosAsync();

        var semColecaoEntry = library.CollectionEntries.Single(e => e.Key == LibraryViewModel.NoCollectionKey);
        Assert.Equal("Sem coleção", semColecaoEntry.Label);
        Assert.True(semColecaoEntry.IsVirtual);
        Assert.True(semColecaoEntry.HasSeparatorAfter);

        library.SelectSidebarCommand.Execute(semColecaoEntry);

        var visible = Assert.Single(library.Photos);
        Assert.Equal(p4.FileName, visible.FileName);
        Assert.Equal(1, semColecaoEntry.Count);
        Assert.True(semColecaoEntry.IsSelected);

        // Verifica que o banco não contém registros físicos para itens virtuais
        var physicalCollections = await _env.Organization.GetCollectionsAsync();
        Assert.DoesNotContain("Sem coleção", physicalCollections, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(LibraryViewModel.NoCollectionKey, physicalCollections, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(LibraryViewModel.AllCollectionsKey, physicalCollections, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Todas", physicalCollections, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Counters_AreDirectOnly_AndTodasPlusSemColecaoEqualsTotal()
    {
        var (library, _, _, _, _) = await SetupPhotosAsync();

        // Forçar uma foto como ausente (missing) para testar se contadores cobrem todo o acervo do catálogo
        var card4 = library.Photos.First(p => p.FileName == "p4.png");
        card4.Photo.IsMissing = true;

        var todas = library.CollectionEntries.Single(e => e.Key == LibraryViewModel.AllCollectionsKey);
        var semColecao = library.CollectionEntries.Single(e => e.Key == LibraryViewModel.NoCollectionKey);
        var top = library.CollectionEntries.Single(e => e.Key == "Top");
        var favoritas = library.CollectionEntries.Single(e => e.Key == "Favoritas");
        var cidades = library.CollectionEntries.Single(e => e.Key == "Cidades2026");

        // Contagens diretas
        Assert.Equal(2, top.Count);       // p1 e p2
        Assert.Equal(1, favoritas.Count); // p1
        Assert.Equal(1, cidades.Count);   // p3

        // Invariante essencial: Todas + Sem coleção == Total de fotos do catálogo
        Assert.Equal(3, todas.Count);
        Assert.Equal(1, semColecao.Count);
        Assert.Equal(4, todas.Count + semColecao.Count);
    }

    [Fact]
    public async Task Counters_UpdateAfterSaveBatchAndDuplicateMerge()
    {
        var (library, _, _, _, p4) = await SetupPhotosAsync();

        Assert.Equal(3, library.CollectionEntries.Single(e => e.Key == LibraryViewModel.AllCollectionsKey).Count);
        Assert.Equal(1, library.CollectionEntries.Single(e => e.Key == LibraryViewModel.NoCollectionKey).Count);

        // 1. Salvar uma coleção para p4 (que não tinha)
        var card4 = library.Photos.First(p => p.FileName == p4.FileName);
        library.SelectedPhoto = card4;
        var novaCol = await _env.Collections.CreateAsync("NovaColecao", null);
        await library.AddPhotoToCollectionAsync(card4, novaCol.Id);

        Assert.Equal(4, library.CollectionEntries.Single(e => e.Key == LibraryViewModel.AllCollectionsKey).Count);
        Assert.Equal(0, library.CollectionEntries.Single(e => e.Key == LibraryViewModel.NoCollectionKey).Count);
        Assert.Contains(library.CollectionEntries, e => e.Key == "NovaColecao" && e.Count == 1);

        // 2. Aplicar lote em duas fotos adicionando outra coleção
        var loteCol = await _env.Collections.CreateAsync("LoteExtra", null);
        var plan = new OrganizationBatch { CollectionsToAdd = [loteCol.Id] };
        await library.ApplyOrganizationBatchAsync([library.Photos[0], library.Photos[1]], plan);

        Assert.Contains(library.CollectionEntries, e => e.Key == "LoteExtra" && e.Count == 2);
        Assert.Equal(4, library.CollectionEntries.Single(e => e.Key == LibraryViewModel.AllCollectionsKey).Count);
        Assert.Equal(0, library.CollectionEntries.Single(e => e.Key == LibraryViewModel.NoCollectionKey).Count);
    }

    [Fact]
    public async Task CollectionCombo_DoesNotExposeInternalKeys()
    {
        var (library, _, _, _, _) = await SetupPhotosAsync();

        // O primeiro item deve ser "Qualquer" e não "Todas" nem as chaves internas
        Assert.Equal(LibraryViewModel.AnyCollectionChoice, library.CollectionChoices[0]);
        Assert.Equal("Qualquer", library.CollectionChoices[0]);

        Assert.DoesNotContain(LibraryViewModel.NoCollectionKey, library.CollectionChoices);
        Assert.DoesNotContain(LibraryViewModel.AllCollectionsKey, library.CollectionChoices);
        Assert.DoesNotContain("Todas", library.CollectionChoices);
        Assert.DoesNotContain("Sem coleção", library.CollectionChoices);

        // Apenas as coleções reais aparecem ordenadas
        var realChoices = library.CollectionChoices.Skip(1).ToList();
        Assert.Equal(["Cidades2026", "Favoritas", "Top"], realChoices);

        // Selecionar no combo refiltra
        library.CollectionChoice = "Favoritas";
        Assert.Single(library.Photos);
        Assert.Equal("p1.png", library.Photos[0].FileName);

        // Voltar para "Qualquer" limpa o filtro de coleção
        library.CollectionChoice = LibraryViewModel.AnyCollectionChoice;
        Assert.Equal(4, library.Photos.Count);
    }

    [Fact]
    public async Task VirtualEntries_AreFlaggedVirtual_AndMutuallyExclusiveWithRealCollections()
    {
        var (library, _, _, _, _) = await SetupPhotosAsync();

        var todas = library.CollectionEntries.Single(e => e.Key == LibraryViewModel.AllCollectionsKey);
        var semColecao = library.CollectionEntries.Single(e => e.Key == LibraryViewModel.NoCollectionKey);
        var top = library.CollectionEntries.Single(e => e.Key == "Top");

        Assert.True(todas.IsVirtual);
        Assert.True(semColecao.IsVirtual);
        Assert.False(top.IsVirtual);

        // Selecionar Todas
        library.SelectSidebarCommand.Execute(todas);
        Assert.True(todas.IsSelected);
        Assert.False(semColecao.IsSelected);
        Assert.False(top.IsSelected);

        // Selecionar Sem coleção
        library.SelectSidebarCommand.Execute(semColecao);
        Assert.False(todas.IsSelected);
        Assert.True(semColecao.IsSelected);
        Assert.False(top.IsSelected);

        // Selecionar Top
        library.SelectSidebarCommand.Execute(top);
        Assert.False(todas.IsSelected);
        Assert.False(semColecao.IsSelected);
        Assert.True(top.IsSelected);
    }

    [Fact]
    public void RealWindow_CollectionsSidebar_ShowsVirtualItemsAndFiltersGrid()
    {
        RunSta(() =>
        {
            var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (app.Resources.MergedDictionaries.Count == 0)
                foreach (var name in new[] { "Colors", "Typography", "Buttons", "Inputs", "Cards", "Tabs", "Tree" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/PhotoManager;component/Themes/{name}.xaml") });

            var library = _env.CreateLibrary();
            _env.CreatePng("a.png");
            _env.CreatePng("b.png");
            var task = library.ImportFolderAsync(_env.Photos);
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false);
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);

            var a = library.Photos.First(p => p.FileName == "a.png");
            library.SelectedPhoto = a;
            var colTopTask = _env.Collections.CreateAsync("Top", null);
            var frame2 = new DispatcherFrame();
            colTopTask.ContinueWith(_ => frame2.Continue = false);
            if (!colTopTask.IsCompleted) Dispatcher.PushFrame(frame2);
            var colTop = colTopTask.GetAwaiter().GetResult();

            var addTask = library.AddPhotoToCollectionAsync(a, colTop.Id);
            var frame3 = new DispatcherFrame();
            addTask.ContinueWith(_ => frame3.Continue = false);
            if (!addTask.IsCompleted) Dispatcher.PushFrame(frame3);

            var view = new LibraryView { DataContext = library };
            var window = new Window { Width = 1100, Height = 700, Content = view };
            try
            {
                window.Show();
                window.UpdateLayout();

                // Verifica itens na coleção
                var todas = library.CollectionEntries.First(e => e.Key == LibraryViewModel.AllCollectionsKey);
                var semColecao = library.CollectionEntries.First(e => e.Key == LibraryViewModel.NoCollectionKey);
                Assert.Equal(1, todas.Count);
                Assert.Equal(1, semColecao.Count);

                // Clica em Todas
                library.SelectSidebarCommand.Execute(todas);
                window.UpdateLayout();
                Assert.Single(library.Photos);
                Assert.Equal("a.png", library.Photos[0].FileName);

                // Clica em Sem coleção
                library.SelectSidebarCommand.Execute(semColecao);
                window.UpdateLayout();
                Assert.Single(library.Photos);
                Assert.Equal("b.png", library.Photos[0].FileName);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception ex) { failure = ex; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }
}
