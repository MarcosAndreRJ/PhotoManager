namespace PhotoManager.Application.Transfer;

public enum TransferSortField { Name, Date, Size, Type }

/// <summary>Busca (nome/extensão) e ordenação de uma pasta listada; cada painel aplica a sua, sem afetar o outro.</summary>
public static class TransferListing
{
    public static IReadOnlyList<TransferEntry> Apply(IEnumerable<TransferEntry> entries, string? search, TransferSortField field, bool descending, bool foldersFirst)
    {
        var query = entries;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var terms = search.Split([' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // Todos os termos precisam aparecer no nome (que inclui a extensão): "viagem mp4" acha viagem01.mp4.
            query = query.Where(e => terms.All(term => e.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        var name = StringComparer.OrdinalIgnoreCase;
        var natural = Comparer<string>.Create(NaturalCompare);
        IComparer<TransferEntry> comparer = field switch
        {
            TransferSortField.Date => Comparer<TransferEntry>.Create((a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc)),
            TransferSortField.Size => Comparer<TransferEntry>.Create((a, b) => a.Size.CompareTo(b.Size)),
            TransferSortField.Type => Comparer<TransferEntry>.Create((a, b) => name.Compare(a.Extension, b.Extension)),
            _ => Comparer<TransferEntry>.Create((a, b) => natural.Compare(a.Name, b.Name))
        };
        var byName = Comparer<TransferEntry>.Create((a, b) => natural.Compare(a.Name, b.Name));
        var sign = descending ? -1 : 1;
        var list = query.ToList();
        list.Sort((a, b) =>
        {
            if (foldersFirst && a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;   // pastas sempre antes, mesmo em ordem decrescente
            var result = sign * comparer.Compare(a, b);
            return result != 0 ? result : byName.Compare(a, b);                          // desempate estável por nome
        });
        return list;
    }

    /// <summary>Ordem "natural" (IMG2 antes de IMG10), como o Explorer.</summary>
    public static int NaturalCompare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0'); var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var cmp = string.CompareOrdinal(a, b);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (cmp != 0) return cmp;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}

/// <summary>Histórico de navegação de um painel (voltar/avançar) e a pasta superior.</summary>
public sealed class NavigationHistory
{
    private readonly List<string> _entries = [];
    private int _index = -1;

    public string? Current => _index >= 0 ? _entries[_index] : null;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    /// <summary>Visita uma pasta nova (descarta o "avançar"); repetir a pasta atual não cria entrada.</summary>
    public void Visit(string path)
    {
        if (Current is not null && string.Equals(Current, path, StringComparison.OrdinalIgnoreCase)) return;
        if (_index < _entries.Count - 1) _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(path);
        _index = _entries.Count - 1;
    }

    public string? Back() => CanGoBack ? _entries[--_index] : null;
    public string? Forward() => CanGoForward ? _entries[++_index] : null;

    /// <summary>Pasta superior; nulo na raiz (C:\ ou \\servidor\compartilhamento).</summary>
    public static string? Parent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (full.Length == 2 && full[1] == ':') return null;
        var parent = System.IO.Path.GetDirectoryName(full);
        return string.IsNullOrEmpty(parent) ? null : parent;
    }
}

public sealed record BreadcrumbPart(string Label, string Path);

public static class Breadcrumbs
{
    /// <summary>"D:\Fotos\2026" → D:\ › Fotos › 2026 (cada trecho com o caminho para onde leva). Aceita UNC.</summary>
    public static IReadOnlyList<BreadcrumbPart> Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        var full = path.Trim();
        var root = System.IO.Path.GetPathRoot(full) ?? string.Empty;
        var parts = new List<BreadcrumbPart>();
        if (root.Length > 0) parts.Add(new(root.TrimEnd('\\'), root));
        var rest = full[root.Length..].Trim('\\', '/');
        var current = root;
        foreach (var segment in rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            parts.Add(new(segment, current));
        }
        if (parts.Count > 0 && root.Length > 0 && root.StartsWith(@"\\", StringComparison.Ordinal)) parts[0] = new(root.TrimEnd('\\'), root);
        return parts;
    }
}
