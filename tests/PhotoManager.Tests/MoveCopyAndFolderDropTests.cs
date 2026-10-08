using System.IO;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

[Collection("WpfUi")]
public sealed class MoveCopyAndFolderDropTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    // ---------- colecoes: arrastar = mover, Ctrl/Shift = copiar ----------

    private static DropPlanRequest Request(bool copy, long? active, params (long Photo, long[] Cols)[] photos) => new(
        PhotoIds: photos.Select(p => p.Photo).ToList(), ActiveSourceCollectionId: active, TargetCollectionId: 9, TargetCollectionName: "Destino",
        TargetIsVirtual: false, IsTargetHeaderOrEmpty: false, CopyPressed: copy,
        PhotosCollectionsMap: photos.ToDictionary(p => p.Photo, p => (IReadOnlyList<long>)p.Cols),
        CollectionNames: new Dictionary<long, string> { [1] = "Origem", [2] = "Outra", [9] = "Destino" });

    [Fact]
    public void Collections_PlainDrag_Moves_FromActiveCollection()
    {
        var plan = DropPlanner.Plan(Request(copy: false, active: 1, (10, [1, 2]), (20, [1])));
        Assert.Equal(DropAction.Move, plan.Action);
        Assert.Equal(1, plan.SourceCollectionId);
        Assert.Equal([10L, 20L], plan.PhotoIdsToMove);
    }

    [Fact]
    public void Collections_CtrlOrShiftDrag_Copies_KeepingTheSource()
    {
        var plan = DropPlanner.Plan(Request(copy: true, active: 1, (10, [1]), (20, [1])));
        Assert.Equal(DropAction.Add, plan.Action);
        Assert.Equal([10L, 20L], plan.PhotoIdsToAdd);
        Assert.Empty(plan.PhotoIdsToMove);
        Assert.Contains("Copiar 2 fotos", plan.Message);
    }

    [Fact]
    public void Collections_PlainDrag_WithoutActiveSource_AsksOnlyWhenThereIsSomethingToMoveFrom()
    {
        var withCommon = DropPlanner.Plan(Request(copy: false, active: null, (10, [1]), (20, [1, 2])));
        Assert.Equal(DropAction.AskMenu, withCommon.Action);
        Assert.Contains(withCommon.AskMenuOptions, o => o.Action == DropAction.Move && o.SourceCollectionId == 1);

        var without = DropPlanner.Plan(Request(copy: false, active: null, (10, []), (20, [2])));
        Assert.Equal(DropAction.Add, without.Action);
    }

    // ---------- pastas: arrastar move o arquivo, Ctrl/Shift copia ----------

    [Fact]
    public void FolderDropPlanner_MoveCopyAndSkips()
    {
        FolderDropPhoto[] photos = [new(1, @"D:\A", false), new(2, @"D:\B", false), new(3, @"D:\Dest", false), new(4, @"D:\C", true)];

        var move = FolderDropPlanner.Plan(photos, @"D:\Dest\", "Dest", copy: false);
        Assert.Equal(FolderDropAction.Move, move.Action);
        Assert.Equal([1L, 2L], move.PhotoIds);             // a que ja esta na pasta e a ausente ficam de fora
        Assert.Equal(1, move.SkippedAlreadyThere);
        Assert.Equal(1, move.SkippedMissing);
        Assert.Contains("Mover 2 fotos", move.Message);

        var copy = FolderDropPlanner.Plan(photos, @"D:\Dest", "Dest", copy: true);
        Assert.Equal(FolderDropAction.Copy, copy.Action);
        Assert.Contains("Copiar 2 fotos", copy.Message);

        Assert.Equal(FolderDropAction.NoOp, FolderDropPlanner.Plan([photos[2]], @"d:\dest", "Dest", false).Action);
        Assert.False(FolderDropPlanner.Plan([photos[3]], @"D:\Dest", "Dest", false).CanDrop);
        Assert.Equal(FolderDropAction.Blocked, FolderDropPlanner.Plan(photos, null, "", false).Action);
        Assert.Equal(FolderDropAction.Blocked, FolderDropPlanner.Plan([], @"D:\Dest", "Dest", false).Action);
    }

    [Fact]
    public async Task FolderDrop_Move_MovesTheFilePhysically_AndKeepsThePhotoId()
    {
        var path = _env.CreatePng("m1.png");
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);
        var dest = Path.Combine(_env.Photos, "destino");
        Directory.CreateDirectory(dest);
        var photo = (await _env.Catalog.GetPhotosAsync()).Single(p => p.CurrentPath == path);

        var plan = vm.PlanFolderDrop([photo.Id], new FolderNode(dest, "destino"), copyPressed: false);
        Assert.Equal(FolderDropAction.Move, plan.Action);
        await vm.ExecuteFolderDropAsync(plan);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(dest, "m1.png")));
        var after = (await _env.Catalog.GetPhotosAsync()).Single(p => p.Id == photo.Id);
        Assert.Equal(Path.Combine(dest, "m1.png"), after.CurrentPath);
    }

    [Fact]
    public async Task FolderDrop_CtrlCopy_KeepsTheOriginal_AndCatalogsTheCopy()
    {
        var path = _env.CreatePng("c1.png");
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);
        var dest = Path.Combine(_env.Photos, "copias");
        Directory.CreateDirectory(dest);
        var photo = (await _env.Catalog.GetPhotosAsync()).Single(p => p.CurrentPath == path);

        var plan = vm.PlanFolderDrop([photo.Id], new FolderNode(dest, "copias"), copyPressed: true);
        Assert.Equal(FolderDropAction.Copy, plan.Action);
        await vm.ExecuteFolderDropAsync(plan);

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(dest, "c1.png")));
        Assert.Equal(2, (await _env.Catalog.GetPhotosAsync()).Count);
    }

    // ---------- lote: colecoes em comum ----------

    [Fact]
    public void CommonCollections_OnlyWhenAllPhotosHaveExactlyTheSameSet()
    {
        Assert.Equal([1L, 2L], CollectionSelectionHelper.CommonWhenIdentical([new long[] { 1, 2 }, new long[] { 2, 1 }, new long[] { 1, 2 }]));
        Assert.Empty(CollectionSelectionHelper.CommonWhenIdentical([new long[] { 1, 2 }, new long[] { 1 }]));
        Assert.Empty(CollectionSelectionHelper.CommonWhenIdentical([new long[] { 1 }, new long[] { 1, 3 }]));
        Assert.Empty(CollectionSelectionHelper.CommonWhenIdentical([Array.Empty<long>(), Array.Empty<long>()]));
        Assert.Empty(CollectionSelectionHelper.CommonWhenIdentical([new long[] { 1 }]));
    }

    [Fact]
    public async Task RemoveCommonCollections_RemovesFromAllSelectedPhotos_OnlyTheAssociation()
    {
        var p1 = _env.CreatePng("r1.png");
        var p2 = _env.CreatePng("r2.png");
        var keep = await _env.Collections.CreateAsync("Fica", null);
        var drop = await _env.Collections.CreateAsync("Sai", null);
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);
        var ids = (await _env.Catalog.GetPhotosAsync()).Select(p => p.Id).ToList();
        await _env.Collections.AddPhotosAsync(keep.Id, ids);
        await _env.Collections.AddPhotosAsync(drop.Id, ids);
        await vm.ImportFolderAsync(_env.Photos);
        vm.UpdateSelection(vm.Photos.ToList());

        Assert.True(vm.HasCommonCollections);
        Assert.Equal(2, vm.CommonCollections.Count);

        await vm.RemoveCommonCollectionsAsync([drop.Id]);

        var after = await _env.Catalog.GetPhotosAsync();
        Assert.Equal(2, after.Count);
        Assert.All(after, p => Assert.Equal([keep.Id], p.CollectionIds));
        Assert.True(File.Exists(p1) && File.Exists(p2));
        Assert.Single(vm.CommonCollections);
        await vm.RemoveCommonCollectionsAsync(vm.CommonCollections.Select(c => c.Id).ToList());
        Assert.False(vm.HasCommonCollections);
    }

    // ---------- subcolecoes: item virtual "Todas" ----------

    private async Task<(LibraryViewModel Vm, long Pai, long Filho, long Neto, long Outra, List<long> PhotoIds)> BuildTreeAsync()
    {
        for (var i = 0; i < 4; i++) _env.CreatePng($"s{i}.png");
        var pai = await _env.Collections.CreateAsync("Pai", null);
        var filho = await _env.Collections.CreateAsync("Filho", pai.Id);
        var neto = await _env.Collections.CreateAsync("Neto", filho.Id);
        var outra = await _env.Collections.CreateAsync("Outra", null);
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);
        var ids = (await _env.Catalog.GetPhotosAsync()).Select(p => p.Id).ToList();
        await _env.Collections.AddPhotosAsync(pai.Id, [ids[0]]);
        await _env.Collections.AddPhotosAsync(filho.Id, [ids[1]]);
        await _env.Collections.AddPhotosAsync(neto.Id, [ids[2]]);
        await _env.Collections.AddPhotosAsync(outra.Id, [ids[3]]);
        await vm.ImportFolderAsync(_env.Photos);
        return (vm, pai.Id, filho.Id, neto.Id, outra.Id, ids);
    }

    [Fact]
    public async Task AggregateNode_IsFirstChild_OnlyWhenThereAreSubcollections_AndIsNotARealNode()
    {
        var (vm, pai, _, neto, outra, _) = await BuildTreeAsync();
        var paiNode = vm.CollectionNodes.Single(n => n.Id == pai);

        Assert.True(paiNode.DisplayChildren[0].IsAggregate);
        Assert.Equal("Todas", paiNode.DisplayChildren[0].Name);
        Assert.Equal(paiNode.Children.Count + 1, paiNode.DisplayChildren.Count);
        Assert.DoesNotContain(paiNode.SelfAndDescendants(), n => n.IsAggregate);
        Assert.Contains(paiNode.SelfAndDescendants(includeAggregates: true), n => n.IsAggregate);
        Assert.Equal(4, vm.CollectionNodes.SelectMany(r => r.SelfAndDescendants()).Count());
        Assert.Null(vm.CollectionNodes.Single(n => n.Id == outra).AggregateNode);
        Assert.Null(vm.CollectionNodes.SelectMany(r => r.SelfAndDescendants()).Single(n => n.Id == neto).AggregateNode);
        Assert.Equal(3, paiNode.AggregateNode!.DirectCount);
        Assert.Equal(1, paiNode.DirectCount);
    }

    [Fact]
    public async Task AggregateNode_Selecting_ListsTheCollectionAndAllItsSubcollections()
    {
        var (vm, pai, _, _, _, ids) = await BuildTreeAsync();
        var paiNode = vm.CollectionNodes.Single(n => n.Id == pai);

        vm.SelectCollectionNode(paiNode.AggregateNode);
        Assert.Equal(3, vm.Photos.Count);
        Assert.Equal(ids.Take(3).OrderBy(i => i), vm.Photos.Select(c => c.Photo.Id).OrderBy(i => i));
        Assert.True(vm.HasActiveFilters);

        vm.SelectCollectionNode(paiNode);
        Assert.Single(vm.Photos);
    }

    [Fact]
    public async Task AggregateNode_HasNoEditCommands_AndIsNeverADropTarget()
    {
        var (vm, pai, _, _, _, ids) = await BuildTreeAsync();
        var aggregate = vm.CollectionNodes.Single(n => n.Id == pai).AggregateNode!;
        var dialogs = 0;
        vm.ShowNameDialog = _ => { dialogs++; return false; };
        vm.ShowDeleteDialog = _ => { dialogs++; return false; };

        await vm.CreateSubcollectionAsync(aggregate);
        await vm.RenameCollectionAsync(aggregate);
        await vm.DeleteCollectionAsync(aggregate);
        Assert.Equal(0, dialogs);

        var photoPlan = vm.PlanDrop([ids[3]], aggregate.Id, aggregate.Name, targetIsVirtual: true, isTargetHeaderOrEmpty: false, copyPressed: false);
        Assert.False(photoPlan.CanDrop);
        var collectionPlan = vm.PlanCollectionDrop(ids[3], aggregate.Id, aggregate.Name, targetIsRoot: false, targetIsVirtual: true);
        Assert.False(collectionPlan.CanDrop);
    }
}
