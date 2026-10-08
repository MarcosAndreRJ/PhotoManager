using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Transfer;

/// <summary>
/// Organização física no módulo Transferência: criar/renomear/excluir pastas e arquivos e marcar cores.
/// O catálogo acompanha: renomear atualiza o caminho dos itens catalogados (nada fica "ausente"), excluir vai para a Lixeira e marca os itens como ausentes,
/// e a cor é a mesma etiqueta da Biblioteca (gravada por item do catálogo — não existe um segundo sistema de cor).
/// </summary>
public interface ITransferOrganizer
{
    /// <summary>Cria a pasta e devolve o caminho completo. Falha (IOException) se o nome for inválido ou já existir.</summary>
    Task<string> CreateFolderAsync(string parentFolder, string name, CancellationToken cancellationToken = default);
    /// <summary>Renomeia pasta ou arquivo (com o .xmp junto) e devolve o novo caminho. Arquivo mantém a extensão original.</summary>
    Task<TransferRenameResult> RenameAsync(string path, string newName, CancellationToken cancellationToken = default);
    /// <summary>Envia pastas (com todo o conteúdo) e arquivos (com o .xmp) para a Lixeira — nunca exclui definitivamente.</summary>
    Task<TransferRecycleResult> RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    /// <summary>Cor dos arquivos catalogados diretamente dentro da pasta (sem subpastas). Arquivo ausente do dicionário = fora do catálogo.</summary>
    Task<IReadOnlyDictionary<string, PhotoColor>> GetColorsAsync(string folder, CancellationToken cancellationToken = default);
    /// <summary>Marca a cor dos arquivos; com <paramref name="addMissing"/>, cataloga antes os que ainda não estão no catálogo.</summary>
    Task<TransferColorResult> SetColorAsync(IReadOnlyList<string> paths, PhotoColor color, bool addMissing, CancellationToken cancellationToken = default);
    /// <summary>Avisa a Biblioteca de que algo mudou no catálogo por aqui (cor, caminhos, itens novos ou ausentes).</summary>
    event EventHandler<TransferCatalogChange>? CatalogChanged;

    /// <summary>
    /// Copia ou move arquivos e pastas (recursivo, .xmp junto) para <see cref="TransferRequest.Destination"/>, com progresso e cancelamento.
    /// A cópia vai para um temporário e só ganha o nome final quando termina (cancelar não deixa arquivo pela metade).
    /// "Substituir" manda o item antigo para a Lixeira antes. Mover: o catálogo segue os arquivos (e as cores de pasta).
    /// </summary>
    Task<TransferBatchResult> TransferAsync(TransferRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Move/renomeia cada item para o caminho exato indicado (usado pelo desfazer e pelo renomear em lote). O catálogo acompanha.</summary>
    Task<IReadOnlyList<(string Path, string Error)>> MovePathsAsync(IReadOnlyList<(string From, string To)> moves, CancellationToken cancellationToken = default);
    /// <summary>Apaga as pastas que estiverem vazias (as mais profundas primeiro). Devolve as que não puderam ser apagadas por terem conteúdo.</summary>
    Task<IReadOnlyList<string>> RemoveEmptyFoldersAsync(IReadOnlyList<string> folders, CancellationToken cancellationToken = default);
    /// <summary>Cor das subpastas diretas.</summary>
    Task<IReadOnlyDictionary<string, PhotoColor>> GetFolderColorsAsync(string parent, CancellationToken cancellationToken = default);
    Task SetFolderColorAsync(IReadOnlyList<string> folders, PhotoColor color, CancellationToken cancellationToken = default);
    /// <summary>Para cada arquivo, outros lugares do catálogo com conteúdo idêntico (mesmo tamanho e mesmo SHA-256). Só calcula hash quando há alguém do mesmo tamanho.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FindDuplicatesAsync(IReadOnlyList<string> files, CancellationToken cancellationToken = default);
    /// <summary>Data da foto/vídeo: catálogo → EXIF → contêiner do vídeo → data de modificação.</summary>
    Task<CaptureDate> GetCaptureDateAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Linhas do painel de detalhes (lidas sob demanda, só do item selecionado).</summary>
    Task<TransferItemDetails> GetDetailsAsync(string path, CancellationToken cancellationToken = default);
}

public sealed record TransferRenameResult(string NewPath, int CatalogItemsUpdated);
public sealed record TransferRecycleResult(IReadOnlyList<string> Recycled, IReadOnlyList<(string Path, string Error)> Failed);
public sealed record TransferColorResult(int Colored, int Added, int Skipped);

/// <summary><see cref="Color"/> preenchido = só cores mudaram (a Biblioteca atualiza no lugar); nulo = estrutura mudou (recarregar).</summary>
public sealed record TransferCatalogChange(IReadOnlyList<string> Paths, PhotoColor? Color);

/// <summary>Regras de nome de pasta/arquivo do Windows, sem tocar no disco (testáveis).</summary>
public static class TransferNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Mensagem de erro, ou nulo se o nome serve. <paramref name="siblings"/> são os nomes que já existem na pasta; <paramref name="current"/> é o nome atual (renomear).</summary>
    public static string? Validate(string? name, IEnumerable<string> siblings, string? current = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return "Digite um nome.";
        if (trimmed.Length > 200) return "O nome é longo demais (máximo 200 caracteres).";
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return "O nome não pode conter \\ / : * ? \" < > |";
        if (trimmed is "." or ".." || trimmed.EndsWith('.')) return "O nome não pode terminar com ponto.";
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(trimmed))) return "Este nome é reservado pelo Windows.";
        if (current is not null && string.Equals(trimmed, current, StringComparison.Ordinal)) return null;     // nada mudou: confirmar só fecha
        var isCaseOnlyChange = current is not null && string.Equals(trimmed, current, StringComparison.OrdinalIgnoreCase);
        if (!isCaseOnlyChange && siblings.Any(s => string.Equals(s, trimmed, StringComparison.OrdinalIgnoreCase))) return "Já existe um item com este nome nesta pasta.";
        return null;
    }

    /// <summary>"Nova pasta", "Nova pasta (2)", "Nova pasta (3)"… — o primeiro que não existe.</summary>
    public static string Unique(IEnumerable<string> existing, string baseName = "Nova pasta")
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{baseName} ({i})")) return $"{baseName} ({i})";
    }

    /// <summary>Novo nome de arquivo: a extensão original é sempre mantida (evita "perder" o tipo do arquivo por engano).</summary>
    public static string WithOriginalExtension(string newName, string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);
        newName = newName.Trim();
        return extension.Length > 0 && newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? newName[..^extension.Length] + extension : newName + extension;
    }

    /// <summary>Se <paramref name="path"/> é <paramref name="folder"/> ou está dentro dela.</summary>
    public static bool IsSameOrInside(string? path, string? folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
        var p = path.TrimEnd('\\', '/'); var f = folder.TrimEnd('\\', '/');
        return string.Equals(p, f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Troca o começo <paramref name="oldRoot"/> de <paramref name="path"/> por <paramref name="newRoot"/> (pasta renomeada).</summary>
    public static string Rebase(string path, string oldRoot, string newRoot)
    {
        var p = path.TrimEnd('\\'); var o = oldRoot.TrimEnd('\\');
        return string.Equals(p, o, StringComparison.OrdinalIgnoreCase) ? newRoot : newRoot.TrimEnd('\\') + p[o.Length..];
    }
}
