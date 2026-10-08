using System.IO;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Metadata;
using PhotoManager.Domain.Photos;
using PhotoManager.Infrastructure.Media;
using PhotoManager.Wpf.ViewModels;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

public sealed class MissingAndTreeTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private static Photo P(long id, string path) => new() { Id = id, FileName = Path.GetFileName(path), CurrentPath = path, Extension = Path.GetExtension(path), IsMissing = true };

    // ---------- relatório de não encontrados ----------

    [Fact]
    public void MissingReport_MergesVanishedSubfoldersIntoTheHighestMissingFolder_AndKeepsExistingFoldersApart()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\", @"D:\Fotos", @"D:\Fotos\Vivo" };
        Photo[] missing =
        [
            P(1, @"D:\Fotos\Sumiu\a.jpg"), P(2, @"D:\Fotos\Sumiu\b.jpg"), P(3, @"D:\Fotos\Sumiu\Sub\c.jpg"), P(4, @"D:\Fotos\Sumiu\Sub\Fundo\d.jpg"),
            P(5, @"D:\Fotos\Vivo\e.jpg"),
            P(6, @"E:\Disco\f.jpg")                                   // E:\ nem existe: o item é o próprio disco
        ];

        var report = MissingReport.Build(missing, existing.Contains);

        Assert.Equal(3, report.Count);
        var gone = report.Single(e => e.Folder == @"D:\Fotos\Sumiu");
        Assert.False(gone.FolderExists);
        Assert.Equal(4, gone.FileCount);
        Assert.Equal(2, gone.SubfolderCount);
        Assert.Contains("Pasta não encontrada", gone.Title);

        var alive = report.Single(e => e.Folder == @"D:\Fotos\Vivo");
        Assert.True(alive.FolderExists);
        Assert.Equal([5L], alive.PhotoIds);
        Assert.Contains("ausente(s) nesta pasta", alive.Detail);

        Assert.False(report.Single(e => e.Folder.StartsWith("E:")).FolderExists);
        Assert.Equal(report.Select(e => e.Folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase), report.Select(e => e.Folder));
    }

    [Fact]
    public void MissingReport_IsEmptyWhenNothingIsMissing() =>
        Assert.Empty(MissingReport.Build([], _ => true));

    // ---------- remover do catálogo ----------

    [Fact]
    public async Task RemoveMissing_DeletesOnlyRowsWhoseFileIsGone_WithTheirTagsCollectionsAndHistory_AndNeverFiles()
    {
        var gonePath = _env.CreatePng("g1.png");
        var keepPath = _env.CreatePng("k1.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = (await _env.Catalog.GetPhotosAsync()).ToList();
        var gone = photos.Single(p => p.CurrentPath == gonePath);
        var keep = photos.Single(p => p.CurrentPath == keepPath);
        var collection = await _env.Collections.CreateAsync("Col", null);
        await _env.Collections.AddPhotosAsync(collection.Id, [gone.Id, keep.Id]);
        await _env.MetadataEditing.SaveAsync(gone, new MetadataEdit { Title = "t" });          // cria histórico (sidecar)
        await DeleteWhenFreeAsync(gonePath);

        // Segurança: pedir a remoção de um arquivo que EXISTE não remove nada dele.
        var removed = await _env.Catalog.RemoveMissingAsync([gone, keep]);

        Assert.Equal([gone.Id], removed);
        var after = (await _env.Catalog.GetPhotosAsync()).ToList();
        Assert.Equal([keep.Id], after.Select(p => p.Id));
        Assert.True(File.Exists(keepPath));
        Assert.Equal([keep.Id], await ((ICollectionRepositoryProbe)new ICollectionRepositoryProbe(_env)).PhotoIdsInCollectionAsync(collection.Id));
        Assert.Empty(await _env.MetadataEditing.GetHistoryAsync(gone.Id));
        Assert.False((await _env.Collections.HasOrphansAsync()));
    }

    [Fact]
    public async Task LibraryMenu_RemoveFromCatalog_AsksFirst_AndOnlyTouchesMissingItems()
    {
        var gonePath = _env.CreatePng("m1.png");
        var keepPath = _env.CreatePng("m2.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        await DeleteWhenFreeAsync(gonePath);
        library.ShowMissingDialog = _ => { };                     // sem janela real nos testes
        await library.RefreshFolderAsync(null);                   // marca o ausente
        var goneCard = library.Photos.Single(c => c.IsMissing);
        var keepCard = library.Photos.Single(c => !c.IsMissing);
        library.UpdateSelection([goneCard, keepCard]);

        var asked = new List<int>();
        library.ConfirmRemoveFromCatalog = n => { asked.Add(n); return false; };
        await library.RemoveFromCatalogMenuAsync(goneCard);
        Assert.Equal([1], asked);                                  // o existente da seleção não entra na conta
        Assert.Equal(2, library.Photos.Count);                     // recusou: nada mudou

        library.ConfirmRemoveFromCatalog = _ => true;
        await library.RemoveFromCatalogMenuAsync(goneCard);
        Assert.Equal([keepCard.Photo.Id], library.Photos.Select(c => c.Photo.Id));
        Assert.True(File.Exists(keepPath));

        await library.RemoveFromCatalogMenuAsync(keepCard);        // item existente: não pode ser removido por este caminho
        Assert.Single(library.Photos);
        Assert.Contains("Só itens com arquivo ausente", library.StatusText);
    }

    [Fact]
    public async Task MissingDialogViewModel_RemovesPerItem_AndAllWithConfirmation()
    {
        MissingEntry[] entries =
        [
            new(@"D:\A", false, 2, 0, [1, 2]),
            new(@"D:\B", true, 1, 0, [3]),
            new(@"D:\C", false, 3, 1, [4, 5, 6])
        ];
        var removedCalls = new List<IReadOnlyList<long>>();
        var confirm = false;
        var vm = new MissingItemsViewModel(entries, ids => { removedCalls.Add(ids); return Task.FromResult(ids.Count); }, _ => confirm);
        Assert.Equal(3, vm.Entries.Count);
        Assert.Equal(6, vm.TotalFiles);

        await vm.RemoveAsync(vm.Entries[1]);
        Assert.Equal([[3L]], removedCalls);
        Assert.Equal(2, vm.Entries.Count);

        await vm.RemoveAllAsync();                                 // recusou a confirmação
        Assert.Single(removedCalls);
        Assert.Equal(2, vm.Entries.Count);

        confirm = true;
        await vm.RemoveAllAsync();
        Assert.Equal(2, removedCalls.Count);
        Assert.Equal([1L, 2L, 4L, 5L, 6L], removedCalls[1].OrderBy(x => x));
        Assert.Empty(vm.Entries);
        Assert.False(vm.HasEntries);
    }

    [Fact]
    public async Task MissingDialogViewModel_ReportsFailuresInsteadOfThrowing()
    {
        var vm = new MissingItemsViewModel([new(@"D:\A", false, 1, 0, [1])], _ => throw new IOException("banco ocupado"), _ => true);
        await vm.RemoveAsync(vm.Entries[0]);
        Assert.Single(vm.Entries);
        Assert.Contains("banco ocupado", vm.Status);
    }

    // ---------- pastas inacessíveis ----------

    private static async Task DeleteWhenFreeAsync(string path)
    {
        for (var attempt = 0; ; attempt++)   // a geração de miniaturas pode estar lendo o arquivo por alguns ms
        {
            try { File.Delete(path); return; }
            catch (IOException) when (attempt < 30) { await Task.Delay(100); }
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(50);
    }

    [Fact]
    public async Task Tree_MarksUnreachableFoldersAndHidesTheirContents_AndRestoresWhenTheyReturn()
    {
        _env.CreatePng("Raiz/Sub1/a.png");
        _env.CreatePng("Raiz/Sub2/b.png");
        _env.CreatePng("Outra/c.png");
        var library = _env.CreateLibrary();
        var offline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        library.DirectoryExists = path => !offline.Any(o => path.StartsWith(o, StringComparison.OrdinalIgnoreCase)) && Directory.Exists(path);
        await library.ImportFolderAsync(_env.Photos);

        var root = library.Folders.Single();
        await WaitAsync(() => library.Folders.SelectMany(r => r.SelfAndDescendants()).All(n => n.IsAccessible));
        Assert.True(root.SelfAndDescendants().All(n => n.IsAccessible));
        var raiz = root.SelfAndDescendants().Single(n => n.Label == "Raiz");
        Assert.NotEmpty(raiz.DisplayChildren);

        offline.Add(Path.Combine(_env.Photos, "Raiz"));
        await library.RefreshFolderAsync(null);
        var raizAgain = library.Folders.SelectMany(r => r.SelfAndDescendants()).Single(n => n.Label == "Raiz");
        await WaitAsync(() => !raizAgain.IsAccessible);

        Assert.False(raizAgain.IsAccessible);
        Assert.Empty(raizAgain.DisplayChildren);                                  // o conteúdo não é listado
        Assert.NotEmpty(raizAgain.Children);                                      // mas continua no modelo (volta quando a pasta volta)
        Assert.Contains("inacessível", raizAgain.StateToolTip);
        Assert.True(library.Folders.SelectMany(r => r.SelfAndDescendants()).Single(n => n.Label == "Outra").IsAccessible);   // as outras não são afetadas

        offline.Clear();
        library.ShowMissingDialog = _ => { };
        await library.RefreshFolderAsync(null);
        var back = library.Folders.SelectMany(r => r.SelfAndDescendants()).Single(n => n.Label == "Raiz");
        await WaitAsync(() => back.IsAccessible);
        Assert.True(back.IsAccessible);
        Assert.NotEmpty(back.DisplayChildren);
    }

    [Fact]
    public async Task ExpandAllAndCollapseAll_ActOnTheWholeSubtree_ForFoldersAndCollections()
    {
        _env.CreatePng("A/B/C/x.png");
        var pai = await _env.Collections.CreateAsync("Pai", null);
        var filho = await _env.Collections.CreateAsync("Filho", pai.Id);
        await _env.Collections.CreateAsync("Neto", filho.Id);
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        var folderRoot = library.Folders.Single();
        library.CollapseAllCommand.Execute(folderRoot);
        Assert.All(folderRoot.SelfAndDescendants(), n => Assert.False(n.IsExpanded));
        library.ExpandAllCommand.Execute(folderRoot);
        Assert.All(folderRoot.SelfAndDescendants(), n => Assert.True(n.IsExpanded));

        var paiNode = library.CollectionNodes.Single(n => n.Id == pai.Id);
        library.ExpandAllCommand.Execute(paiNode);
        Assert.All(paiNode.SelfAndDescendants(), n => Assert.True(n.IsExpanded));
        library.CollapseAllCommand.Execute(paiNode);
        Assert.All(paiNode.SelfAndDescendants(), n => Assert.False(n.IsExpanded));

        library.ExpandAllCommand.Execute(paiNode.AggregateNode);                  // o item virtual "Todas" ignora
        Assert.False(paiNode.IsExpanded);
    }

    // ---------- vídeo em retrato (rotação do celular) ----------

    [Fact]
    public void Mp4Info_AppliesThePhoneRotationMatrix()
    {
        var path = Path.Combine(_env.Photos, "retrato.mp4");
        File.WriteAllBytes(path, FakeMp4.Build(1920, 1080, 10, rotate90: true));
        var info = Mp4Info.TryRead(path)!;
        Assert.Equal(1080, info.Width);
        Assert.Equal(1920, info.Height);

        var normal = Path.Combine(_env.Photos, "paisagem.mp4");
        File.WriteAllBytes(normal, FakeMp4.Build(1920, 1080, 10));
        var info2 = Mp4Info.TryRead(normal)!;
        Assert.Equal((1920, 1080), (info2.Width, info2.Height));
    }

    [Fact]
    public async Task OldVideoEntries_AreCorrectedInTheBackground_AndShowAsPortrait()
    {
        var path = Path.GetFullPath(Path.Combine(_env.Photos, "celular.mp4"));
        File.WriteAllBytes(path, FakeMp4.Build(1920, 1080, 10, rotate90: true));
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photo = (await _env.Catalog.GetPhotosAsync()).Single();
        Assert.Equal(1080, photo.Width);                                          // importação nova já lê certo

        // Simula o catálogo de uma versão antiga: dimensões deitadas e revisão 0.
        photo.Width = 1920; photo.Height = 1080; photo.MediaInfoRevision = 0;
        await _env.Repository.UpdateMediaInfoAsync(photo);
        Assert.Equal(PhotoOrientation.Landscape, (await _env.Catalog.GetPhotosAsync()).Single().Orientation);

        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);                              // recarrega; dispara a correção em segundo plano
        await WaitAsync(() => library.Photos.Single().Photo.Orientation == PhotoOrientation.Portrait);

        var card = library.Photos.Single();
        Assert.Equal(PhotoOrientation.Portrait, card.Photo.Orientation);
        Assert.True(card.IsPortraitOrSquare);
        Assert.Equal("Retrato", card.OrientationText);
        Photo stored = null!;
        for (var i = 0; i < 100; i++)                                                // a gravação no banco termina logo depois da atualização em memória
        {
            stored = (await _env.Catalog.GetPhotosAsync()).Single();
            if (stored.MediaInfoRevision == PhotoManager.Application.Catalog.CatalogService.CurrentMediaInfoRevision) break;
            await Task.Delay(50);
        }
        Assert.Equal((1080, 1920, PhotoManager.Application.Catalog.CatalogService.CurrentMediaInfoRevision), (stored.Width, stored.Height, stored.MediaInfoRevision));   // gravado no banco: não repete
    }
}

/// <summary>Pequeno acesso ao repositório para conferir as relações de coleção.</summary>
internal sealed class ICollectionRepositoryProbe(TestEnvironment env)
{
    public async Task<IReadOnlyList<long>> PhotoIdsInCollectionAsync(long collectionId) =>
        (await env.Catalog.GetPhotosAsync()).Where(p => p.CollectionIds.Contains(collectionId)).Select(p => p.Id).ToList();
}
