using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Metadata;

/// <summary>
/// Orquestra a edição segura: compara com o arquivo, grava via <see cref="IMetadataWriter"/> (que valida antes de substituir),
/// atualiza o catálogo (tamanho/data) e registra a versão. Se nada mudou, não toca no arquivo nem cria versão.
/// </summary>
public sealed class MetadataEditService(IMetadataReader reader, IMetadataWriter writer, ICatalogRepository catalog, IMetadataVersionRepository versions) : IMetadataEditService
{
    public bool CanEdit(Photo photo) => !photo.IsMissing && writer.CanWrite(photo.CurrentPath);

    public async Task<MetadataSaveResult> SaveAsync(Photo photo, MetadataEdit edit, CancellationToken cancellationToken = default)
    {
        if (!CanEdit(photo)) throw new NotSupportedException("Não é possível gravar metadados neste arquivo (ausente ou formato sem suporte).");
        var wanted = edit.Normalize();
        var current = MetadataEdit.From(await reader.ReadAsync(photo.CurrentPath, cancellationToken));
        var changed = current.ChangedFields(wanted);
        if (changed.Count == 0) return new MetadataSaveResult(false, photo.MetadataVersion, []);

        // Guarda o estado original antes da primeira edição, para o histórico poder mostrar de onde se partiu.
        if (photo.MetadataVersion == 0) await versions.RecordVersionAsync(photo.Id, 0, current, "Original", cancellationToken);

        await writer.WriteAsync(photo.CurrentPath, wanted, cancellationToken);

        var info = new FileInfo(photo.CurrentPath);
        photo.FileSize = info.Length;
        photo.ModifiedAt = info.LastWriteTimeUtc;
        await catalog.UpdateLocationAsync(photo, cancellationToken);

        var version = photo.MetadataVersion + 1;
        await versions.RecordVersionAsync(photo.Id, version, wanted, string.Join(", ", changed), cancellationToken);
        photo.MetadataVersion = version;
        return new MetadataSaveResult(true, version, changed);
    }

    public Task<IReadOnlyList<MetadataVersionEntry>> GetHistoryAsync(long photoId, CancellationToken cancellationToken = default) =>
        versions.GetHistoryAsync(photoId, cancellationToken);
}
