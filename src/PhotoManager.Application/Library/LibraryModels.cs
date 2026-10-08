using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Library;

/// <summary>Trecho marcado num vídeo (entrada/saída), com nome e nota — vira lista de cortes (EDL/FCPXML) para o editor de vídeo.</summary>
public sealed record ClipMarker(long Id, long PhotoId, double InSeconds, double OutSeconds, string Name, int Rating)
{
    public double Duration => Math.Max(0, OutSeconds - InSeconds);
}

/// <summary>Busca salva que se atualiza sozinha (mesma sintaxe da caixa de busca).</summary>
public sealed record SmartCollection(long Id, string Name, string Query);

/// <summary>Par pasta do catálogo → pasta de backup, com o resultado da última conferência.</summary>
public sealed record BackupTarget(long Id, string SourceFolder, string TargetFolder, DateTime? LastVerifiedUtc, int? MissingCount, int? CheckedCount, int? DifferentCount)
{
    public bool IsVerified => LastVerifiedUtc.HasValue;
    public bool IsHealthy => IsVerified && MissingCount == 0 && DifferentCount == 0;
}

/// <summary>Dados de organização "pro" da Biblioteca que não cabem no catálogo básico.</summary>
public interface ILibraryRepository
{
    Task UpdatePickAsync(IReadOnlyCollection<long> photoIds, PickFlag flag, CancellationToken cancellationToken = default);
    Task UpdateUsageAsync(IReadOnlyCollection<long> photoIds, UsageStatus status, string? note, CancellationToken cancellationToken = default);
    Task UpdateAnalysisAsync(long photoId, long? perceptualHash, double? sharpness, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClipMarker>> GetMarkersAsync(long? photoId = null, CancellationToken cancellationToken = default);
    Task<ClipMarker> AddMarkerAsync(long photoId, double inSeconds, double outSeconds, string name, int rating, CancellationToken cancellationToken = default);
    Task UpdateMarkerAsync(ClipMarker marker, CancellationToken cancellationToken = default);
    Task DeleteMarkerAsync(long markerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SmartCollection>> GetSmartCollectionsAsync(CancellationToken cancellationToken = default);
    Task<SmartCollection> SaveSmartCollectionAsync(long? id, string name, string query, CancellationToken cancellationToken = default);
    Task DeleteSmartCollectionAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupTarget>> GetBackupTargetsAsync(CancellationToken cancellationToken = default);
    Task<BackupTarget> SaveBackupTargetAsync(long? id, string sourceFolder, string targetFolder, CancellationToken cancellationToken = default);
    Task DeleteBackupTargetAsync(long id, CancellationToken cancellationToken = default);
    Task UpdateBackupStatusAsync(long id, DateTime verifiedUtc, int checkedCount, int missingCount, int differentCount, CancellationToken cancellationToken = default);
}

/// <summary>Regras de formato curto (YouTube Shorts / Instagram Reels / TikTok).</summary>
public static class ShortFormRules
{
    /// <summary>Shorts e Reels aceitam até 3 minutos (desde 2024).</summary>
    public const double MaxSeconds = 180;
    /// <summary>"Curto de verdade": até 60 s, o que performa melhor e cabe em qualquer plataforma.</summary>
    public const double QuickSeconds = 60;

    /// <summary>Vídeo vertical perto de 9:16 (tolerância para 3:4, 4:5 de celular não conta) e com duração dentro do limite.</summary>
    public static bool IsShortForm(Photo photo, double maxSeconds = MaxSeconds) =>
        photo.IsVideo && IsNineBySixteen(photo) && photo.DurationSeconds is > 0 and var seconds && seconds <= maxSeconds;

    public static bool IsNineBySixteen(Photo photo)
    {
        if (photo.Width is not > 0 || photo.Height is not > 0) return false;
        double w = photo.Width.Value, h = photo.Height.Value;
        if (photo.UserRotation is 90 or 270) (w, h) = (h, w);
        if (w >= h) return false;
        var ratio = w / h;                          // 9/16 = 0,5625
        return ratio is >= 0.50 and <= 0.62;
    }
}
