using System.IO;
using PhotoManager.Application.Transfer;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

public sealed class FullCompareTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private string CreateFile(string relativePath, int size = 10, DateTime? modified = null)
    {
        var fullPath = Path.Combine(_env.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, new byte[size]);
        if (modified.HasValue)
        {
            File.SetLastWriteTimeUtc(fullPath, modified.Value);
        }
        return fullPath;
    }

    [Fact]
    public void FullFolderComparer_DetectsSameDifferentAndMissing()
    {
        var leftRoot = Path.Combine(_env.Root, "left");
        var rightRoot = Path.Combine(_env.Root, "right");
        Directory.CreateDirectory(leftRoot);
        Directory.CreateDirectory(rightRoot);

        var date1 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var date2 = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);

        // Same file (name, size, date)
        CreateFile("left/same.jpg", 100, date1);
        CreateFile("right/same.jpg", 100, date1);

        // Different size
        CreateFile("left/sub/diff_size.png", 100, date1);
        CreateFile("right/sub/diff_size.png", 200, date1);

        // Different date
        CreateFile("left/diff_date.txt", 100, date1);
        CreateFile("right/diff_date.txt", 100, date2);

        // Only on left
        CreateFile("left/only_left.jpg", 50, date1);

        // Only on right
        CreateFile("right/sub/only_right.jpg", 60, date1);

        var result = FullFolderComparer.Compare(leftRoot, rightRoot);

        // Pastas + arquivos
        Assert.Equal(1, result.Summary.SameCount); // same.jpg
        Assert.True(result.Summary.DifferentCount >= 2); // diff_size, diff_date (e a pasta 'sub' com diferenças)
        Assert.Equal(1, result.Summary.OnlyLeftCount); // only_left.jpg
        Assert.Equal(1, result.Summary.OnlyRightCount); // only_right.jpg

        var sameItem = result.Items.First(i => i.RelativePath == "same.jpg");
        Assert.Equal(FullCompareStatus.Same, sameItem.Status);

        var diffSizeItem = result.Items.First(i => i.RelativePath == Path.Combine("sub", "diff_size.png"));
        Assert.Equal(FullCompareStatus.Different, diffSizeItem.Status);
        Assert.Contains("tamanho", diffSizeItem.Details, StringComparison.OrdinalIgnoreCase);

        var diffDateItem = result.Items.First(i => i.RelativePath == "diff_date.txt");
        Assert.Equal(FullCompareStatus.Different, diffDateItem.Status);
        Assert.Contains("data", diffDateItem.Details, StringComparison.OrdinalIgnoreCase);

        var onlyLeftItem = result.Items.First(i => i.RelativePath == "only_left.jpg");
        Assert.Equal(FullCompareStatus.OnlyLeft, onlyLeftItem.Status);

        var onlyRightItem = result.Items.First(i => i.RelativePath == Path.Combine("sub", "only_right.jpg"));
        Assert.Equal(FullCompareStatus.OnlyRight, onlyRightItem.Status);
    }

    [Fact]
    public async Task FullCompareViewModel_LoadsAndFiltersCorrectly()
    {
        var leftRoot = Path.Combine(_env.Root, "vm_left");
        var rightRoot = Path.Combine(_env.Root, "vm_right");
        Directory.CreateDirectory(leftRoot);
        Directory.CreateDirectory(rightRoot);

        CreateFile("vm_left/equal.txt", 10);
        CreateFile("vm_right/equal.txt", 10);

        CreateFile("vm_left/diff.txt", 10);
        CreateFile("vm_right/diff.txt", 20);

        CreateFile("vm_left/left_only.txt", 15);
        CreateFile("vm_right/right_only.txt", 25);

        var vm = new FullCompareViewModel(leftRoot, rightRoot);

        // Aguarda carregar
        while (vm.IsLoading)
        {
            await Task.Delay(20);
        }

        Assert.Equal(4, vm.TotalCount);
        Assert.Equal(1, vm.SameCount);
        Assert.Equal(1, vm.DifferentCount);
        Assert.Equal(1, vm.OnlyLeftCount);
        Assert.Equal(1, vm.OnlyRightCount);

        // Filtro Todas
        vm.Filter = FullCompareFilter.All;
        Assert.Equal(4, vm.FilteredItems.Count);

        // Filtro Só Diferenças
        vm.Filter = FullCompareFilter.OnlyDifferences;
        Assert.Equal(3, vm.FilteredItems.Count);

        // Filtro Só Iguais
        vm.Filter = FullCompareFilter.OnlySame;
        Assert.Single(vm.FilteredItems);
        Assert.Equal("equal.txt", vm.FilteredItems[0].Name);

        // Filtro Só Esquerda
        vm.Filter = FullCompareFilter.OnlyLeft;
        Assert.Single(vm.FilteredItems);
        Assert.Equal("left_only.txt", vm.FilteredItems[0].Name);

        // Filtro Só Direita
        vm.Filter = FullCompareFilter.OnlyRight;
        Assert.Single(vm.FilteredItems);
        Assert.Equal("right_only.txt", vm.FilteredItems[0].Name);

        // Busca textual
        vm.Filter = FullCompareFilter.All;
        vm.SearchText = "diff";
        Assert.Single(vm.FilteredItems);
        Assert.Equal("diff.txt", vm.FilteredItems[0].Name);
    }
}
