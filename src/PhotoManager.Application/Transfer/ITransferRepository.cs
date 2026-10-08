using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Transfer;

/// <summary>Dados do catálogo usados só pela Transferência: cor de pastas e candidatos a duplicata por tamanho.</summary>
public interface ITransferRepository
{
    /// <summary>Cor das subpastas diretas de <paramref name="parent"/> (caminho completo → cor).</summary>
    Task<IReadOnlyDictionary<string, PhotoColor>> GetFolderColorsAsync(string parent, CancellationToken cancellationToken = default);
    /// <summary>Marca as pastas; <see cref="PhotoColor.None"/> remove a marcação.</summary>
    Task SetFolderColorAsync(IReadOnlyList<string> folders, PhotoColor color, CancellationToken cancellationToken = default);
    /// <summary>A pasta (e as subpastas marcadas dentro dela) mudou de caminho: as cores vão junto.</summary>
    Task RebaseFolderColorsAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);
    /// <summary>Esquece a cor da pasta e das subpastas (pasta excluída).</summary>
    Task RemoveFolderColorsAsync(IReadOnlyList<string> folders, CancellationToken cancellationToken = default);
    /// <summary>Itens presentes no catálogo com algum destes tamanhos (o primeiro filtro barato de duplicata).</summary>
    Task<IReadOnlyList<CatalogSizeMatch>> GetSizeMatchesAsync(IReadOnlyCollection<long> sizes, CancellationToken cancellationToken = default);
}

/// <summary><see cref="ContentHash"/> só vale se <see cref="HashedAtSize"/> = <see cref="Size"/> (o arquivo não mudou desde o cálculo).</summary>
public sealed record CatalogSizeMatch(long Id, string Path, long Size, string? ContentHash, long? HashedAtSize);
