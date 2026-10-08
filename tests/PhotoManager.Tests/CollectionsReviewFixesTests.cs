using PhotoManager.Application.Collections;

namespace PhotoManager.Tests;

/// <summary>Regressões encontradas na revisão da frente de coleções.</summary>
[Collection("WpfUi")]
public sealed class CollectionsReviewFixesTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task DeleteAsync_PromoteChildren_ChildWithSameNameAsDeletedParent_IsPromotedWithoutConflict()
    {
        var pai = await _env.Collections.CreateAsync("Natal", null);
        var filho = await _env.Collections.CreateAsync("Natal", pai.Id);

        var result = await _env.Collections.DeleteAsync(pai.Id, DeleteMode.PromoteChildren);

        Assert.Equal(1, result.PromotedCount);
        var all = await _env.Collections.GetAllAsync();
        var promoted = Assert.Single(all);
        Assert.Equal(filho.Id, promoted.Id);
        Assert.Null(promoted.ParentCollectionId);
        Assert.Equal("Natal", promoted.Name);
    }

    [Theory]
    [InlineData("A\u001eB")]
    [InlineData("A\u001fB")]
    [InlineData("A\tB")]
    public async Task CollectionNames_WithControlCharacters_AreRejected(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _env.Collections.CreateAsync(name, null));
        var ok = await _env.Collections.CreateAsync("Ok", null);
        await Assert.ThrowsAsync<ArgumentException>(() => _env.Collections.RenameAsync(ok.Id, name));
    }

    [Theory]
    [InlineData("A=B")]
    [InlineData("Pai|Filho")]
    [InlineData("1=2=3|4")]
    public async Task CollectionNames_WithSeparatorCharacters_RoundTripThroughPhotoQuery(string name)
    {
        var path = _env.CreatePng("sep.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photo = (await _env.Catalog.GetPhotosAsync()).First(p => p.CurrentPath == path);
        var other = await _env.Collections.CreateAsync("Outra", null);
        var col = await _env.Collections.CreateAsync(name, null);
        await _env.Collections.AddPhotosAsync(col.Id, [photo.Id]);
        await _env.Collections.AddPhotosAsync(other.Id, [photo.Id]);

        var reloaded = (await _env.Catalog.GetPhotosAsync()).First(p => p.Id == photo.Id);

        Assert.Equal(2, reloaded.CollectionIds.Count);
        Assert.Contains(col.Id, reloaded.CollectionIds);
        Assert.Contains(other.Id, reloaded.CollectionIds);
        Assert.Contains(name, reloaded.Collections);
        Assert.Contains("Outra", reloaded.Collections);
        Assert.Equal(2, reloaded.Collections.Count);
    }

    [Fact]
    public async Task ExecuteCollectionDropPlan_WhenCollectionNoLongerExists_ReportsErrorInsteadOfThrowing()
    {
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);
        var plan = new CollectionDropPlan(CollectionDropAction.Move, 9999, "Fantasma", null, "Raiz", "Mover", true);

        var ok = await vm.ExecuteCollectionDropPlanAsync(plan);

        Assert.False(ok);
        Assert.Contains("não encontrada", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tree_SiblingsAreAlphabetical_RegardlessOfCreationOrOfMoves()
    {
        var zeta = await _env.Collections.CreateAsync("Zeta", null);
        var alfa = await _env.Collections.CreateAsync("Alfa", null);
        var pai = await _env.Collections.CreateAsync("Pai", null);
        await _env.Collections.CreateAsync("Beta", pai.Id);
        await _env.Collections.MoveAsync(zeta.Id, pai.Id);   // Zeta vira o último criado/movido em "Pai"
        await _env.Collections.CreateAsync("Alfa2", pai.Id);

        var tree = await _env.Collections.GetTreeAsync();
        Assert.Equal(["Alfa", "Pai"], tree.Roots.Select(n => n.Name));
        Assert.Equal(["Alfa2", "Beta", "Zeta"], tree.Roots.First(n => n.Id == pai.Id).Children.Select(n => n.Name));

        var all = await _env.Collections.GetAllAsync();
        var nodes = PhotoManager.Wpf.Views.CollectionNode.Build(all, new Dictionary<long, int>());
        Assert.Equal(["Alfa", "Pai"], nodes.Select(n => n.Name));
        Assert.Equal(["Alfa2", "Beta", "Zeta"], nodes.First(n => n.Id == pai.Id).Children.Select(n => n.Name));
        Assert.NotEqual(0L, alfa.Id);
    }
}
