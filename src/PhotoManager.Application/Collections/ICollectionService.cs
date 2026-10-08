using PhotoManager.Domain.Collections;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Collections;

public interface ICollectionService
{
    Task<CollectionTree> GetTreeAsync(CancellationToken cancellationToken = default);
    Task<Collection> CreateAsync(string name, long? parentId, CancellationToken cancellationToken = default);
    Task<Collection> RenameAsync(long id, string newName, CancellationToken cancellationToken = default);
    Task<MoveResult> MoveAsync(long id, long? newParentId, CancellationToken cancellationToken = default);
    Task<DeleteResult> DeleteAsync(long id, DeleteMode mode, CancellationToken cancellationToken = default);
    Task<int> AddPhotosAsync(long collectionId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default);
    Task<int> RemovePhotosAsync(long collectionId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default);
    Task<MovePhotosResult> MovePhotosAsync(long fromId, long toId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<long>> GetAncestorsAsync(long id, CancellationToken cancellationToken = default);
    Task<bool> IsDescendantAsync(long id, long possibleAncestorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Collection?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<bool> HasOrphansAsync(CancellationToken cancellationToken = default);
    Task SyncPhotoCollectionsAsync(IEnumerable<Photo> photos, CancellationToken cancellationToken = default);
}
