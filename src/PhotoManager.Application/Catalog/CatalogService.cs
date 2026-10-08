using Microsoft.Extensions.Logging;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Catalog;

public sealed class CatalogService(ICatalogRepository repository, IPhotoInfoReader photoInfoReader, ILogger<CatalogService> logger) : ICatalogService
{
    /// <summary>
    /// Revisão das informações de mídia (dimensões "como exibidas" + rotação): 2 = JPEG com orientação EXIF aplicada e vídeo com a rotação do
    /// contêiner guardada. Itens com revisão menor são relidos em segundo plano.
    /// </summary>
    public const int CurrentMediaInfoRevision = 2;

    public async Task<IReadOnlyList<Photo>> RefreshMediaInfoAsync(IReadOnlyList<Photo> photos, CancellationToken cancellationToken = default)
    {
        var needsThumbnail = new List<Photo>();
        foreach (var photo in photos.Where(p => p.MediaInfoRevision < CurrentMediaInfoRevision && !p.IsMissing && (p.IsVideo || ImageFormats.IsJpeg(p.Extension))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (photo.IsVideo)
                {
                    var info = await photoInfoReader.ReadVideoInfoAsync(photo.CurrentPath, cancellationToken);
                    photo.Width = info.Width ?? photo.Width;
                    photo.Height = info.Height ?? photo.Height;
                    photo.DurationSeconds = info.DurationSeconds ?? photo.DurationSeconds;
                    photo.AutoRotation = info.RotationDegrees;
                    if (info.RotationDegrees != 0) needsThumbnail.Add(photo);           // a miniatura antiga saiu deitada
                }
                else
                {
                    var orientation = await photoInfoReader.ReadExifOrientationAsync(photo.CurrentPath, cancellationToken);
                    // As dimensões antigas são os pixels brutos: giros de 90°/270° (EXIF 5-8) trocam largura e altura.
                    if (orientation is >= 5 and <= 8 && photo.Width is { } w && photo.Height is { } h) { photo.Width = h; photo.Height = w; }
                    if (orientation != 1) needsThumbnail.Add(photo);
                }
                photo.MediaInfoRevision = CurrentMediaInfoRevision;
                await repository.UpdateMediaInfoAsync(photo, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.LogWarning(ex, "Não foi possível reler as informações de {FilePath}.", photo.CurrentPath);
            }
        }
        return needsThumbnail;
    }

    public async Task SetUserRotationAsync(IReadOnlyList<Photo> photos, int degrees, CancellationToken cancellationToken = default)
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (degrees % 90 != 0) throw new ArgumentOutOfRangeException(nameof(degrees), "O giro deve ser múltiplo de 90°.");
        foreach (var photo in photos)
        {
            await repository.UpdateUserRotationAsync(photo.Id, degrees, cancellationToken);
            photo.UserRotation = degrees;
        }
    }

    public async Task SetColorLabelAsync(IReadOnlyList<Photo> photos, PhotoColor color, CancellationToken cancellationToken = default)
    {
        if (photos.Count == 0) return;
        await repository.UpdateColorLabelAsync(photos.Select(p => p.Id).ToList(), color, cancellationToken);
        foreach (var photo in photos) photo.ColorLabel = color;
    }

    public async Task<IReadOnlyList<long>> RemoveMissingAsync(IReadOnlyList<Photo> photos, CancellationToken cancellationToken = default)
    {
        // Segurança: a decisão é reconferida no disco; um arquivo que voltou a existir nunca é removido do catálogo.
        var ids = photos.Where(p => !File.Exists(p.CurrentPath)).Select(p => p.Id).Distinct().ToList();
        if (ids.Count == 0) return [];
        await repository.DeletePhotosAsync(ids, cancellationToken);
        return ids;
    }

    public async Task<IReadOnlyList<Photo>> GetPhotosAsync(PhotoFilter? filter = null, CancellationToken cancellationToken = default)
    {
        await repository.UpdateMissingStatesAsync(cancellationToken);
        var photos = await repository.GetAllAsync(cancellationToken);
        if (filter is null) return photos;
        return photos.Where(filter.Matches).ToList();
    }

    public Task<CatalogImportResult> ImportFolderAsync(string folderPath, IProgress<CatalogProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ImportPathsAsync([folderPath], progress, cancellationToken);

    /// <summary>Cataloga arquivos e/ou pastas (recursivo) no lugar em que estão; o que já está no catálogo é ignorado. Usado pelo "Adicionar pasta" e por soltar do Explorer.</summary>
    public async Task<CatalogImportResult> ImportPathsAsync(IReadOnlyList<string> paths, IProgress<CatalogProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = paths.SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                : File.Exists(path) ? [path] : [])
            .Where(MediaFormats.IsSupported)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        var imported = 0;
        var failed = 0;
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            try
            {
                if (await repository.FindByPathAsync(Path.GetFullPath(path), cancellationToken) is null)
                {
                    await repository.AddAsync(await BuildPhotoAsync(path, cancellationToken), cancellationToken);
                    imported++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failed++;
                logger.LogWarning(ex, "Não foi possível catalogar o arquivo {FilePath}.", path);
            }
            progress?.Report(new CatalogProgress(scanned, imported, failed, path));
        }

        await repository.UpdateMissingStatesAsync(cancellationToken);
        return new CatalogImportResult(scanned, imported, failed);
    }

    private async Task<Photo> BuildPhotoAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var photo = new Photo
        {
            FileName = info.Name,
            CurrentPath = info.FullName,
            Extension = info.Extension.ToLowerInvariant(),
            FileSize = info.Length,
            CreatedAt = info.CreationTimeUtc,
            ModifiedAt = info.LastWriteTimeUtc,
            ImportedAt = DateTime.UtcNow
        };
        try
        {
            if (MediaFormats.IsVideo(path))
            {
                var video = await photoInfoReader.ReadVideoInfoAsync(path, cancellationToken);
                photo.Width = video.Width;
                photo.Height = video.Height;
                photo.DurationSeconds = video.DurationSeconds;
                photo.DateTaken = video.CreatedUtc?.ToLocalTime();
                photo.AutoRotation = video.RotationDegrees;
                photo.MediaInfoRevision = CurrentMediaInfoRevision;
            }
            else
            {
                var dimensions = await photoInfoReader.ReadDimensionsAsync(path, cancellationToken);   // já "como exibidas" (EXIF aplicado)
                photo.Width = dimensions.Width;
                photo.Height = dimensions.Height;
                if (ImageFormats.IsJpeg(path)) photo.MediaInfoRevision = CurrentMediaInfoRevision;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            logger.LogWarning(ex, "Não foi possível ler as dimensões de {FilePath}.", path);
        }
        return photo;
    }
}
