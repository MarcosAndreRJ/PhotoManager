using System.Threading;
using System.Windows.Threading;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Domain.Collections;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>
/// Testes abrangentes da Etapa D:
/// - DropPlanner (função pura de decisão: Adicionar, Mover, Origem Ambígua/AskMenu, Bloqueios, NoOp, contagens)
/// - PhotoSelectionDragHelper (regra de seleção múltipla vs foto única)
/// - Execução no LibraryViewModel via ICollectionService (transacional, sem mexer em arquivos físicos)
/// - Atualização de fotos em memória, contadores da sidebar e filtro da grade
/// - Concorrência lógica: salvar organização após drag-and-drop preserva as coleções
/// </summary>
[Collection("WpfUi")]
public sealed class CollectionsEtapaDTests : IDisposable
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
    public void PhotoSelectionDragHelper_ClickedSelectedPhoto_ReturnsAllSelected()
    {
        var selectedIds = new List<long> { 101, 102, 103 };
        var dragged = PhotoSelectionDragHelper.ResolvePhotoIdsToDrag(102, selectedIds);

        Assert.Equal(3, dragged.Count);
        Assert.Contains(101, dragged);
        Assert.Contains(102, dragged);
        Assert.Contains(103, dragged);
    }

    [Fact]
    public void PhotoSelectionDragHelper_ClickedUnselectedPhoto_ReturnsOnlyClickedPhoto()
    {
        var selectedIds = new List<long> { 101, 102, 103 };
        var dragged = PhotoSelectionDragHelper.ResolvePhotoIdsToDrag(999, selectedIds);

        Assert.Single(dragged);
        Assert.Equal(999, dragged[0]);
    }

    [Fact]
    public void DropPlanner_AddSinglePhoto_ReturnsAddPlan()
    {
        var request = new DropPlanRequest(
            PhotoIds: [10],
            ActiveSourceCollectionId: null,
            TargetCollectionId: 1,
            TargetCollectionName: "Viagens",
            TargetIsVirtual: false,
            IsTargetHeaderOrEmpty: false,
            CopyPressed: true,
            PhotosCollectionsMap: new Dictionary<long, IReadOnlyList<long>> { [10] = [] },
            CollectionNames: new Dictionary<long, string> { [1] = "Viagens" });

        var plan = DropPlanner.Plan(request);

        Assert.Equal(DropAction.Add, plan.Action);
        Assert.True(plan.CanDrop);
        Assert.Equal(1, plan.TargetCollectionId);
        Assert.Single(plan.PhotoIdsToAdd);
        Assert.Equal(10, plan.PhotoIdsToAdd[0]);
        Assert.Empty(plan.PhotoIdsAlreadyInTarget);
        Assert.Contains("+ Copiar 1 foto para “Viagens”", plan.Message);
    }

    [Fact]
    public void DropPlanner_AddMultiplePhotos_MixedTargetMembership_ReturnsAddPlanWithBothCounts()
    {
        var request = new DropPlanRequest(
            PhotoIds: [10, 20, 30],
            ActiveSourceCollectionId: null,
            TargetCollectionId: 1,
            TargetCollectionName: "Viagens",
            TargetIsVirtual: false,
            IsTargetHeaderOrEmpty: false,
            CopyPressed: true,
            PhotosCollectionsMap: new Dictionary<long, IReadOnlyList<long>>
            {
                [10] = [1],     // Já pertence
                [20] = [2, 3],  // Não pertence
                [30] = []       // Não pertence
            },
            CollectionNames: new Dictionary<long, string> { [1] = "Viagens", [2] = "Praia", [3] = "Férias" });

        var plan = DropPlanner.Plan(request);

        Assert.Equal(DropAction.Add, plan.Action);
        Assert.True(plan.CanDrop);
        Assert.Equal(2, plan.PhotoIdsToAdd.Count);
        Assert.Contains(20, plan.PhotoIdsToAdd);
        Assert.Contains(30, plan.PhotoIdsToAdd);
        Assert.Single(plan.PhotoIdsAlreadyInTarget);
        Assert.Equal(10, plan.PhotoIdsAlreadyInTarget[0]);
        Assert.Contains("Copiar 2 foto(s) para “Viagens” (1 já pertence(m))", plan.Message);
    }

    [Fact]
    public void DropPlanner_AddPhotos_AllAlreadyInTarget_ReturnsNoOp()
    {
        var request = new DropPlanRequest(
            PhotoIds: [10, 20],
            ActiveSourceCollectionId: null,
            TargetCollectionId: 1,
            TargetCollectionName: "Viagens",
            TargetIsVirtual: false,
            IsTargetHeaderOrEmpty: false,
            CopyPressed: true,
            PhotosCollectionsMap: new Dictionary<long, IReadOnlyList<long>>
            {
                [10] = [1],
                [20] = [1, 2]
            },
            CollectionNames: new Dictionary<long, string> { [1] = "Viagens" });

        var plan = DropPlanner.Plan(request);

        Assert.Equal(DropAction.NoOp, plan.Action);
        Assert.True(plan.CanDrop);
        Assert.Empty(plan.PhotoIdsToAdd);
        Assert.Equal(2, plan.PhotoIdsAlreadyInTarget.Count);
        Assert.Contains("Todas as 2 fotos já pertencem à coleção “Viagens”", plan.Message);
    }

    [Fact]
    public void DropPlanner_Move_WithActiveSource_ReturnsMovePlan()
    {
        var request = new DropPlanRequest(
            PhotoIds: [10, 20, 30],
            ActiveSourceCollectionId: 1, // Ativo na sidebar: Viagens
            TargetCollectionId: 2,       // Destino: Roraima 2026
            TargetCollectionName: "Roraima 2026",
            TargetIsVirtual: false,
            IsTargetHeaderOrEmpty: false,
            CopyPressed: false,
            PhotosCollectionsMap: new Dictionary<long, IReadOnlyList<long>>
            {
                [10] = [1],
                [20] = [1, 5],
                [30] = [1]
            },
            CollectionNames: new Dictionary<long, string>
            {
                [1] = "Viagens",
                [2] = "Roraima 2026"
            });

        var plan = DropPlanner.Plan(request);

        Assert.Equal(DropAction.Move, plan.Action);
        Assert.True(plan.CanDrop);
        Assert.Equal(1, plan.SourceCollectionId);
        Assert.Equal("Viagens", plan.SourceCollectionName);
        Assert.Equal(2, plan.TargetCollectionId);
        Assert.Equal("Roraima 2026", plan.TargetCollectionName);
        Assert.Equal(3, plan.PhotoIdsToMove.Count);
        Assert.Contains("→ Mover 3 fotos de “Viagens” para “Roraima 2026”", plan.Message);
    }

    [Fact]
    public void DropPlanner_Move_ToSameCollection_ReturnsNoOp()
    {
        var request = new DropPlanRequest(
            PhotoIds: [10],
            ActiveSourceCollectionId: 1,
            TargetCollectionId: 1,
            TargetCollectionName: "Viagens",
            TargetIsVirtual: false,
            IsTargetHeaderOrEmpty: false,
            CopyPressed: false,
            PhotosCollectionsMap: new Dictionary<long, IReadOnlyList<long>> { [10] = [1] },
            CollectionNames: new Dictionary<long, string> { [1] = "Viagens" });

        var plan = DropPlanner.Plan(request);

        Assert.Equal(DropAction.NoOp, plan.Action);
        Assert.True(plan.CanDrop);
        Assert.Contains("já está nesta coleção", plan.Message);
    }

    [Fact]
    public void DropPlanner_Move_AmbiguousSource_IntersectingCollections_ReturnsAskMenuWithAllCommonOptions()
    {
        // Fotos arrastadas: 10 e 20.
        // Foto 10 pertence a: [1, 2, 4]
        // Foto 20 pertence a: [1, 2, 5]
        // Interseção comum: [1, 2]
        // Destino pretendido com Shift: [3]
        var request = new DropPlanRequest(
            PhotoIds: [10, 20],
            ActiveSourceCollectionId: null, // Sem coleção ativa (origem ambígua)
            TargetCollectionId: 3,
            TargetCollectionName: "Destino",
            TargetIsVirtual: false,
            IsTargetHeaderOrEmpty: false,
            CopyPressed: false,
            PhotosCollectionsMap: new Dictionary<long, IReadOnlyList<long>>
            {
                [10] = [1, 2, 4],
                [20] = [1, 2, 5]
            },
            CollectionNames: new Dictionary<long, string>
            {
                [1] = "Família",
                [2] = "Férias",
                [3] = "Destino",
                [4] = "Outro",
                [5] = "Mais"
            });

        var plan = DropPlanner.Plan(request);

        Assert.Equal(DropAction.AskMenu, plan.Action);
        Assert.True(plan.CanDrop);
        Assert.Equal(4, plan.AskMenuOptions.Count);

        // Opção 1: Adicionar à coleção
        Assert.Equal("Adicionar à coleção “Destino”", plan.AskMenuOptions[0].Header);
        Assert.Equal(DropAction.Add, plan.AskMenuOptions[0].Action);

        // Opções 2 e 3: Mover das coleções comuns
        Assert.Contains(plan.AskMenuOptions, opt => opt.Action == DropAction.Move && opt.SourceCollectionId == 1 && opt.Header.Contains("Família"));
        Assert.Contains(plan.AskMenuOptions, opt => opt.Action == DropAction.Move && opt.SourceCollectionId == 2 && opt.Header.Contains("Férias"));

        // Opção 4: Cancelar
        Assert.Equal("Cancelar", plan.AskMenuOptions.Last().Header);
    }

    [Fact]
    public void DropPlanner_Move_NoActiveSource_NoCommonCollections_JustAdds_WithoutMenu()
    {
        // Fotos arrastadas: 10 e 20.
        // Foto 10 pertence a: [1]
        // Foto 20 pertence a: [2]
        // Sem interseção
        var request = new DropPlanRequest(
            PhotoIds: [10, 20],
            ActiveSourceCollectionId: null,
            TargetCollectionId: 3,
            TargetCollectionName: "Destino",
            TargetIsVirtual: false,
            IsTargetHeaderOrEmpty: false,
            CopyPressed: false,
            PhotosCollectionsMap: new Dictionary<long, IReadOnlyList<long>>
            {
                [10] = [1],
                [20] = [2]
            },
            CollectionNames: new Dictionary<long, string>
            {
                [1] = "Família",
                [2] = "Férias",
                [3] = "Destino"
            });

        var plan = DropPlanner.Plan(request);

        // Arrastar = mover, mas nao ha colecao de origem de onde tirar: nao ha o que perguntar, so adiciona.
        Assert.Equal(DropAction.Add, plan.Action);
        Assert.True(plan.CanDrop);
        Assert.Empty(plan.AskMenuOptions);
        Assert.Equal(2, plan.PhotoIdsToAdd.Count);
    }

    [Fact]
    public void DropPlanner_TargetVirtual_ReturnsBlocked()
    {
        var request = new DropPlanRequest(
            PhotoIds: [10],
            ActiveSourceCollectionId: null,
            TargetCollectionId: null,
            TargetCollectionName: null,
            TargetIsVirtual: true,
            IsTargetHeaderOrEmpty: false,
            CopyPressed: true,
            PhotosCollectionsMap: new Dictionary<long, IReadOnlyList<long>> { [10] = [] },
            CollectionNames: new Dictionary<long, string>());

        var plan = DropPlanner.Plan(request);

        Assert.Equal(DropAction.Blocked, plan.Action);
        Assert.False(plan.CanDrop);
        Assert.Contains("Não é possível soltar em itens virtuais", plan.Message);
    }

    [Fact]
    public void DropPlanner_TargetHeaderOrEmpty_ReturnsBlocked()
    {
        var request = new DropPlanRequest(
            PhotoIds: [10],
            ActiveSourceCollectionId: null,
            TargetCollectionId: null,
            TargetCollectionName: null,
            TargetIsVirtual: false,
            IsTargetHeaderOrEmpty: true,
            CopyPressed: true,
            PhotosCollectionsMap: new Dictionary<long, IReadOnlyList<long>> { [10] = [] },
            CollectionNames: new Dictionary<long, string>());

        var plan = DropPlanner.Plan(request);

        Assert.Equal(DropAction.Blocked, plan.Action);
        Assert.False(plan.CanDrop);
        Assert.Contains("Destino inválido", plan.Message);
    }

    [Fact]
    public void DropPlanner_NoPhotos_ReturnsBlocked()
    {
        var request = new DropPlanRequest(
            PhotoIds: [],
            ActiveSourceCollectionId: null,
            TargetCollectionId: 1,
            TargetCollectionName: "Viagens",
            TargetIsVirtual: false,
            IsTargetHeaderOrEmpty: false,
            CopyPressed: true,
            PhotosCollectionsMap: new Dictionary<long, IReadOnlyList<long>>(),
            CollectionNames: new Dictionary<long, string> { [1] = "Viagens" });

        var plan = DropPlanner.Plan(request);

        Assert.Equal(DropAction.Blocked, plan.Action);
        Assert.False(plan.CanDrop);
        Assert.Contains("Nenhuma foto selecionada", plan.Message);
    }

    [Fact]
    public async Task LibraryViewModel_ExecuteDropPlan_Add_UpdatesCollectionsAndSidebarCounts()
    {
        var path1 = _env.CreatePng("p1.png");
        var path2 = _env.CreatePng("p2.png");

        var targetCol = await _env.Collections.CreateAsync("Natureza", null);

        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);

        var photo1 = vm.Photos.First(p => p.FileName == "p1.png").Photo;
        var photo2 = vm.Photos.First(p => p.FileName == "p2.png").Photo;

        var plan = vm.PlanDrop(
            photoIds: [photo1.Id, photo2.Id],
            targetCollectionId: targetCol.Id,
            targetCollectionName: targetCol.Name,
            targetIsVirtual: false,
            isTargetHeaderOrEmpty: false,
            copyPressed: true);

        Assert.Equal(DropAction.Add, plan.Action);
        Assert.Equal(2, plan.PhotoIdsToAdd.Count);

        await vm.ExecuteDropPlanAsync(plan);

        // Verifica status text
        Assert.Contains("2 foto(s) adicionada(s) a “Natureza”", vm.StatusText);

        // Verifica nós da sidebar
        var node = vm.CollectionNodes.FirstOrDefault(n => n.Id == targetCol.Id);
        Assert.NotNull(node);
        Assert.Equal(2, node.DirectCount);

        // Verifica banco real
        var inCol = await ((ICollectionRepository)_env.Repository).GetPhotoIdsInCollectionAsync(targetCol.Id);
        Assert.Equal(2, inCol.Count);
        Assert.Contains(photo1.Id, inCol);
        Assert.Contains(photo2.Id, inCol);

        // Arquivos físicos em disco intactos
        Assert.True(File.Exists(path1));
        Assert.True(File.Exists(path2));
    }

    [Fact]
    public async Task LibraryViewModel_ExecuteDropPlan_Move_RemovesFromSourceAndAddsToTarget()
    {
        var path1 = _env.CreatePng("p1.png");
        var path2 = _env.CreatePng("p2.png");

        var fromCol = await _env.Collections.CreateAsync("Origem", null);
        var toCol = await _env.Collections.CreateAsync("Destino", null);

        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);

        var photo1 = vm.Photos.First(p => p.FileName == "p1.png").Photo;
        var photo2 = vm.Photos.First(p => p.FileName == "p2.png").Photo;

        await _env.Collections.AddPhotosAsync(fromCol.Id, [photo1.Id, photo2.Id]);
        await vm.ImportFolderAsync(_env.Photos); // recarrega fotos e coleções

        // Seleciona a coleção de origem na sidebar para filtrar por ela
        var fromNode = vm.CollectionNodes.First(n => n.Id == fromCol.Id);
        vm.SelectCollectionNode(fromNode);

        // A grade contém as duas fotos
        Assert.Equal(2, vm.Photos.Count);

        // Planeja mover foto1 de Origem para Destino
        var plan = vm.PlanDrop(
            photoIds: [photo1.Id],
            targetCollectionId: toCol.Id,
            targetCollectionName: toCol.Name,
            targetIsVirtual: false,
            isTargetHeaderOrEmpty: false,
            copyPressed: false);

        Assert.Equal(DropAction.Move, plan.Action);
        Assert.Equal(fromCol.Id, plan.SourceCollectionId);
        Assert.Equal(toCol.Id, plan.TargetCollectionId);

        await vm.ExecuteDropPlanAsync(plan);

        // Verifica status
        Assert.Contains("1 foto(s) movida(s) de “Origem” para “Destino”", vm.StatusText);

        // Como Origem estava filtrada, a foto1 saiu da visualização atual
        Assert.Single(vm.Photos);
        Assert.Equal(photo2.Id, vm.Photos[0].Photo.Id);

        // Contadores da sidebar
        Assert.Equal(1, fromNode.DirectCount);
        var toNode = vm.CollectionNodes.First(n => n.Id == toCol.Id);
        Assert.Equal(1, toNode.DirectCount);

        // No banco de dados
        var inFrom = await ((ICollectionRepository)_env.Repository).GetPhotoIdsInCollectionAsync(fromCol.Id);
        var inTo = await ((ICollectionRepository)_env.Repository).GetPhotoIdsInCollectionAsync(toCol.Id);
        Assert.Single(inFrom);
        Assert.Equal(photo2.Id, inFrom[0]);
        Assert.Single(inTo);
        Assert.Equal(photo1.Id, inTo[0]);

        // Arquivos físicos em disco intactos
        Assert.True(File.Exists(path1));
        Assert.True(File.Exists(path2));
    }

    [Fact]
    public async Task LibraryViewModel_ExecuteDropPlan_SaveOrganizationAfterwards_PreservesCollections()
    {
        var path1 = _env.CreatePng("p1.png");
        var targetCol = await _env.Collections.CreateAsync("Portfólio", null);

        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);

        var photo1 = vm.Photos.First(p => p.FileName == "p1.png").Photo;

        var plan = vm.PlanDrop(
            photoIds: [photo1.Id],
            targetCollectionId: targetCol.Id,
            targetCollectionName: targetCol.Name,
            targetIsVirtual: false,
            isTargetHeaderOrEmpty: false,
            copyPressed: true);

        await vm.ExecuteDropPlanAsync(plan);

        // Agora altera a nota ou categoria no painel e salva
        vm.SelectPhoto(photo1.Id);
        Assert.NotNull(vm.SelectedPhoto);
        vm.SelectedPhoto.Category = "Editorial";
        vm.SelectedPhoto.Rating = 5;

        await vm.SaveSelectedAsync();

        // Recarrega do banco para garantir que a coleção persistiu após salvar organização
        await vm.ImportFolderAsync(_env.Photos);
        var all = await _env.Repository.GetAllAsync();
        var reloadedPhoto = all.FirstOrDefault(p => p.Id == photo1.Id);
        Assert.NotNull(reloadedPhoto);
        Assert.Contains(targetCol.Id, reloadedPhoto.CollectionIds);
        Assert.Equal("Editorial", reloadedPhoto.CategoryName);
        Assert.Equal(5, reloadedPhoto.Rating);
    }

    [Fact]
    public void LibraryView_DragDrop_STA_InitializesWithoutException()
    {
        RunSta(() =>
        {
            var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            if (app.Resources.MergedDictionaries.Count == 0)
                foreach (var name in new[] { "Colors", "Typography", "Buttons", "Inputs", "Cards", "Tabs", "Tree" })
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri($"pack://application:,,,/PhotoManager;component/Themes/{name}.xaml") });
            if (!app.Resources.Contains("BooleanToVisibilityConverter"))
                app.Resources.Add("BooleanToVisibilityConverter", new System.Windows.Controls.BooleanToVisibilityConverter());

            var vm = _env.CreateLibrary();
            var view = new LibraryView { DataContext = vm };
            Assert.NotNull(view);
            Assert.NotNull(view.FindName("CollectionTree"));
            Assert.NotNull(view.FindName("PhotoList"));
            Assert.NotNull(view.FindName("DragFeedbackPopup"));
        });
    }
}
