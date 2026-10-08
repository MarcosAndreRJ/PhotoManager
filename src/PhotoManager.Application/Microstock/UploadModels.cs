using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Microstock;

public sealed record Agency(long Id, string Name, bool IsActive, int Order);

public enum UploadRecordStatus
{
    Pending,
    Uploaded,
    Rejected,
    Error
}

public sealed record UploadRecord(
    long Id,
    long PhotoId,
    long AgencyId,
    DateTime UploadedAt,
    UploadRecordStatus Status,
    int MetadataVersion,
    string? RemoteFileName,
    int RetryCount,
    string? LastError,
    string? Notes,
    string AgencyName,
    bool AgencyIsActive);

public sealed record UploadRecordInput(
    long PhotoId,
    long AgencyId,
    DateTime UploadedAt,
    UploadRecordStatus Status,
    int MetadataVersion,
    string? RemoteFileName,
    int RetryCount,
    string? LastError,
    string? Notes);

public sealed record UploadMarkResult(int RecordsCreated, IReadOnlyList<string> Warnings);

public interface IUploadHistoryRepository
{
    Task<IReadOnlyList<Agency>> GetAgenciesAsync(bool activeOnly = false, CancellationToken cancellationToken = default);
    Task<Agency> SaveAgencyAsync(Agency agency, CancellationToken cancellationToken = default);
    Task SetAgencyActiveAsync(long agencyId, bool active, CancellationToken cancellationToken = default);
    Task SetAgencyOrderAsync(IReadOnlyList<long> agencyIds, CancellationToken cancellationToken = default);
    Task<UploadRecord> AppendRecordAsync(UploadRecordInput input, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UploadRecord>> GetCurrentRecordsAsync(IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UploadRecord>> GetHistoryAsync(long photoId, CancellationToken cancellationToken = default);
}

public interface IUploadHistoryService
{
    Task<IReadOnlyList<Agency>> GetAgenciesAsync(bool activeOnly = false, CancellationToken cancellationToken = default);
    Task<Agency> SaveAgencyAsync(Agency agency, CancellationToken cancellationToken = default);
    Task SetAgencyActiveAsync(long agencyId, bool active, CancellationToken cancellationToken = default);
    Task SetAgencyOrderAsync(IReadOnlyList<long> agencyIds, CancellationToken cancellationToken = default);
    Task<UploadMarkResult> MarkUploadedAsync(IReadOnlyCollection<long> photoIds, IReadOnlyCollection<long> agencyIds, DateTime uploadedAt, string? remoteFileName, string? notes, CancellationToken cancellationToken = default);
    Task<UploadMarkResult> MarkIssueAsync(IReadOnlyCollection<long> photoIds, IReadOnlyCollection<long> agencyIds, UploadRecordStatus status, string reason, DateTime uploadedAt, string? notes, CancellationToken cancellationToken = default);
    Task<UploadMarkResult> UndoMarkingAsync(IReadOnlyCollection<long> photoIds, IReadOnlyCollection<long> agencyIds, DateTime at, string? notes, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UploadRecordSnapshot>> GetCurrentSnapshotsAsync(IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UploadRecord>> GetHistoryAsync(long photoId, CancellationToken cancellationToken = default);
}

public sealed class UploadHistoryService(IUploadHistoryRepository repository, ICatalogRepository catalog) : IUploadHistoryService
{
    private readonly IUploadHistoryRepository _repository = repository;
    private readonly ICatalogRepository _catalog = catalog;

    public Task<IReadOnlyList<Agency>> GetAgenciesAsync(bool activeOnly = false, CancellationToken cancellationToken = default) => _repository.GetAgenciesAsync(activeOnly, cancellationToken);
    public Task<Agency> SaveAgencyAsync(Agency agency, CancellationToken cancellationToken = default) => _repository.SaveAgencyAsync(agency, cancellationToken);
    public Task SetAgencyActiveAsync(long agencyId, bool active, CancellationToken cancellationToken = default) => _repository.SetAgencyActiveAsync(agencyId, active, cancellationToken);
    public Task SetAgencyOrderAsync(IReadOnlyList<long> agencyIds, CancellationToken cancellationToken = default) => _repository.SetAgencyOrderAsync(agencyIds, cancellationToken);

    public async Task<UploadMarkResult> MarkUploadedAsync(IReadOnlyCollection<long> photoIds, IReadOnlyCollection<long> agencyIds, DateTime uploadedAt, string? remoteFileName, string? notes, CancellationToken cancellationToken = default)
    {
        var photos = await _catalog.GetAllAsync(cancellationToken);
        var agencies = await _repository.GetAgenciesAsync(true, cancellationToken);
        return await AppendForSelectionAsync(photoIds, agencyIds, photos, agencies, (photo, agency) => new UploadRecordInput(photo.Id, agency.Id, uploadedAt, UploadRecordStatus.Uploaded, photo.MetadataVersion, Clean(remoteFileName), 0, null, Clean(notes)), cancellationToken);
    }

    public async Task<UploadMarkResult> MarkIssueAsync(IReadOnlyCollection<long> photoIds, IReadOnlyCollection<long> agencyIds, UploadRecordStatus status, string reason, DateTime uploadedAt, string? notes, CancellationToken cancellationToken = default)
    {
        if (status is not (UploadRecordStatus.Error or UploadRecordStatus.Rejected)) throw new ArgumentException("A marcação de problema deve ser erro ou rejeição.", nameof(status));
        var photos = await _catalog.GetAllAsync(cancellationToken);
        var agencies = await _repository.GetAgenciesAsync(true, cancellationToken);
        return await AppendForSelectionAsync(photoIds, agencyIds, photos, agencies, (photo, agency) => new UploadRecordInput(photo.Id, agency.Id, uploadedAt, status, photo.MetadataVersion, null, 0, Clean(reason), Clean(notes)), cancellationToken);
    }

    public async Task<UploadMarkResult> UndoMarkingAsync(IReadOnlyCollection<long> photoIds, IReadOnlyCollection<long> agencyIds, DateTime at, string? notes, CancellationToken cancellationToken = default)
    {
        var photos = await _catalog.GetAllAsync(cancellationToken);
        var agencies = await _repository.GetAgenciesAsync(true, cancellationToken);
        return await AppendForSelectionAsync(photoIds, agencyIds, photos, agencies, (photo, agency) => new UploadRecordInput(photo.Id, agency.Id, at, UploadRecordStatus.Pending, photo.MetadataVersion, null, 0, null, Clean(notes) ?? "Marcação desfeita"), cancellationToken);
    }

    public async Task<IReadOnlyList<UploadRecordSnapshot>> GetCurrentSnapshotsAsync(IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default)
    {
        var records = await _repository.GetCurrentRecordsAsync(photoIds, cancellationToken);
        return records.Select(record => new UploadRecordSnapshot(record.PhotoId, record.AgencyId, record.AgencyName, record.AgencyIsActive, record.Status switch
        {
            UploadRecordStatus.Uploaded => UploadStatusSnapshot.Uploaded,
            UploadRecordStatus.Rejected => UploadStatusSnapshot.Rejected,
            UploadRecordStatus.Error => UploadStatusSnapshot.Error,
            _ => UploadStatusSnapshot.Pending
        }, record.MetadataVersion, record.UploadedAt)).ToList();
    }

    public Task<IReadOnlyList<UploadRecord>> GetHistoryAsync(long photoId, CancellationToken cancellationToken = default) => _repository.GetHistoryAsync(photoId, cancellationToken);

    private async Task<UploadMarkResult> AppendForSelectionAsync(
        IReadOnlyCollection<long> photoIds,
        IReadOnlyCollection<long> agencyIds,
        IReadOnlyList<Photo> photos,
        IReadOnlyList<Agency> agencies,
        Func<Photo, Agency, UploadRecordInput> create,
        CancellationToken cancellationToken)
    {
        var photoMap = photos.ToDictionary(photo => photo.Id);
        var agencyMap = agencies.ToDictionary(agency => agency.Id);
        var warnings = new List<string>();
        var created = 0;
        foreach (var photoId in photoIds.Distinct())
        {
            if (!photoMap.TryGetValue(photoId, out var photo)) { warnings.Add($"Foto {photoId} não encontrada."); continue; }
            foreach (var agencyId in agencyIds.Distinct())
            {
                if (!agencyMap.TryGetValue(agencyId, out var agency)) { warnings.Add($"Banco {agencyId} não encontrado ou inativo."); continue; }
                await _repository.AppendRecordAsync(create(photo, agency), cancellationToken);
                created++;
            }
        }
        return new(created, warnings);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
