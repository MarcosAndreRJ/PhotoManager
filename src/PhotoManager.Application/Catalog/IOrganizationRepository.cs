using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Catalog;

public interface IOrganizationRepository
{
    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetTagsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetCollectionsAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(long photoId, string? category, string? note, int rating, bool favorite, IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default);
}
