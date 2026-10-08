using System.Windows.Media.Imaging;
using PhotoManager.Application.Catalog;
using PhotoManager.Infrastructure.Media;

namespace PhotoManager.Infrastructure.Images;

public sealed class ThumbnailService(string cacheDirectory) : IThumbnailService, IPhotoInfoReader, PhotoManager.Application.Transfer.IFileThumbnailService
{
    /// <summary>Cache por caminho+tamanho+data (arquivos fora do catálogo não têm PhotoId); fica em Thumbnails/Files.</summary>
    public async Task<string?> GetOrCreateForFileAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        FileInfo info;
        try { info = new FileInfo(sourcePath); if (!info.Exists) return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
        var key = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes($"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"));
        var folder = Path.Combine(cacheDirectory, "Files");
        var destination = Path.Combine(folder, Convert.ToHexString(key) + ".jpg");
        if (File.Exists(destination)) return destination;
        try
        {
            Directory.CreateDirectory(folder);
            await Task.Run(() => CreateThumbnail(sourcePath, destination), cancellationToken);
            return destination;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    public async Task<(int Width, int Height)> ReadDimensionsAsync(string path, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => ImageLoader.ReadSize(path), cancellationToken);
    }

    public Task<MediaInfo> ReadVideoInfoAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Mp4Info.TryRead(path) is { } info ? new MediaInfo(info.Width, info.Height, info.DurationSeconds, info.CreatedUtc, info.RotationDegrees) : new MediaInfo(null, null, null, null), cancellationToken);

    public Task<int> ReadExifOrientationAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => ExifOrientation.Read(path), cancellationToken);

    public void Remove(long photoId)
    {
        try { var file = Path.Combine(cacheDirectory, $"{photoId}.jpg"); if (File.Exists(file)) File.Delete(file); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public async Task<string?> GetOrCreateAsync(long photoId, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) return null;
        Directory.CreateDirectory(cacheDirectory);
        var destination = Path.Combine(cacheDirectory, $"{photoId}.jpg");
        if (File.Exists(destination)) return destination;
        try
        {
            await Task.Run(() => CreateThumbnail(sourcePath, destination), cancellationToken);
            return destination;
        }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
        catch (FileFormatException) { return null; }
    }

    private static void CreateThumbnail(string sourcePath, string destination)
    {
        var frame = ImageLoader.LoadFrame(sourcePath);
        var scale = Math.Min(1d, 280d / Math.Max(frame.PixelWidth, frame.PixelHeight));
        var bitmap = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var temp = destination + ".tmp";
        using (var stream = File.Create(temp)) encoder.Save(stream);
        File.Move(temp, destination, overwrite: true);
    }
}
