using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Catalog;

public interface ICatalogService
{
    Task<IReadOnlyList<Photo>> GetPhotosAsync(PhotoFilter? filter = null, CancellationToken cancellationToken = default);
    Task<CatalogImportResult> ImportFolderAsync(string folderPath, IProgress<CatalogProgress>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Cataloga arquivos e/ou pastas no lugar em que estão (usado ao soltar do Explorer).</summary>
    Task<CatalogImportResult> ImportPathsAsync(IReadOnlyList<string> paths, IProgress<CatalogProgress>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>
    /// Relê dimensões/duração/rotação dos itens ainda não conferidos com a regra atual (rotação EXIF de JPEG e do contêiner de vídeo).
    /// Devolve os itens cuja <b>miniatura em cache precisa ser refeita</b> (estava deitada/de cabeça para baixo).
    /// </summary>
    Task<IReadOnlyList<Photo>> RefreshMediaInfoAsync(IReadOnlyList<Photo> photos, CancellationToken cancellationToken = default);
    /// <summary>Grava o giro manual (0/90/180/270) de cada item.</summary>
    Task SetUserRotationAsync(IReadOnlyList<Photo> photos, int degrees, CancellationToken cancellationToken = default);
    /// <summary>Aplica a etiqueta de cor (ou remove, com None) aos itens.</summary>
    Task SetColorLabelAsync(IReadOnlyList<Photo> photos, PhotoColor color, CancellationToken cancellationToken = default);
    /// <summary>Remove do catálogo SOMENTE itens cujo arquivo realmente não existe mais (reconfere no disco). Devolve os Ids removidos.</summary>
    Task<IReadOnlyList<long>> RemoveMissingAsync(IReadOnlyList<Photo> photos, CancellationToken cancellationToken = default);
}

public sealed record PhotoFilter(string? Search = null, string? Category = null, string? Tag = null, string? Collection = null, bool FavoritesOnly = false, int? MinimumRating = null, long? CollectionId = null)
{
    public bool Matches(Photo photo) =>
        (string.IsNullOrWhiteSpace(Search) || photo.FileName.Contains(Search, StringComparison.OrdinalIgnoreCase) || photo.PersonalNote?.Contains(Search, StringComparison.OrdinalIgnoreCase) == true || photo.PlaceName?.Contains(Search, StringComparison.OrdinalIgnoreCase) == true) &&
        (string.IsNullOrWhiteSpace(Category) || string.Equals(photo.CategoryName, Category, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(Tag) || photo.Tags.Any(tag => string.Equals(tag, Tag, StringComparison.OrdinalIgnoreCase))) &&
        (CollectionId.HasValue ? photo.CollectionIds.Contains(CollectionId.Value) : (string.IsNullOrWhiteSpace(Collection) || photo.Collections.Any(collection => string.Equals(collection, Collection, StringComparison.OrdinalIgnoreCase)))) &&
        (!FavoritesOnly || photo.IsFavorite) &&
        (!MinimumRating.HasValue || photo.Rating >= MinimumRating.Value);
}

public interface IThumbnailService
{
    Task<string?> GetOrCreateAsync(long photoId, string sourcePath, CancellationToken cancellationToken = default);
    /// <summary>Apaga a miniatura em cache (melhor esforço).</summary>
    void Remove(long photoId);
}

public interface IPhotoInfoReader
{
    Task<(int Width, int Height)> ReadDimensionsAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Largura, altura, duração e data de criação de um vídeo (nulos quando o contêiner não permite ler).</summary>
    Task<MediaInfo> ReadVideoInfoAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Orientação EXIF 1-8 de um JPEG (1 = normal ou desconhecida).</summary>
    Task<int> ReadExifOrientationAsync(string path, CancellationToken cancellationToken = default);
}

public sealed record CatalogProgress(int Processed, int Imported, int Failed, string CurrentFile);
public sealed record CatalogImportResult(int Scanned, int Imported, int Failed);

public sealed record MediaInfo(int? Width, int? Height, double? DurationSeconds, DateTime? CreatedUtc, int RotationDegrees = 0);
