using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Catalog;

public interface IFileOperationService
{
    Task MoveAsync(Photo photo, string destinationFolder, CancellationToken cancellationToken = default);
    Task<Photo?> CopyAsync(Photo photo, string destinationFolder, bool addCopyToCatalog, CancellationToken cancellationToken = default);
    Task RenameAsync(Photo photo, string newFileName, CancellationToken cancellationToken = default);
    Task MoveToRecycleBinAsync(Photo photo, CancellationToken cancellationToken = default);
    Task RenameBatchAsync(IReadOnlyList<Photo> photos, string template, CancellationToken cancellationToken = default);
    /// <summary>Copia ou move arquivos de FORA do catálogo (soltos do Explorer) para uma pasta, levando o sidecar .xmp junto. Nunca sobrescreve: nome já existente vira "ignorado".</summary>
    Task<ExternalTransferResult> TransferExternalAsync(IReadOnlyList<string> sources, string destinationFolder, bool move, CancellationToken cancellationToken = default);
}

public sealed record ExternalTransferResult(IReadOnlyList<string> Created, IReadOnlyList<string> SkippedExisting, IReadOnlyList<string> Failed);
