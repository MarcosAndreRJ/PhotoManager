using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoManager.Application.Library;
using PhotoManager.Application.Transfer;
using PhotoManager.Domain.Photos;
using PhotoManager.Infrastructure.Images;

namespace PhotoManager.Infrastructure.Files;

/// <summary>
/// Exporta com predefinição: redimensiona pelo lado maior (orientação EXIF e giro manual já aplicados), grava JPEG/PNG,
/// aplica marca d'água de texto e renomeia pelo modelo. Vídeos e "formato original" são copiados byte a byte.
/// </summary>
public sealed class ExportService : IExportService
{
    public async Task<ExportResult> ExportAsync(IReadOnlyList<Photo> photos, ExportPreset preset, string destination, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        var created = new List<string>();
        var failed = new List<(string, string)>();
        var items = photos.Where(p => !p.IsMissing && (preset.IncludeVideos || !p.IsVideo)).ToList();
        var renames = BatchRenamePlanner.Plan(items.Select(p => (p.CurrentPath, p.DisplayDate)).ToList(), preset.NameTemplate, 1, []);
        for (var i = 0; i < items.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) return new ExportResult(created, failed, true);
            var photo = items[i];
            progress?.Report(new ExportProgress(i, items.Count, photo.FileName));
            try
            {
                var stem = renames.Error is null ? Path.GetFileNameWithoutExtension(renames.Renames[i].To) : Path.GetFileNameWithoutExtension(photo.FileName);
                var copyAsIs = photo.IsVideo || preset.Format == "original";
                var extension = copyAsIs ? photo.Extension : "." + preset.Format;
                var target = Unique(Path.Combine(destination, stem + extension));
                var temporary = target + ".pmpart";
                if (copyAsIs) await Task.Run(() => File.Copy(photo.CurrentPath, temporary, overwrite: true), cancellationToken);
                else await Task.Run(() => Render(photo, preset, temporary), cancellationToken);
                File.Move(temporary, target);
                created.Add(target);
            }
            catch (OperationCanceledException) { return new ExportResult(created, failed, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FileFormatException)
            {
                failed.Add((photo.CurrentPath, ex.Message));
            }
        }
        progress?.Report(new ExportProgress(items.Count, items.Count, string.Empty));
        return new ExportResult(created, failed, false);
    }

    private static void Render(Photo photo, ExportPreset preset, string path)
    {
        var source = ImageLoader.LoadFull(photo.CurrentPath) ?? throw new InvalidOperationException("Formato não suportado para exportar.");
        if (photo.UserRotation != 0) source = ExifOrientation.Rotate(source, photo.UserRotation);
        if (preset.LongEdge is { } edge && Math.Max(source.PixelWidth, source.PixelHeight) > edge)
        {
            var scale = edge / (double)Math.Max(source.PixelWidth, source.PixelHeight);
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        if (!string.IsNullOrWhiteSpace(preset.WatermarkText)) source = Watermark(source, preset);
        BitmapEncoder encoder = preset.Format == "png" ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = Math.Clamp(preset.Quality, 10, 100) };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Texto com sombra, tamanho proporcional à imagem (3,5% do lado menor), na posição escolhida.</summary>
    private static BitmapSource Watermark(BitmapSource source, ExportPreset preset)
    {
        var width = source.PixelWidth; var height = source.PixelHeight;
        var size = Math.Max(12, Math.Min(width, height) * 0.035);
        var text = new FormattedText(preset.WatermarkText!, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), size,
            new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(preset.WatermarkOpacity, 0.05, 1)), 255, 255, 255)), 1.0);
        var margin = size;
        var origin = preset.WatermarkPosition switch
        {
            WatermarkPosition.TopLeft => new Point(margin, margin),
            WatermarkPosition.TopRight => new Point(width - text.Width - margin, margin),
            WatermarkPosition.BottomLeft => new Point(margin, height - text.Height - margin),
            WatermarkPosition.Center => new Point((width - text.Width) / 2, (height - text.Height) / 2),
            _ => new Point(width - text.Width - margin, height - text.Height - margin)
        };
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, width, height));
            var shadow = new FormattedText(preset.WatermarkText!, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), size,
                new SolidColorBrush(Color.FromArgb((byte)(160 * Math.Clamp(preset.WatermarkOpacity, 0.05, 1)), 0, 0, 0)), 1.0);
            context.DrawText(shadow, new Point(origin.X + size / 16, origin.Y + size / 16));
            context.DrawText(text, origin);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static string Unique(string path) =>
        File.Exists(path) ? TransferConflicts.KeepBothName(Path.GetDirectoryName(path)!, Path.GetFileName(path), File.Exists) : path;
}
