using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Catalog;

public interface IOrganizationService
{
    Task SaveAsync(Photo photo, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetTagsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetCollectionsAsync(CancellationToken cancellationToken = default);
}

public sealed class OrganizationService(IOrganizationRepository repository) : IOrganizationService
{
    public Task SaveAsync(Photo photo, CancellationToken cancellationToken = default) => repository.SaveAsync(photo.Id, photo.CategoryName, photo.PersonalNote, photo.Rating, photo.IsFavorite, photo.Tags, cancellationToken);
    public Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default) => repository.GetCategoriesAsync(cancellationToken);
    public Task<IReadOnlyList<string>> GetTagsAsync(CancellationToken cancellationToken = default) => repository.GetTagsAsync(cancellationToken);
    public Task<IReadOnlyList<string>> GetCollectionsAsync(CancellationToken cancellationToken = default) => repository.GetCollectionsAsync(cancellationToken);
}
