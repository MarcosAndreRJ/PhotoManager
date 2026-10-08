using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Domain.Collections;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.ViewModels;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>
/// Testes abrangentes da Etapa C2:
/// - Construção e contagem da árvore (CollectionNode)
/// - Seleção na árvore (fotos diretas)
/// - Operações de CRUD via LibraryViewModel (criar, subcoleção, renomear, mover, excluir nos dois modos)
/// - Chips e seletores estruturados no painel de organização (única e lote)
/// - Validações dos ViewModels de diálogo e renderização WPF UI
/// </summary>
[Collection("WpfUi")]
public sealed class CollectionsEtapaC2Tests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

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

    [Fact]
    public void CollectionNode_Build_CreatesCorrectHierarchyAndAlphabeticalOrder()
    {
        // Raízes: ZOO, Animais, Brasil
        // Filhos de Animais: Cães, Aves, Gatos
        // Filhos de Cães: Pastor, Beagle
        var items = new List<Collection>
        {
            new() { Id = 1, Name = "ZOO", ParentCollectionId = null, SortOrder = 0 },
            new() { Id = 2, Name = "Animais", ParentCollectionId = null, SortOrder = 0 },
            new() { Id = 3, Name = "Brasil", ParentCollectionId = null, SortOrder = 0 },
            new() { Id = 4, Name = "Cães", ParentCollectionId = 2, SortOrder = 0 },
            new() { Id = 5, Name = "Aves", ParentCollectionId = 2, SortOrder = 0 },
            new() { Id = 6, Name = "Gatos", ParentCollectionId = 2, SortOrder = 0 },
            new() { Id = 7, Name = "Pastor", ParentCollectionId = 4, SortOrder = 0 },
            new() { Id = 8, Name = "Beagle", ParentCollectionId = 4, SortOrder = 0 }
        };

        var directCounts = new Dictionary<long, int>
        {
            [1] = 5,
            [2] = 2,
            [4] = 3,
            [7] = 4,
            [8] = 1
        };

        var roots = CollectionNode.Build(items, directCounts, new HashSet<long> { 2, 4 });

        // Ordem alfabética nas raízes: Animais, Brasil, ZOO
        Assert.Equal(3, roots.Count);
        Assert.Equal("Animais", roots[0].Name);
        Assert.Equal("Brasil", roots[1].Name);
        Assert.Equal("ZOO", roots[2].Name);

        // Filhos de Animais em ordem: Aves, Cães, Gatos
        var animais = roots[0];
        Assert.True(animais.IsExpanded);
        Assert.Equal(3, animais.Children.Count);
        Assert.Equal("Aves", animais.Children[0].Name);
        Assert.Equal("Cães", animais.Children[1].Name);
        Assert.Equal("Gatos", animais.Children[2].Name);

        // Filhos de Cães em ordem: Beagle, Pastor
        var caes = animais.Children[1];
        Assert.True(caes.IsExpanded);
        Assert.Equal(2, caes.Children.Count);
        Assert.Equal("Beagle", caes.Children[0].Name);
        Assert.Equal("Pastor", caes.Children[1].Name);
    }

    [Fact]
    public void CollectionNode_CalculatesDirectAndSubtreeCountsAccurately()
    {
        // Animais (2) -> Cães (3) -> Pastor (4)
        //                         -> Beagle (1)
        var items = new List<Collection>
        {
            new() { Id = 1, Name = "Animais", ParentCollectionId = null },
            new() { Id = 2, Name = "Cães", ParentCollectionId = 1 },
            new() { Id = 3, Name = "Pastor", ParentCollectionId = 2 },
            new() { Id = 4, Name = "Beagle", ParentCollectionId = 2 }
        };

        var directCounts = new Dictionary<long, int>
        {
            [1] = 2,
            [2] = 3,
            [3] = 4,
            [4] = 1
        };

        var roots = CollectionNode.Build(items, directCounts);
        var animais = roots.Single();
        var caes = animais.Children.Single();
        var pastor = caes.Children.First(c => c.Name == "Pastor");
        var beagle = caes.Children.First(c => c.Name == "Beagle");

        // Pastor: direto = 4, subárvore = 0 (folha)
        Assert.Equal(4, pastor.DirectCount);
        Assert.Equal(0, pastor.SubtreeCount);
        Assert.Equal("4 fotos diretas", pastor.ToolTipText);

        // Beagle: direto = 1, subárvore = 0 (folha)
        Assert.Equal(1, beagle.DirectCount);
        Assert.Equal(0, beagle.SubtreeCount);
        Assert.Equal("1 fotos diretas", beagle.ToolTipText);

        // Cães: direto = 3, subárvore = 4 + 1 = 5
        Assert.Equal(3, caes.DirectCount);
        Assert.Equal(5, caes.SubtreeCount);
        Assert.Equal("3 fotos diretas · 5 em subcoleções", caes.ToolTipText);

        // Animais: direto = 2, subárvore = 3 + 5 = 8
        Assert.Equal(2, animais.DirectCount);
        Assert.Equal(8, animais.SubtreeCount);
        Assert.Equal("2 fotos diretas · 8 em subcoleções", animais.ToolTipText);
    }

    [Fact]
    public void CollectionNode_PreservesExpandedStateAcrossRebuilds()
    {
        var items = new List<Collection>
        {
            new() { Id = 1, Name = "Viagens", ParentCollectionId = null },
            new() { Id = 2, Name = "2026", ParentCollectionId = 1 }
        };

        var expanded = new HashSet<long> { 1 };
        var roots1 = CollectionNode.Build(items, new Dictionary<long, int>(), expanded);
        Assert.True(roots1[0].IsExpanded);

        // Sem estar no hashset
        var roots2 = CollectionNode.Build(items, new Dictionary<long, int>(), new HashSet<long>());
        Assert.False(roots2[0].IsExpanded);
    }

    [Fact]
    public async Task Library_SelectingCollectionNode_FiltersDirectPhotosOnly()
    {
        _env.CreatePng("viagem_geral.png");
        _env.CreatePng("praia.png");
        _env.CreatePng("avulsa.png");

        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        var viagens = await _env.Collections.CreateAsync("Viagens", null);
        var praia = await _env.Collections.CreateAsync("Praia", viagens.Id);

        var cardGeral = library.Photos.First(p => p.FileName == "viagem_geral.png");
        var cardPraia = library.Photos.First(p => p.FileName == "praia.png");

        await library.AddPhotoToCollectionAsync(cardGeral, viagens.Id);
        await library.AddPhotoToCollectionAsync(cardPraia, praia.Id);

        // Selecionar o nó "Viagens" na árvore
        var nodeViagens = library.CollectionNodes.First(n => n.Id == viagens.Id);
        library.SelectCollectionNode(nodeViagens);

        // Deve mostrar apenas a foto direta "viagem_geral.png", não "praia.png" nem "avulsa.png"
        Assert.Single(library.Photos);
        Assert.Equal("viagem_geral.png", library.Photos[0].FileName);
        Assert.Equal(1, nodeViagens.DirectCount);
        Assert.Equal(1, nodeViagens.SubtreeCount);
        Assert.Equal("1 fotos diretas · 1 em subcoleções", nodeViagens.ToolTipText);

        // Selecionar a subcoleção "Praia"
        var nodePraia = nodeViagens.Children.First(c => c.Id == praia.Id);
        library.SelectCollectionNode(nodePraia);

        Assert.Single(library.Photos);
        Assert.Equal("praia.png", library.Photos[0].FileName);

        // Selecionar "Sem coleção"
        var semColecao = library.VirtualCollectionEntries.Single(e => e.Key == LibraryViewModel.NoCollectionKey);
        library.SelectSidebarCommand.Execute(semColecao);
        Assert.Single(library.Photos);
        Assert.Equal("avulsa.png", library.Photos[0].FileName);

        // Selecionar "Todas"
        var todas = library.VirtualCollectionEntries.Single(e => e.Key == LibraryViewModel.AllCollectionsKey);
        library.SelectSidebarCommand.Execute(todas);
        Assert.Equal(2, library.Photos.Count); // fotos em pelo menos uma coleção
    }

    [Fact]
    public async Task Library_CreateRootCollection_AddsNodeAndUpdatesDatabase()
    {
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        // Configurar delegate para simular o diálogo
        library.ShowNameDialog = vm =>
        {
            vm.Name = "Natureza";
            return true;
        };

        library.CreateRootCollectionCommand.Execute(null);
        await Task.Delay(50); // permite conclusão da task assíncrona disparada pelo comando

        var all = await _env.Collections.GetAllAsync();
        Assert.Contains(all, c => c.Name == "Natureza" && c.ParentCollectionId == null);
        Assert.Contains(library.CollectionNodes, n => n.Name == "Natureza");
    }

    [Fact]
    public async Task Library_CreateSubcollection_ExpandsParentNodeAndAddsChild()
    {
        var parent = await _env.Collections.CreateAsync("Eventos", null);
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        var parentNode = library.CollectionNodes.First(n => n.Id == parent.Id);
        Assert.False(parentNode.IsExpanded);

        library.ShowNameDialog = vm =>
        {
            vm.Name = "Casamento";
            return true;
        };
        library.CreateSubcollectionCommand.Execute(parentNode);
        await Task.Delay(50);

        var all = await _env.Collections.GetAllAsync();
        var sub = all.FirstOrDefault(c => c.Name == "Casamento" && c.ParentCollectionId == parent.Id);
        Assert.NotNull(sub);

        var updatedParentNode = library.CollectionNodes.First(n => n.Id == parent.Id);
        Assert.True(updatedParentNode.IsExpanded);
        Assert.Contains(updatedParentNode.Children, c => c.Name == "Casamento");
    }

    [Fact]
    public async Task Library_RenameCollection_UpdatesNodeAndAssociatedPhotos()
    {
        _env.CreatePng("evento.png");
        var col = await _env.Collections.CreateAsync("AntigoNome", null);

        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var card = library.Photos.Single();
        await library.AddPhotoToCollectionAsync(card, col.Id);

        var node = library.CollectionNodes.First(n => n.Id == col.Id);
        library.ShowNameDialog = vm =>
        {
            vm.Name = "NovoNome";
            return true;
        };

        library.RenameCollectionCommand.Execute(node);
        await Task.Delay(50);

        var reloaded = await _env.Collections.GetByIdAsync(col.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("NovoNome", reloaded.Name);

        Assert.Contains(library.CollectionNodes, n => n.Name == "NovoNome");
        Assert.Contains("NovoNome", card.Photo.Collections);
    }

    [Fact]
    public async Task Library_MoveCollection_UpdatesParentAndPreventsCycles()
    {
        var viagens = await _env.Collections.CreateAsync("Viagens", null);
        var brasil = await _env.Collections.CreateAsync("Brasil", viagens.Id);
        var sp = await _env.Collections.CreateAsync("SP", brasil.Id);
        var trabalho = await _env.Collections.CreateAsync("Trabalho", null);

        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        var spNode = library.CollectionNodes
            .SelectMany(n => n.SelfAndDescendants())
            .First(n => n.Id == sp.Id);

        // Mover SP para Trabalho
        library.ShowMoveDialog = vm =>
        {
            vm.SelectedDestination = vm.Destinations.First(d => d.ParentId == trabalho.Id);
            return true;
        };

        library.MoveCollectionCommand.Execute(spNode);
        await Task.Delay(50);

        var reloadedSP = await _env.Collections.GetByIdAsync(sp.Id);
        Assert.NotNull(reloadedSP);
        Assert.Equal(trabalho.Id, reloadedSP.ParentCollectionId);

        // Tentar mover Viagens para dentro do seu próprio filho Brasil (deve falhar a validação com ciclo detectado)
        var cycleResult = await _env.Collections.MoveAsync(viagens.Id, brasil.Id);
        Assert.False(cycleResult.Success);
        Assert.Contains("Ciclo detectado", cycleResult.Message);
    }

    [Fact]
    public async Task Library_DeleteCollection_PromoteChildren_MovesChildrenToParentAndDoesNotDeleteFiles()
    {
        var filePath = _env.CreatePng("foto_promover.png");
        var pai = await _env.Collections.CreateAsync("Pai", null);
        var meio = await _env.Collections.CreateAsync("Meio", pai.Id);
        var neto = await _env.Collections.CreateAsync("Neto", meio.Id);

        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var card = library.Photos.Single();
        await library.AddPhotoToCollectionAsync(card, meio.Id);

        var meioNode = library.CollectionNodes
            .SelectMany(n => n.SelfAndDescendants())
            .First(n => n.Id == meio.Id);

        library.ShowDeleteDialog = vm =>
        {
            vm.SelectedMode = DeleteMode.PromoteChildren;
            return true;
        };

        library.DeleteCollectionCommand.Execute(meioNode);
        await Task.Delay(50);

        // Meio excluído
        Assert.Null(await _env.Collections.GetByIdAsync(meio.Id));

        // Neto foi promovido para Pai
        var reloadedNeto = await _env.Collections.GetByIdAsync(neto.Id);
        Assert.NotNull(reloadedNeto);
        Assert.Equal(pai.Id, reloadedNeto.ParentCollectionId);

        // Arquivo físico permanece intacto!
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task Library_DeleteCollection_WithDescendants_CascadesSubtreeAndDoesNotDeleteFiles()
    {
        var filePath = _env.CreatePng("foto_cascata.png");
        var pai = await _env.Collections.CreateAsync("PaiCascata", null);
        var filho = await _env.Collections.CreateAsync("FilhoCascata", pai.Id);

        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var card = library.Photos.Single();
        await library.AddPhotoToCollectionAsync(card, filho.Id);

        var paiNode = library.CollectionNodes.First(n => n.Id == pai.Id);

        library.ShowDeleteDialog = vm =>
        {
            vm.SelectedMode = DeleteMode.WithDescendants;
            return true;
        };

        library.DeleteCollectionCommand.Execute(paiNode);
        await Task.Delay(50);

        Assert.Null(await _env.Collections.GetByIdAsync(pai.Id));
        Assert.Null(await _env.Collections.GetByIdAsync(filho.Id));

        // Arquivo físico NUNCA é tocado
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task Library_Chips_AddAndRemove_ReflectsInPhotoAndTreeCounts()
    {
        _env.CreatePng("foto1.png");
        var colA = await _env.Collections.CreateAsync("ColecaoA", null);
        var colB = await _env.Collections.CreateAsync("ColecaoB", null);

        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        var card = library.Photos.Single();
        library.SelectedPhoto = card;

        Assert.Empty(library.SelectedPhotoCollections);
        Assert.Equal(2, library.AvailableCollectionsForSelectedPhoto.Count);

        // Adicionar chip via AvailableCollectionsForSelectedPhoto
        var itemA = library.AvailableCollectionsForSelectedPhoto.First(i => i.Id == colA.Id);
        await library.AddPhotoToCollectionAsync(card, itemA.Id);

        Assert.Single(library.SelectedPhotoCollections);
        Assert.Equal("ColecaoA", library.SelectedPhotoCollections[0].Name);
        Assert.Single(library.AvailableCollectionsForSelectedPhoto);

        // Adicionar segunda coleção
        var itemB = library.AvailableCollectionsForSelectedPhoto.First(i => i.Id == colB.Id);
        await library.AddPhotoToCollectionAsync(card, itemB.Id);

        Assert.Equal(2, library.SelectedPhotoCollections.Count);
        Assert.Empty(library.AvailableCollectionsForSelectedPhoto);

        // Remover chip da ColecaoA via comando
        var chipToRemove = library.SelectedPhotoCollections.First(c => c.Id == colA.Id);
        library.RemovePhotoFromCollectionCommand.Execute(chipToRemove);
        await Task.Delay(50);

        Assert.Single(library.SelectedPhotoCollections);
        Assert.Equal("ColecaoB", library.SelectedPhotoCollections[0].Name);
        Assert.Contains(library.AvailableCollectionsForSelectedPhoto, i => i.Id == colA.Id);
    }

    [Fact]
    public async Task Library_SaveSelected_PreservesCollectionsWithoutMutation()
    {
        _env.CreatePng("save_test.png");
        var col = await _env.Collections.CreateAsync("Fixa", null);

        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        var card = library.Photos.Single();
        library.SelectedPhoto = card;
        await library.AddPhotoToCollectionAsync(card, col.Id);

        // Modifica outros metadados e chama SaveSelectedAsync
        card.Category = "Viagens";
        card.TagsText = "ferias";
        await library.SaveSelectedAsync();

        // As coleções devem continuar intactas
        Assert.Contains(col.Id, card.Photo.CollectionIds);
        Assert.Contains("Fixa", card.Photo.Collections);

        var reloaded = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var reloadedCard = reloaded.Photos.First(p => p.FileName == "save_test.png");
        Assert.Contains("Fixa", reloadedCard.Photo.Collections);
    }

    [Fact]
    public async Task Library_BatchOrganization_AppliesCollectionIdsToAllSelectedPhotos()
    {
        _env.CreatePng("batch1.png");
        _env.CreatePng("batch2.png");
        var col = await _env.Collections.CreateAsync("Lote2026", null);

        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        library.UpdateSelection(library.Photos.ToList());

        // Adiciona coleção ao lote
        var batchItem = library.AvailableCollectionsForBatch.First(i => i.Id == col.Id);
        library.AddBatchCollection(batchItem);

        Assert.Single(library.BatchSelectedCollections);
        Assert.Equal("Lote2026", library.BatchSelectedCollections[0].Name);

        // Aplica organização em lote
        await library.ApplyOrganizationBatchAsync();

        var allPhotos = library.Photos.Select(p => p.Photo).ToList();
        Assert.All(allPhotos, p => Assert.Contains(col.Id, p.CollectionIds));
        Assert.All(allPhotos, p => Assert.Contains("Lote2026", p.Collections));
    }

    [Fact]
    public void DialogViewModels_Validation_CollectionNameAndMoveRules()
    {
        // 1. CollectionNameViewModel
        var siblings = new List<string> { "Fotos", "Vídeos" };
        var nameVm = new CollectionNameViewModel("Criar Coleção", "", siblings);

        // Vazio não é válido
        Assert.False(nameVm.IsValid);

        // Nome existente entre irmãos não é válido (case-insensitive)
        nameVm.Name = "fotos";
        Assert.False(nameVm.IsValid);
        Assert.Contains("já existe", nameVm.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // Nome válido
        nameVm.Name = "Músicas";
        Assert.True(nameVm.IsValid);
        Assert.True(string.IsNullOrEmpty(nameVm.ErrorMessage));

        // 2. MoveCollectionViewModel
        var pai = new Collection { Id = 1, Name = "Pai", ParentCollectionId = null };
        var source = new Collection { Id = 2, Name = "Filho", ParentCollectionId = 1 };
        var neto = new Collection { Id = 3, Name = "Neto", ParentCollectionId = 2 };
        var outro = new Collection { Id = 4, Name = "Outro", ParentCollectionId = null };

        var allItems = new List<Collection> { pai, source, neto, outro };
        var roots = CollectionNode.Build(allItems, new Dictionary<long, int>());
        var sourceNode = roots.SelectMany(r => r.SelfAndDescendants()).First(n => n.Id == 2);
        var moveVm = new MoveCollectionViewModel(sourceNode, roots);

        // Deve conter a opção de raiz
        Assert.Contains(moveVm.Destinations, d => d.IsRootOption && d.IsEnabled);

        // Pai atual deve estar desabilitado ("Já é o pai atual")
        var paiDest = moveVm.Destinations.First(d => d.Id == 1);
        Assert.False(paiDest.IsEnabled);
        Assert.Contains("pai atual", paiDest.DisabledReason, StringComparison.OrdinalIgnoreCase);

        // Próprio nó deve estar desabilitado
        var selfDest = moveVm.Destinations.First(d => d.Id == 2);
        Assert.False(selfDest.IsEnabled);

        // Neto (descendente) deve estar desabilitado
        var netoDest = moveVm.Destinations.First(d => d.Id == 3);
        Assert.False(netoDest.IsEnabled);
        Assert.Contains("Subcoleção", netoDest.DisabledReason, StringComparison.OrdinalIgnoreCase);

        // Outro deve estar habilitado
        var outroDest = moveVm.Destinations.First(d => d.Id == 4);
        Assert.True(outroDest.IsEnabled);
    }

    [Fact]
    public void WpfUi_DialogsAndViews_InstantiateWithoutExceptions()
    {
        RunSta(() =>
        {
            // Garante que os diálogos XAML compilam e instanciam corretamente no thread STA do WPF
            var nameVm = new CollectionNameViewModel("Teste", "Nome", new[] { "Outro" });
            var nameDialog = new CollectionNameDialog(nameVm);
            Assert.NotNull(nameDialog);

            var delVm = new DeleteCollectionViewModel(1, "Teste", 2, 5, 8);
            var delDialog = new DeleteCollectionDialog(delVm);
            Assert.NotNull(delDialog);

            var sourceItem = new Collection { Id = 1, Name = "Origem", ParentCollectionId = null };
            var roots = CollectionNode.Build(new[] { sourceItem }, new Dictionary<long, int>());
            var moveVm = new MoveCollectionViewModel(roots[0], roots);
            var moveDialog = new MoveCollectionDialog(moveVm);
            Assert.NotNull(moveDialog);
        });
    }
}
