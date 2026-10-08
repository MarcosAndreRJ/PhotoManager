using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Library;

/// <summary>Imagem em tons de cinza (8 bits por pixel), para análise.</summary>
public sealed record GrayImage(byte[] Pixels, int Width, int Height);

public interface IGrayImageReader
{
    Task<GrayImage?> ReadAsync(string imagePath, CancellationToken cancellationToken = default);
}

public sealed record AnalysisProgress(int Done, int Total);

/// <summary>
/// Calcula hash perceptual e nitidez de cada item ainda não analisado, a partir da MINIATURA em cache (rápido; o original nunca é aberto).
/// Vídeos usam o quadro da miniatura. Roda em segundo plano e pode ser cancelado/retomado.
/// </summary>
public sealed class PhotoAnalysisService(ILibraryRepository repository, IThumbnailService thumbnails, IGrayImageReader reader)
{
    public async Task<int> AnalyzeAsync(IReadOnlyList<Photo> photos, IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var pending = photos.Where(p => !p.IsMissing && (p.PerceptualHash is null || (!p.IsVideo && p.Sharpness is null))).ToList();
        var done = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var photo = pending[i];
            try
            {
                if (await thumbnails.GetOrCreateAsync(photo.Id, photo.CurrentPath, cancellationToken) is { } thumbnail && await reader.ReadAsync(thumbnail, cancellationToken) is { } gray)
                {
                    photo.PerceptualHash = ImageAnalysis.DifferenceHash(gray.Pixels, gray.Width, gray.Height);
                    // Vídeo: o quadro da miniatura costuma ter desfoque de movimento; a nitidez só vale para fotos.
                    photo.Sharpness = photo.IsVideo ? null : Math.Round(ImageAnalysis.Sharpness(gray.Pixels, gray.Width, gray.Height), 1);
                    await repository.UpdateAnalysisAsync(photo.Id, photo.PerceptualHash, photo.Sharpness, cancellationToken);
                    done++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { }
            if (i % 20 == 0) progress?.Report(new AnalysisProgress(i, pending.Count));
        }
        progress?.Report(new AnalysisProgress(pending.Count, pending.Count));
        return done;
    }
}
