using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Catalog;

public interface ICatalogRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Photo>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Photo?> FindByPathAsync(string path, CancellationToken cancellationToken = default);
    Task<long> AddAsync(Photo photo, CancellationToken cancellationToken = default);
    Task UpdateMissingStatesAsync(CancellationToken cancellationToken = default);
    Task UpdateLocationAsync(Photo photo, CancellationToken cancellationToken = default);
    /// <summary>Grava largura, altura, duração e revisão das informações de mídia.</summary>
    Task UpdateMediaInfoAsync(Photo photo, CancellationToken cancellationToken = default);
    Task UpdateUserRotationAsync(long photoId, int degrees, CancellationToken cancellationToken = default);
    Task UpdateColorLabelAsync(IReadOnlyCollection<long> photoIds, PhotoColor color, CancellationToken cancellationToken = default);
    /// <summary>Grava o resultado de examinar o arquivo atrás de GPS (e o nome do local, se já buscado).</summary>
    Task UpdateGpsAsync(long photoId, double? latitude, double? longitude, string? placeName, bool gpsChecked, CancellationToken cancellationToken = default);
    /// <summary>Apaga as linhas do catálogo (e, por cascata, tags, coleções, histórico de metadados e registros de upload delas). Nunca toca em arquivos.</summary>
    Task<int> DeletePhotosAsync(IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default);
    /// <summary>Consulta leve (Id, caminho, cor) dos itens dentro da pasta — só os filhos diretos ou, com <paramref name="recursive"/>, todas as subpastas.</summary>
    Task<IReadOnlyList<CatalogPathEntry>> GetEntriesUnderAsync(string folder, bool recursive, CancellationToken cancellationToken = default);
    /// <summary>Grava o novo caminho (e nome) de itens cujo arquivo/pasta foi renomeado; eles voltam a constar como presentes.</summary>
    Task UpdatePathsAsync(IReadOnlyList<(long Id, string NewPath)> moves, CancellationToken cancellationToken = default);
}

public sealed record CatalogPathEntry(long Id, string Path, PhotoColor ColorLabel);
