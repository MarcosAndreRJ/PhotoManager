using System.Security.Cryptography;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Catalog;

public sealed record DuplicateScanProgress(int Processed, int Total, int Hashed, int Reused, int Failed, string CurrentFile);

public sealed record DuplicateScanResult(
    IReadOnlyList<DuplicateGroup> Groups,
    int CandidateCount,
    int HashedCount,
    int ReusedCount,
    int FailedCount,
    IReadOnlyList<string> Warnings);

public sealed record DuplicateGroup(string Hash, long FileSize, IReadOnlyList<Photo> Photos, bool IsIgnored = false)
{
    public long GroupId => Convert.ToInt64(Hash[..Math.Min(16, Hash.Length)], 16);
}

public interface IFileHashService
{
    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default);
}

public sealed class Sha256FileHashService : IFileHashService
{
    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

public interface IDuplicateRepository
{
    Task SaveHashAsync(long photoId, string hash, long size, DateTime modifiedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> GetIgnoredHashesAsync(CancellationToken cancellationToken = default);
    Task SetIgnoredHashAsync(string hash, bool ignored, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<long>> GetDuplicatePhotoIdsAsync(CancellationToken cancellationToken = default);
}

public interface IDuplicateDetectionService
{
    Task<DuplicateScanResult> ScanAsync(IProgress<DuplicateScanProgress>? progress = null, CancellationToken cancellationToken = default);
    Task IgnoreGroupAsync(string hash, CancellationToken cancellationToken = default);
    Task UnignoreGroupAsync(string hash, CancellationToken cancellationToken = default);
}

public sealed class DuplicateDetectionService(
    ICatalogRepository catalog,
    IDuplicateRepository repository,
    IFileHashService? hashService = null) : IDuplicateDetectionService
{
    private readonly IFileHashService _hashService = hashService ?? new Sha256FileHashService();

    public async Task<DuplicateScanResult> ScanAsync(IProgress<DuplicateScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var photos = await catalog.GetAllAsync(cancellationToken);
        var candidates = photos.Where(photo => !photo.IsMissing && File.Exists(photo.CurrentPath))
            .GroupBy(photo => photo.FileSize)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToList();
        var hashes = new Dictionary<long, string>();
        var warnings = new List<string>();
        var hashed = 0;
        var reused = 0;
        var failed = 0;
        var processed = 0;

        foreach (var photo in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(photo.CurrentPath);
            try
            {
                if (!string.IsNullOrWhiteSpace(photo.ContentHash) && photo.HashedAtSize == info.Length && photo.HashedAtModified == info.LastWriteTimeUtc)
                {
                    hashes[photo.Id] = photo.ContentHash;
                    reused++;
                }
                else
                {
                    var hash = await _hashService.ComputeSha256Async(photo.CurrentPath, cancellationToken);
                    photo.ContentHash = hash;
                    photo.HashedAtSize = info.Length;
                    photo.HashedAtModified = info.LastWriteTimeUtc;
                    await repository.SaveHashAsync(photo.Id, hash, info.Length, info.LastWriteTimeUtc, cancellationToken);
                    hashes[photo.Id] = hash;
                    hashed++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failed++;
                warnings.Add($"{photo.FileName}: {exception.Message}");
            }
            processed++;
            progress?.Report(new(processed, candidates.Count, hashed, reused, failed, photo.FileName));
        }

        var ignored = await repository.GetIgnoredHashesAsync(cancellationToken);
        var groups = hashes.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= 2 && !ignored.Contains(group.Key))
            .Select(group => new DuplicateGroup(group.Key, candidates.First(photo => photo.Id == group.First().Key).FileSize, group.Select(item => candidates.First(photo => photo.Id == item.Key)).OrderBy(photo => photo.CurrentPath, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(group => group.Photos[0].FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new(groups, candidates.Count, hashed, reused, failed, warnings);
    }

    public Task IgnoreGroupAsync(string hash, CancellationToken cancellationToken = default) => repository.SetIgnoredHashAsync(hash, true, cancellationToken);
    public Task UnignoreGroupAsync(string hash, CancellationToken cancellationToken = default) => repository.SetIgnoredHashAsync(hash, false, cancellationToken);
}
