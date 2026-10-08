using PhotoManager.Domain.Collections;

namespace PhotoManager.Application.Collections;

public interface ICollectionRepository
{
    Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Collection?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<Collection> InsertAsync(Collection collection, CancellationToken cancellationToken = default);
    Task UpdateAsync(Collection collection, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Collection>> GetChildrenAsync(long? parentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<long>> GetPhotoIdsInCollectionAsync(long collectionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<long, int>> GetDirectCountsAsync(CancellationToken cancellationToken = default);
    Task<int> GetTotalDistinctInAnyCollectionAsync(CancellationToken cancellationToken = default);
    Task<int> GetTotalPhotosCountAsync(CancellationToken cancellationToken = default);
    Task<int> AddPhotosAsync(long collectionId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default);
    Task<int> RemovePhotosAsync(long collectionId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default);
    Task<MovePhotosResult> MovePhotosAsync(long fromId, long toId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default);
    Task<DeleteResult> DeleteHierarchyAsync(long id, DeleteMode mode, IReadOnlyList<(long Id, string OldName, string NewName)> childRenames, CancellationToken cancellationToken = default);
    Task<bool> HasOrphansAsync(CancellationToken cancellationToken = default);
}
