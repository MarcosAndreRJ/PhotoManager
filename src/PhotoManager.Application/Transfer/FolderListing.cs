using PhotoManager.Application.Catalog;

namespace PhotoManager.Application.Transfer;

public enum TransferEntryKind { Folder, Photo, Video, Other }

/// <summary>Um item (pasta ou arquivo) de uma pasta listada pelo módulo Transferência.</summary>
public sealed record TransferEntry(string Path, string Name, TransferEntryKind Kind, long Size, DateTime ModifiedUtc)
{
    public bool IsFolder => Kind == TransferEntryKind.Folder;
    public bool IsMedia => Kind is TransferEntryKind.Photo or TransferEntryKind.Video;
    public string Extension => IsFolder ? string.Empty : System.IO.Path.GetExtension(Name).ToLowerInvariant();
}

public sealed record FolderListResult(IReadOnlyList<TransferEntry> Entries, string? Error)
{
    public bool IsOk => Error is null;
}

/// <summary>Enumeração eficiente de uma pasta: só nomes, tamanhos e datas (nada é aberto nem lido). Falhas viram mensagem, não exceção.</summary>
public static class FolderLister
{
    public static FolderListResult List(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return new([], "Nenhuma pasta escolhida.");
        try
        {
            if (!Directory.Exists(path)) return new([], "A pasta não existe ou não está disponível (disco desconectado ou rede fora do ar).");
            var entries = new List<TransferEntry>();
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden, RecurseSubdirectories = false };
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (info is DirectoryInfo directory)
                    entries.Add(new(directory.FullName, directory.Name, TransferEntryKind.Folder, 0, directory.LastWriteTimeUtc));
                else if (info is FileInfo file)
                    entries.Add(new(file.FullName, file.Name, KindOf(file.Name), file.Length, file.LastWriteTimeUtc));
            }
            return new(entries, null);
        }
        catch (UnauthorizedAccessException) { return new([], "Sem permissão para abrir esta pasta."); }
        catch (IOException ex) { return new([], $"Não foi possível ler a pasta: {ex.Message}"); }
    }

    public static TransferEntryKind KindOf(string fileName) =>
        MediaFormats.IsVideo(fileName) ? TransferEntryKind.Video : ImageFormats.IsSupported(fileName) ? TransferEntryKind.Photo : TransferEntryKind.Other;
}
