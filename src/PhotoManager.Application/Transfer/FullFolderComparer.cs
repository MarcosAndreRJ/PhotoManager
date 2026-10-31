using System.IO;

namespace PhotoManager.Application.Transfer;

public enum FullCompareStatus
{
    Same,
    Different,
    OnlyLeft,
    OnlyRight
}

public sealed record FullCompareItem(
    string RelativePath,
    string Name,
    bool IsFolder,
    FullCompareStatus Status,
    bool ExistsLeft,
    long? SizeLeft,
    DateTime? ModifiedUtcLeft,
    bool ExistsRight,
    long? SizeRight,
    DateTime? ModifiedUtcRight,
    string Details
);

public sealed record FullCompareSummary(
    string FolderLeft,
    string FolderRight,
    int TotalItems,
    int SameCount,
    int DifferentCount,
    int OnlyLeftCount,
    int OnlyRightCount,
    long TotalBytesLeft,
    long TotalBytesRight
);

public sealed class FullCompareResult
{
    public required FullCompareSummary Summary { get; init; }
    public required IReadOnlyList<FullCompareItem> Items { get; init; }
}

/// <summary>
/// Compara recursivamente todos os itens de duas pastas até o último nível.
/// </summary>
public static class FullFolderComparer
{
    private sealed record EntryInfo(
        string RelativePath,
        string Name,
        bool IsFolder,
        long Size,
        DateTime ModifiedUtc
    );

    public static FullCompareResult Compare(
        string folderLeft,
        string folderRight,
        CancellationToken cancellationToken = default)
    {
        var leftExists = Directory.Exists(folderLeft);
        var rightExists = Directory.Exists(folderRight);

        if (!leftExists && !rightExists)
        {
            return new FullCompareResult
            {
                Summary = new FullCompareSummary(folderLeft, folderRight, 0, 0, 0, 0, 0, 0, 0),
                Items = []
            };
        }

        var leftEntries = leftExists
            ? ScanDirectory(folderLeft, cancellationToken)
            : new Dictionary<string, EntryInfo>(StringComparer.OrdinalIgnoreCase);

        var rightEntries = rightExists
            ? ScanDirectory(folderRight, cancellationToken)
            : new Dictionary<string, EntryInfo>(StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();

        // Une todas as chaves relativas
        var allKeys = new HashSet<string>(leftEntries.Keys, StringComparer.OrdinalIgnoreCase);
        allKeys.UnionWith(rightEntries.Keys);

        // Primeiro, resolve os arquivos para saber se subitens de pastas diferem
        var fileStatusMap = new Dictionary<string, FullCompareStatus>(StringComparer.OrdinalIgnoreCase);
        var items = new List<FullCompareItem>(allKeys.Count);

        var totalBytesLeft = 0L;
        var totalBytesRight = 0L;

        // Processa arquivos
        foreach (var relPath in allKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasLeft = leftEntries.TryGetValue(relPath, out var leftInfo);
            var hasRight = rightEntries.TryGetValue(relPath, out var rightInfo);

            var isFolder = (hasLeft && leftInfo!.IsFolder) || (hasRight && rightInfo!.IsFolder);
            if (isFolder) continue;

            if (hasLeft) totalBytesLeft += leftInfo!.Size;
            if (hasRight) totalBytesRight += rightInfo!.Size;

            var name = hasLeft ? leftInfo!.Name : rightInfo!.Name;

            FullCompareStatus status;
            string details;

            if (hasLeft && hasRight)
            {
                var sizeSame = leftInfo!.Size == rightInfo!.Size;
                var dateSame = Math.Abs((leftInfo.ModifiedUtc - rightInfo.ModifiedUtc).TotalSeconds) <= 2;

                if (sizeSame && dateSame)
                {
                    status = FullCompareStatus.Same;
                    details = "Idêntico (mesmo tamanho e data)";
                }
                else
                {
                    status = FullCompareStatus.Different;
                    if (!sizeSame && !dateSame) details = "Tamanho e data diferentes";
                    else if (!sizeSame) details = "Tamanho diferente";
                    else details = "Data diferente";
                }
            }
            else if (hasLeft)
            {
                status = FullCompareStatus.OnlyLeft;
                details = "Existe apenas no painel esquerdo";
            }
            else
            {
                status = FullCompareStatus.OnlyRight;
                details = "Existe apenas no painel direito";
            }

            fileStatusMap[relPath] = status;

            items.Add(new FullCompareItem(
                RelativePath: relPath,
                Name: name,
                IsFolder: false,
                Status: status,
                ExistsLeft: hasLeft,
                SizeLeft: hasLeft ? leftInfo!.Size : null,
                ModifiedUtcLeft: hasLeft ? leftInfo!.ModifiedUtc : null,
                ExistsRight: hasRight,
                SizeRight: hasRight ? rightInfo!.Size : null,
                ModifiedUtcRight: hasRight ? rightInfo!.ModifiedUtc : null,
                Details: details
            ));
        }

        // Agora processa pastas (uma pasta é 'Same' se todos os itens dentro dela forem 'Same')
        foreach (var relPath in allKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasLeft = leftEntries.TryGetValue(relPath, out var leftInfo);
            var hasRight = rightEntries.TryGetValue(relPath, out var rightInfo);

            var isFolder = (hasLeft && leftInfo!.IsFolder) || (hasRight && rightInfo!.IsFolder);
            if (!isFolder) continue;

            var name = hasLeft ? leftInfo!.Name : rightInfo!.Name;

            FullCompareStatus status;
            string details;

            if (hasLeft && hasRight)
            {
                // Verifica se há algum arquivo ou subpasta interna com status diferente de Same
                var prefix = relPath + Path.DirectorySeparatorChar;
                var altPrefix = relPath + "/";

                var hasDifferences = fileStatusMap.Any(kvp =>
                    (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                     kvp.Key.StartsWith(altPrefix, StringComparison.OrdinalIgnoreCase)) &&
                    kvp.Value != FullCompareStatus.Same);

                if (!hasDifferences)
                {
                    // Também verifica se existem subpastas que só existem de um lado
                    var hasMismatchedSubdirs = allKeys.Any(k =>
                        (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                         k.StartsWith(altPrefix, StringComparison.OrdinalIgnoreCase)) &&
                        (!leftEntries.ContainsKey(k) || !rightEntries.ContainsKey(k)));

                    hasDifferences = hasMismatchedSubdirs;
                }

                if (!hasDifferences)
                {
                    status = FullCompareStatus.Same;
                    details = "Conteúdo idêntico até o último nível";
                }
                else
                {
                    status = FullCompareStatus.Different;
                    details = "Conteúdo interno possui diferenças";
                }
            }
            else if (hasLeft)
            {
                status = FullCompareStatus.OnlyLeft;
                details = "Pasta existe apenas no painel esquerdo";
            }
            else
            {
                status = FullCompareStatus.OnlyRight;
                details = "Pasta existe apenas no painel direito";
            }

            items.Add(new FullCompareItem(
                RelativePath: relPath,
                Name: name,
                IsFolder: true,
                Status: status,
                ExistsLeft: hasLeft,
                SizeLeft: null,
                ModifiedUtcLeft: hasLeft ? leftInfo!.ModifiedUtc : null,
                ExistsRight: hasRight,
                SizeRight: null,
                ModifiedUtcRight: hasRight ? rightInfo!.ModifiedUtc : null,
                Details: details
            ));
        }

        // Ordena por caminho relativo (pastas e arquivos em ordem natural)
        items.Sort((a, b) =>
        {
            var folderComp = (b.IsFolder ? 1 : 0).CompareTo(a.IsFolder ? 1 : 0);
            return folderComp != 0 ? folderComp : string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
        });

        var sameCount = items.Count(i => i.Status == FullCompareStatus.Same);
        var diffCount = items.Count(i => i.Status == FullCompareStatus.Different);
        var onlyLeftCount = items.Count(i => i.Status == FullCompareStatus.OnlyLeft);
        var onlyRightCount = items.Count(i => i.Status == FullCompareStatus.OnlyRight);

        var summary = new FullCompareSummary(
            FolderLeft: folderLeft,
            FolderRight: folderRight,
            TotalItems: items.Count,
            SameCount: sameCount,
            DifferentCount: diffCount,
            OnlyLeftCount: onlyLeftCount,
            OnlyRightCount: onlyRightCount,
            TotalBytesLeft: totalBytesLeft,
            TotalBytesRight: totalBytesRight
        );

        return new FullCompareResult
        {
            Summary = summary,
            Items = items
        };
    }

    private static Dictionary<string, EntryInfo> ScanDirectory(string rootPath, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, EntryInfo>(StringComparer.OrdinalIgnoreCase);
        var rootDir = new DirectoryInfo(rootPath);
        if (!rootDir.Exists) return result;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
            RecurseSubdirectories = true
        };

        // Escaneia subdiretórios
        try
        {
            foreach (var dir in rootDir.EnumerateDirectories("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(rootPath, dir.FullName);
                if (string.IsNullOrEmpty(rel) || rel == ".") continue;
                result[rel] = new EntryInfo(rel, dir.Name, true, 0, dir.LastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }

        // Escaneia arquivos
        try
        {
            foreach (var file in rootDir.EnumerateFiles("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(rootPath, file.FullName);
                if (string.IsNullOrEmpty(rel) || rel == ".") continue;
                result[rel] = new EntryInfo(rel, file.Name, false, file.Length, file.LastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }

        return result;
    }
}
