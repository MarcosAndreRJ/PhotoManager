using System.Globalization;
using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Transfer;

// ---------- copiar / mover ----------

/// <summary>O que fazer quando o destino já tem um item com o mesmo nome. Pastas com o mesmo nome são sempre MESCLADAS (a regra vale para os arquivos de dentro).</summary>
public enum ConflictPolicy { Replace, Skip, KeepBoth }

public sealed record TransferRequest(IReadOnlyList<string> Sources, string Destination, bool Move, ConflictPolicy Policy = ConflictPolicy.Skip);

public sealed record TransferProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp(BytesDone / (double)BytesTotal, 0, 1) : FilesTotal > 0 ? FilesDone / (double)FilesTotal : 0;
}

/// <param name="Moves">Origem → destino de cada item de topo efetivamente movido (para desfazer).</param>
/// <param name="Created">Itens de topo criados por cópia (para desfazer: vão para a Lixeira).</param>
public sealed record TransferBatchResult(
    IReadOnlyList<(string From, string To)> Moves,
    IReadOnlyList<string> Created,
    int Files,
    int Skipped,
    IReadOnlyList<(string Path, string Error)> Failed,
    bool Cancelled);

public static class TransferConflicts
{
    /// <summary>Itens de topo que já existem no destino (o usuário escolhe a regra uma vez para todos).</summary>
    public static IReadOnlyList<string> Find(IEnumerable<string> sources, string destination, Func<string, bool> exists) =>
        sources.Select(s => Path.Combine(destination, Path.GetFileName(s.TrimEnd('\\'))))
            .Where(exists).ToList();

    /// <summary>
    /// Remove o que não faz sentido transferir: o .xmp que já vai junto com a mídia e pastas soltas dentro delas mesmas.
    /// Mover para a pasta onde o item já está não faz nada; COPIAR para a mesma pasta é permitido (vira "foto (2).jpg", como duplicar no Explorer).
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> sources, string destination, bool move)
    {
        var list = sources.Select(s => s.Length > 3 ? s.TrimEnd('\\') : s).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sidecars = list.Where(XmpSidecar.UsesSidecar).Select(XmpSidecar.PathFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list.Where(s => !sidecars.Contains(s))
            .Where(s => !move || !string.Equals(Path.GetDirectoryName(s), destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .Where(s => !TransferNames.IsSameOrInside(destination, s))
            .ToList();
    }

    /// <summary>"foto.jpg" → "foto (2).jpg", "foto (3).jpg"… o primeiro que não existe.</summary>
    public static string KeepBothName(string folder, string name, Func<string, bool> exists, bool isFolder = false)
    {
        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isFolder ? string.Empty : Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!exists(candidate)) return candidate;
        }
    }
}

// ---------- desfazer ----------

/// <summary>
/// Uma ação desfazível. Desfazer = mover cada <see cref="Moves"/> de volta (Para → De, na ordem inversa), mandar <see cref="Created"/> para a Lixeira
/// e apagar as <see cref="CreatedFolders"/> que ficaram vazias. Excluir (Lixeira) não entra aqui: restaura-se pela Lixeira do Windows.
/// </summary>
public sealed record TransferUndoEntry(string Description, IReadOnlyList<(string From, string To)> Moves, IReadOnlyList<string> Created, IReadOnlyList<string> CreatedFolders)
{
    public static TransferUndoEntry Rename(string from, string to) => new($"renomear “{Path.GetFileName(to)}”", [(from, to)], [], []);
}

/// <summary>Pilha limitada de ações desfazíveis (a mais recente primeiro).</summary>
public sealed class TransferUndoStack(int capacity = 30)
{
    private readonly LinkedList<TransferUndoEntry> _entries = new();
    public int Count => _entries.Count;
    public TransferUndoEntry? Peek => _entries.First?.Value;
    public void Push(TransferUndoEntry entry)
    {
        _entries.AddFirst(entry);
        while (_entries.Count > capacity) _entries.RemoveLast();
    }
    public TransferUndoEntry? Pop()
    {
        var first = _entries.First?.Value;
        if (first is not null) _entries.RemoveFirst();
        return first;
    }
}

// ---------- comparar pastas ----------

public enum CompareState { None, OnlyHere, Same, Different }

/// <summary>Compara duas listagens pelo NOME: arquivo igual = mesmo tamanho e data (tolerância de 2 s, FAT/exFAT); pasta = existe nos dois lados. Não sincroniza nada.</summary>
public static class FolderComparer
{
    public static (IReadOnlyDictionary<string, CompareState> Left, IReadOnlyDictionary<string, CompareState> Right) Compare(IReadOnlyList<TransferEntry> left, IReadOnlyList<TransferEntry> right)
    {
        var rightByName = right.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var leftByName = left.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return (Mark(left, rightByName), Mark(right, leftByName));
    }

    private static Dictionary<string, CompareState> Mark(IReadOnlyList<TransferEntry> side, Dictionary<string, TransferEntry> other)
    {
        var result = new Dictionary<string, CompareState>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in side)
            result[entry.Path] = !other.TryGetValue(entry.Name, out var twin) || twin.IsFolder != entry.IsFolder ? CompareState.OnlyHere
                : entry.IsFolder || (entry.Size == twin.Size && Math.Abs((entry.ModifiedUtc - twin.ModifiedUtc).TotalSeconds) <= 2) ? CompareState.Same
                : CompareState.Different;
        return result;
    }
}

// ---------- organizar por data ----------

public sealed record DateFolderPattern(string Key, string Label, Func<DateTime, string> RelativeFolder);

public static class DateFolderPlanner
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static IReadOnlyList<DateFolderPattern> Patterns { get; } =
    [
        new("y/m", "Ano\\Mês  (2026\\10)", d => Path.Combine(d.ToString("yyyy"), d.ToString("MM"))),
        new("y/m-name", "Ano\\Mês com nome  (2026\\10 - Outubro)", d => Path.Combine(d.ToString("yyyy"), $"{d:MM} - {PtBr.TextInfo.ToTitleCase(d.ToString("MMMM", PtBr))}")),
        new("y/ymd", "Ano\\Dia  (2026\\2026-10-05)", d => Path.Combine(d.ToString("yyyy"), d.ToString("yyyy-MM-dd"))),
        new("ymd", "Dia  (2026-10-05)", d => d.ToString("yyyy-MM-dd")),
        new("ym", "Ano-Mês  (2026-10)", d => d.ToString("yyyy-MM"))
    ];

    /// <summary>Destino de cada arquivo: <paramref name="root"/>\pasta-da-data. Agrupado por pasta para a pré-visualização.</summary>
    public static IReadOnlyList<(string Folder, IReadOnlyList<string> Files)> Plan(IEnumerable<(string Path, DateTime Date)> files, string root, DateFolderPattern pattern) =>
        files.GroupBy(f => Path.Combine(root, pattern.RelativeFolder(f.Date)), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, (IReadOnlyList<string>)g.Select(f => f.Path).ToList()))
            .ToList();
}

// ---------- renomear em lote ----------

public static class BatchRenamePlanner
{
    public const string Help = "{nome} nome atual · {data} 2026-10-05 · {ano} {mes} {dia} · {hora} 14-30-05 · {seq} 001";

    /// <summary>Novo nome de cada arquivo (a extensão é mantida). Erro = template vazio, nome inválido ou dois arquivos com o mesmo nome final.</summary>
    public static (IReadOnlyList<(string From, string To)> Renames, string? Error) Plan(IReadOnlyList<(string Path, DateTime Date)> files, string template, int start, IEnumerable<string> otherNamesInFolder)
    {
        if (string.IsNullOrWhiteSpace(template)) return ([], "Digite um modelo de nome.");
        var digits = Math.Max(3, (start + files.Count - 1).ToString(CultureInfo.InvariantCulture).Length);
        var renames = new List<(string, string)>();
        var taken = new HashSet<string>(otherNamesInFolder, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            var (path, date) = files[i];
            var stem = template
                .Replace("{nome}", Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
                .Replace("{data}", date.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
                .Replace("{ano}", date.ToString("yyyy"), StringComparison.OrdinalIgnoreCase)
                .Replace("{mes}", date.ToString("MM"), StringComparison.OrdinalIgnoreCase)
                .Replace("{dia}", date.ToString("dd"), StringComparison.OrdinalIgnoreCase)
                .Replace("{hora}", date.ToString("HH-mm-ss"), StringComparison.OrdinalIgnoreCase)
                .Replace("{seq}", (start + i).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'), StringComparison.OrdinalIgnoreCase)
                .Trim();
            var name = stem + Path.GetExtension(path);
            if (TransferNames.Validate(name, []) is { } error) return ([], $"{Path.GetFileName(path)}: {error}");
            if (!taken.Add(name)) return ([], $"Dois arquivos ficariam com o nome “{name}”. Use {{seq}} ou {{hora}} no modelo.");
            renames.Add((path, Path.Combine(Path.GetDirectoryName(path)!, name)));
        }
        return (renames, null);
    }
}

// ---------- detalhes ----------

public sealed record TransferItemDetails(IReadOnlyList<(string Label, string Value)> Rows);

// ---------- layout e locais ----------

public sealed record TransferPaneLayout(string? Path, string ViewMode = "Grid", double ThumbnailSize = 132, string SortField = "Name", bool SortDescending = false, bool FoldersFirst = true, bool ShowDetails = false);

public sealed record TransferLayout(TransferPaneLayout Left, TransferPaneLayout Right, double LeftRatio = 0.5, bool ShowPlaces = true, IReadOnlyList<string>? Pinned = null, IReadOnlyList<string>? Recent = null)
{
    public static TransferLayout Default { get; } = new(new TransferPaneLayout(null), new TransferPaneLayout(null));
}

/// <summary>Onde o layout da Transferência é guardado (settings.json no app; memória nos testes).</summary>
public interface ITransferSettings
{
    TransferLayout Load();
    void Save(TransferLayout layout);
}

public static class TransferPlaces
{
    /// <summary>Pasta vai para o topo dos recentes (sem repetir; no máximo <paramref name="max"/>).</summary>
    public static IReadOnlyList<string> AddRecent(IReadOnlyList<string> recent, string path, int max = 10) =>
        recent.Where(p => !string.Equals(p.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).Prepend(path).Take(max).ToList();
}

/// <summary>Data "da foto" para organizar/renomear: EXIF/catálogo quando houver, senão a data de modificação.</summary>
public sealed record CaptureDate(DateTime Value, bool FromMetadata);

/// <summary>Um lote de copiar/mover: estes itens vão para esta pasta (organizar por data gera um lote por pasta de data).</summary>
public sealed record TransferJob(IReadOnlyList<string> Sources, string Destination);
