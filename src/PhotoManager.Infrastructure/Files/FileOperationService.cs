using Microsoft.VisualBasic.FileIO;
using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Infrastructure.Files;

public sealed class FileOperationService(ICatalogRepository repository) : IFileOperationService
{
    public async Task MoveAsync(Photo photo, string destinationFolder, CancellationToken cancellationToken = default)
    {
        EnsureSource(photo);
        var destination = GetUniqueDestination(destinationFolder, photo.FileName, allowExisting: false);
        Directory.CreateDirectory(destinationFolder);
        await MoveFileAsync(photo.CurrentPath, destination, cancellationToken);
        MoveSidecar(photo.CurrentPath, destination);
        UpdatePhotoLocation(photo, destination);
        await repository.UpdateLocationAsync(photo, cancellationToken);
    }

    public async Task<Photo?> CopyAsync(Photo photo, string destinationFolder, bool addCopyToCatalog, CancellationToken cancellationToken = default)
    {
        EnsureSource(photo);
        Directory.CreateDirectory(destinationFolder);
        var destination = GetUniqueDestination(destinationFolder, photo.FileName, allowExisting: false);
        await Task.Run(() => File.Copy(photo.CurrentPath, destination), cancellationToken);
        CopySidecar(photo.CurrentPath, destination);
        if (!addCopyToCatalog) return null;
        var info = new FileInfo(destination);
        var copy = new Photo
        {
            FileName = info.Name, CurrentPath = info.FullName, Extension = info.Extension.ToLowerInvariant(), FileSize = info.Length,
            Width = photo.Width, Height = photo.Height, DurationSeconds = photo.DurationSeconds, CreatedAt = info.CreationTimeUtc, ModifiedAt = info.LastWriteTimeUtc, DateTaken = photo.DateTaken, ImportedAt = DateTime.UtcNow
        };
        await repository.AddAsync(copy, cancellationToken);
        return copy;
    }

    public async Task RenameAsync(Photo photo, string newFileName, CancellationToken cancellationToken = default)
    {
        EnsureSource(photo);
        var cleanName = Path.GetFileNameWithoutExtension(newFileName);
        if (string.IsNullOrWhiteSpace(cleanName)) throw new ArgumentException("O novo nome não pode ficar vazio.", nameof(newFileName));
        var extension = Path.GetExtension(newFileName);
        var destination = Path.Combine(Path.GetDirectoryName(photo.CurrentPath)!, cleanName + (string.IsNullOrWhiteSpace(extension) ? photo.Extension : extension));
        if (!string.Equals(photo.CurrentPath, destination, StringComparison.OrdinalIgnoreCase) && File.Exists(destination)) throw new IOException($"Já existe um arquivo chamado {Path.GetFileName(destination)}.");
        await MoveFileAsync(photo.CurrentPath, destination, cancellationToken);
        MoveSidecar(photo.CurrentPath, destination);
        UpdatePhotoLocation(photo, destination);
        await repository.UpdateLocationAsync(photo, cancellationToken);
    }

    public async Task MoveToRecycleBinAsync(Photo photo, CancellationToken cancellationToken = default)
    {
        EnsureSource(photo);
        await Task.Run(() => FileSystem.DeleteFile(photo.CurrentPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin), cancellationToken);
        if (XmpSidecar.UsesSidecar(photo.CurrentPath) && File.Exists(XmpSidecar.PathFor(photo.CurrentPath)))
            await Task.Run(() => FileSystem.DeleteFile(XmpSidecar.PathFor(photo.CurrentPath), UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin), cancellationToken);
        photo.IsMissing = true;
        await repository.UpdateLocationAsync(photo, cancellationToken);
    }

    public async Task RenameBatchAsync(IReadOnlyList<Photo> photos, string template, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(template)) throw new ArgumentException("O template não pode ficar vazio.", nameof(template));
        for (var index = 0; index < photos.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var photo = photos[index];
            var date = photo.DateTaken ?? photo.CreatedAt;
            var name = template.Replace("{name}", Path.GetFileNameWithoutExtension(photo.FileName), StringComparison.OrdinalIgnoreCase)
                .Replace("{date}", date.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
                .Replace("{year}", date.ToString("yyyy"), StringComparison.OrdinalIgnoreCase)
                .Replace("{month}", date.ToString("MM"), StringComparison.OrdinalIgnoreCase)
                .Replace("{sequence}", (index + 1).ToString("D3"), StringComparison.OrdinalIgnoreCase);
            await RenameAsync(photo, name + photo.Extension, cancellationToken);
        }
    }

    public async Task<ExternalTransferResult> TransferExternalAsync(IReadOnlyList<string> sources, string destinationFolder, bool move, CancellationToken cancellationToken = default)
    {
        var created = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(destinationFolder, Path.GetFileName(source));
            try
            {
                if (!File.Exists(source)) { failed.Add(source); continue; }
                if (File.Exists(destination)) { skipped.Add(source); continue; }            // nunca sobrescreve
                Directory.CreateDirectory(destinationFolder);
                if (move) await MoveFileAsync(source, destination, cancellationToken);
                else await Task.Run(() => File.Copy(source, destination), cancellationToken);
                if (move) MoveSidecar(source, destination); else CopySidecar(source, destination);
                created.Add(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(source); }
        }
        return new ExternalTransferResult(created, skipped, failed);
    }

    /// <summary>Preview e miniatura podem estar lendo o arquivo por alguns milissegundos; tenta de novo antes de falhar.</summary>
    private static async Task MoveFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(source, destination); return; }
            catch (IOException) when (attempt < 6 && File.Exists(source) && !File.Exists(destination)) { await Task.Delay(100 * attempt, cancellationToken); }
        }
    }

    /// <summary>O sidecar .xmp acompanha a mídia (mover/renomear/copiar); se o destino já tiver um sidecar, o existente é preservado.</summary>
    private static void MoveSidecar(string sourceMedia, string destinationMedia)
    {
        if (!XmpSidecar.UsesSidecar(sourceMedia)) return;
        var from = XmpSidecar.PathFor(sourceMedia);
        var to = XmpSidecar.PathFor(destinationMedia);
        if (!File.Exists(from) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase) || File.Exists(to)) return;
        try { File.Move(from, to); } catch (IOException) { }
    }

    private static void CopySidecar(string sourceMedia, string destinationMedia)
    {
        if (!XmpSidecar.UsesSidecar(sourceMedia)) return;
        var from = XmpSidecar.PathFor(sourceMedia);
        var to = XmpSidecar.PathFor(destinationMedia);
        if (!File.Exists(from) || File.Exists(to)) return;
        try { File.Copy(from, to); } catch (IOException) { }
    }

    private static void EnsureSource(Photo photo)
    {
        if (photo.IsMissing || !File.Exists(photo.CurrentPath)) throw new FileNotFoundException("O arquivo da foto não está disponível.", photo.CurrentPath);
    }

    private static string GetUniqueDestination(string folder, string fileName, bool allowExisting)
    {
        var destination = Path.Combine(folder, fileName);
        if (allowExisting || !File.Exists(destination)) return destination;
        throw new IOException($"Já existe um arquivo chamado {fileName} na pasta de destino.");
    }

    private static void UpdatePhotoLocation(Photo photo, string destination)
    {
        photo.CurrentPath = Path.GetFullPath(destination);
        photo.FileName = Path.GetFileName(destination);
        photo.FileSize = new FileInfo(destination).Length;
        photo.ModifiedAt = File.GetLastWriteTimeUtc(destination);
        photo.IsMissing = false;
    }
}
