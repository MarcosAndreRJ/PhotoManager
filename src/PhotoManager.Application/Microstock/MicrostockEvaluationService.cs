using PhotoManager.Application.Metadata;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Microstock;

public sealed record MicrostockPhotoEvaluation(
    Photo Photo,
    PhotoMetadata Metadata,
    MetadataValidationResult Validation,
    MicrostockPreparationStatus Status,
    IReadOnlyList<UploadRecordSnapshot>? Uploads = null);

public interface IMicrostockEvaluationService
{
    Task<IReadOnlyList<MicrostockPhotoEvaluation>> EvaluateAsync(
        IReadOnlyList<Photo> photos,
        ValidationProfile profile,
        IReadOnlyCollection<UploadRecordSnapshot>? uploads = null,
        IReadOnlyCollection<long>? activeAgencyIds = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Lê os metadados fora da UI com concorrência limitada e cache por versão/tamanho/data.
/// O cache é deliberadamente de sessão: o banco não recebe uma cópia potencialmente obsoleta dos metadados.
/// </summary>
public sealed class MicrostockEvaluationService(IMetadataReader reader) : IMicrostockEvaluationService
{
    private readonly Dictionary<MetadataCacheKey, PhotoMetadata> _metadataCache = [];
    private readonly object _cacheGate = new();

    public async Task<IReadOnlyList<MicrostockPhotoEvaluation>> EvaluateAsync(
        IReadOnlyList<Photo> photos,
        ValidationProfile profile,
        IReadOnlyCollection<UploadRecordSnapshot>? uploads = null,
        IReadOnlyCollection<long>? activeAgencyIds = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (photos.Count == 0) return [];
        var completed = 0;
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = photos.Select(async photo =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var metadata = await ReadCachedAsync(photo, cancellationToken);
                var validation = ValidationProfileValidator.Validate(photo, metadata, profile);
                var photoUploads = uploads?.Where(upload => upload.PhotoId == photo.Id).ToList() ?? [];
                var status = PreparationStatusCalculator.Calculate(photo, validation, photoUploads, activeAgencyIds);
                return new MicrostockPhotoEvaluation(photo, metadata, validation, status, photoUploads);
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref completed));
            }
        }).ToArray();
        return await Task.WhenAll(tasks);
    }

    public void Invalidate(long photoId)
    {
        lock (_cacheGate)
            foreach (var key in _metadataCache.Keys.Where(key => key.PhotoId == photoId).ToList()) _metadataCache.Remove(key);
    }

    private async Task<PhotoMetadata> ReadCachedAsync(Photo photo, CancellationToken cancellationToken)
    {
        var key = new MetadataCacheKey(photo.Id, photo.MetadataVersion, photo.FileSize, photo.ModifiedAt);
        lock (_cacheGate) if (_metadataCache.TryGetValue(key, out var cached)) return cached;
        PhotoMetadata metadata;
        if (photo.IsMissing || !File.Exists(photo.CurrentPath))
            metadata = new PhotoMetadata { Error = "Arquivo ausente." };
        else
        {
            try { metadata = await reader.ReadAsync(photo.CurrentPath, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { metadata = new PhotoMetadata { Error = ex.Message }; }
        }
        lock (_cacheGate) _metadataCache[key] = metadata;
        return metadata;
    }

    private readonly record struct MetadataCacheKey(long PhotoId, int MetadataVersion, long FileSize, DateTime ModifiedAt);
}
