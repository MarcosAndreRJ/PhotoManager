namespace PhotoManager.Application.Transfer;

/// <summary>Miniatura de um arquivo pelo caminho (inclusive fora do catálogo); o cache vale enquanto tamanho e data do arquivo não mudam.</summary>
public interface IFileThumbnailService
{
    Task<string?> GetOrCreateForFileAsync(string sourcePath, CancellationToken cancellationToken = default);
}
