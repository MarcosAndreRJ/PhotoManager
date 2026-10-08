using Microsoft.Data.Sqlite;
using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;
using PhotoManager.Persistence;

namespace PhotoManager.Tests;

public sealed class DuplicateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoManagerTests", Guid.NewGuid().ToString("N"));
    private readonly SqliteCatalogRepository _repository;

    public DuplicateTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteCatalogRepository(Path.Combine(_root, "data", "photomanager.db"));
        _repository.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Scan_HashesOnlyRepeatedSizes_AndGroupsIdenticalFiles()
    {
        var first = await AddFileAsync("a.jpg", [1, 2, 3]);
        var copy = await AddFileAsync("nested\\b.jpg", [1, 2, 3]);
        await AddFileAsync("different.jpg", [1, 2, 3, 4]);
        var hasher = new CountingHashService();
        var service = new DuplicateDetectionService(_repository, (IDuplicateRepository)_repository, hasher);

        var result = await service.ScanAsync();

        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(2, result.HashedCount);
        Assert.Equal(2, hasher.Paths.Count);
        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Photos.Count);
        Assert.Contains(first.FileName, group.Photos.Select(photo => photo.FileName));
        Assert.Contains(copy.FileName, group.Photos.Select(photo => photo.FileName));
    }

    [Fact]
    public async Task Scan_DifferentContentWithSameSizeDoesNotGroup_AndReadFailureDoesNotStopOthers()
    {
        await AddFileAsync("bad.jpg", [1, 1, 1]);
        await AddFileAsync("bad-copy.jpg", [2, 2, 2]);
        await AddFileAsync("good-a.jpg", [4, 5, 6, 7]);
        await AddFileAsync("good-b.jpg", [4, 5, 6, 7]);
        var hasher = new CountingHashService(path => path.EndsWith("bad.jpg", StringComparison.OrdinalIgnoreCase) ? new IOException("leitura simulada") : null);
        var result = await new DuplicateDetectionService(_repository, (IDuplicateRepository)_repository, hasher).ScanAsync();

        Assert.Equal(4, result.CandidateCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Single(result.Groups);
        Assert.Equal(2, result.Groups[0].Photos.Count);
    }

    [Fact]
    public async Task Scan_ReusesValidHash_AndInvalidatesItWhenFileChanges()
    {
        var first = await AddFileAsync("a.jpg", [1, 2, 3]);
        await AddFileAsync("b.jpg", [1, 2, 3]);
        var hasher = new CountingHashService();
        var service = new DuplicateDetectionService(_repository, (IDuplicateRepository)_repository, hasher);

        var initial = await service.ScanAsync();
        var reused = await service.ScanAsync();
        File.WriteAllBytes(first.CurrentPath, [9, 9, 9]);
        File.SetLastWriteTimeUtc(first.CurrentPath, DateTime.UtcNow.AddMinutes(1));
        var changed = await service.ScanAsync();

        Assert.Equal(2, initial.HashedCount);
        Assert.Equal(2, reused.ReusedCount);
        Assert.Equal(1, changed.HashedCount);
        Assert.Empty(changed.Groups);
    }

    [Fact]
    public async Task IgnoredHash_IsPersistedAndRemovedFromResultsUntilUnignored()
    {
        await AddFileAsync("a.jpg", [8, 8]);
        await AddFileAsync("b.jpg", [8, 8]);
        var service = new DuplicateDetectionService(_repository, (IDuplicateRepository)_repository, new CountingHashService());
        var group = Assert.Single((await service.ScanAsync()).Groups);

        await service.IgnoreGroupAsync(group.Hash);
        Assert.Empty((await service.ScanAsync()).Groups);
        await service.UnignoreGroupAsync(group.Hash);
        Assert.Single((await service.ScanAsync()).Groups);
    }

    [Fact]
    public async Task CancelledScanCanBeResumed()
    {
        await AddFileAsync("a.jpg", [3, 3, 3]);
        await AddFileAsync("b.jpg", [3, 3, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new DuplicateDetectionService(_repository, (IDuplicateRepository)_repository, new CountingHashService());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanAsync(cancellationToken: cancellation.Token));
        Assert.Single((await service.ScanAsync()).Groups);
    }

    private async Task<Photo> AddFileAsync(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(_root, "photos", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        var info = new FileInfo(path);
        var photo = new Photo { FileName = info.Name, CurrentPath = info.FullName, Extension = info.Extension.ToLowerInvariant(), FileSize = info.Length, CreatedAt = info.CreationTimeUtc, ModifiedAt = info.LastWriteTimeUtc, ImportedAt = DateTime.UtcNow };
        await _repository.AddAsync(photo);
        return photo;
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class CountingHashService(Func<string, Exception?>? failure = null) : IFileHashService
    {
        private readonly Func<string, Exception?>? _failure = failure;
        public List<string> Paths { get; } = [];
        public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
        {
            Paths.Add(path);
            if (_failure?.Invoke(path) is { } exception) throw exception;
            await using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken));
        }
    }
}
