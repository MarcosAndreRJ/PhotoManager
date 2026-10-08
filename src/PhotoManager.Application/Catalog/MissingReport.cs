using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Catalog;

/// <summary>Um item do relatório "não encontrados": uma pasta que sumiu (com tudo dentro) ou os arquivos ausentes de uma pasta que ainda existe.</summary>
/// <param name="FolderExists">Falso: a pasta (ou o disco) não existe/está inacessível. Verdadeiro: a pasta existe, mas alguns arquivos dela não.</param>
public sealed record MissingEntry(string Folder, bool FolderExists, int FileCount, int SubfolderCount, IReadOnlyList<long> PhotoIds)
{
    public string Title => FolderExists ? Folder : $"Pasta não encontrada: {Folder}";
    public string Detail => FolderExists
        ? $"{FileCount} arquivo(s) ausente(s) nesta pasta"
        : SubfolderCount > 0 ? $"{FileCount} arquivo(s) no catálogo, em {SubfolderCount + 1} pasta(s) (esta e subpastas)" : $"{FileCount} arquivo(s) no catálogo";
}

public static class MissingReport
{
    /// <summary>
    /// Agrupa os itens ausentes por pasta. Pastas que não existem são unidas na mais alta que também não existe
    /// (uma pasta sumida com 30 subpastas vira um item só), em vez de listar subpasta por subpasta.
    /// </summary>
    public static IReadOnlyList<MissingEntry> Build(IEnumerable<Photo> missing, Func<string, bool> directoryExists)
    {
        var exists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Exists(string path) => exists.TryGetValue(path, out var known) ? known : exists[path] = directoryExists(path);

        var byDirectory = missing
            .Where(photo => !string.IsNullOrEmpty(Path.GetDirectoryName(photo.CurrentPath)))
            .GroupBy(photo => Path.GetDirectoryName(photo.CurrentPath)!, StringComparer.OrdinalIgnoreCase);

        var groups = new Dictionary<string, (bool Exists, HashSet<string> Directories, List<long> Ids)>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in byDirectory)
        {
            var existsHere = Exists(directory.Key);
            var top = directory.Key;
            if (!existsHere)
                while (Path.GetDirectoryName(top) is { Length: > 0 } parent && !Exists(parent)) top = parent;

            if (!groups.TryGetValue(top, out var group)) groups[top] = group = (existsHere, new HashSet<string>(StringComparer.OrdinalIgnoreCase), []);
            group.Directories.Add(directory.Key);
            group.Ids.AddRange(directory.Select(photo => photo.Id));
        }

        return groups
            .Select(pair => new MissingEntry(pair.Key, pair.Value.Exists, pair.Value.Ids.Count, pair.Value.Directories.Count(d => !string.Equals(d, pair.Key, StringComparison.OrdinalIgnoreCase)), pair.Value.Ids))
            .OrderBy(entry => entry.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
