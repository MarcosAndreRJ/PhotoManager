using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Library;

public sealed record StatBucket(string Label, int Count, long Bytes, double Seconds = 0);

/// <summary>Painel de armazenamento: para onde vai o espaço e quanto dá para liberar.</summary>
public sealed record LibraryStats(
    int Total, long TotalBytes, int Photos, long PhotoBytes, int Videos, long VideoBytes, double VideoSeconds,
    int Unrated, int Picked, int Rejected, long RejectedBytes, int Missing, int ShortForm,
    int DuplicateExtraCopies, long DuplicateReclaimableBytes,
    IReadOnlyList<StatBucket> ByYear, IReadOnlyList<StatBucket> ByFolder, IReadOnlyList<StatBucket> ByExtension, IReadOnlyList<Photo> LargestVideos)
{
    public long ReclaimableBytes => RejectedBytes + DuplicateReclaimableBytes;

    /// <summary>Estatísticas de um conjunto (o catálogo inteiro ou a visão filtrada). Duplicatas exatas vêm do hash já calculado.</summary>
    public static LibraryStats Compute(IReadOnlyList<Photo> photos, int topFolders = 12, long largeVideoBytes = 2L << 30)
    {
        var present = photos.Where(p => !p.IsMissing).ToList();
        var videos = present.Where(p => p.IsVideo).ToList();
        var images = present.Where(p => !p.IsVideo).ToList();
        var duplicates = present.Where(p => !string.IsNullOrEmpty(p.ContentHash)).GroupBy(p => p.ContentHash!).Where(g => g.Count() > 1).ToList();
        return new LibraryStats(
            photos.Count, present.Sum(p => p.FileSize), images.Count, images.Sum(p => p.FileSize), videos.Count, videos.Sum(p => p.FileSize), videos.Sum(p => p.DurationSeconds ?? 0),
            present.Count(p => p.Rating == 0), present.Count(p => p.Pick == PickFlag.Picked), present.Count(p => p.Pick == PickFlag.Rejected),
            present.Where(p => p.Pick == PickFlag.Rejected).Sum(p => p.FileSize), photos.Count(p => p.IsMissing), videos.Count(p => ShortFormRules.IsShortForm(p)),
            duplicates.Sum(g => g.Count() - 1), duplicates.Sum(g => g.Skip(1).Sum(p => p.FileSize)),
            present.GroupBy(p => p.DisplayDate.Year).OrderByDescending(g => g.Key).Select(g => Bucket(g.Key.ToString(), g)).ToList(),
            present.GroupBy(p => Path.GetDirectoryName(p.CurrentPath) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(g => Bucket(g.Key, g)).OrderByDescending(b => b.Bytes).Take(topFolders).ToList(),
            present.GroupBy(p => p.Extension.ToLowerInvariant()).Select(g => Bucket(g.Key.TrimStart('.').ToUpperInvariant(), g)).OrderByDescending(b => b.Bytes).ToList(),
            videos.Where(v => v.FileSize >= largeVideoBytes).OrderByDescending(v => v.FileSize).Take(20).ToList());
    }

    private static StatBucket Bucket(string label, IEnumerable<Photo> items)
    {
        var list = items as IList<Photo> ?? items.ToList();
        return new StatBucket(label, list.Count, list.Sum(p => p.FileSize), list.Sum(p => p.IsVideo ? p.DurationSeconds ?? 0 : 0));
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.00} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };

    public static string FormatHours(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes:00} min" : $"{span.Minutes} min {span.Seconds:00} s";
    }
}
