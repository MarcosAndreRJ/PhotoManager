using System.Collections.ObjectModel;
using System.Globalization;
using PhotoManager.Application.Library;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Commands;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

/// <summary>Uma foto/vídeo nas ferramentas (miniatura carregada em segundo plano).</summary>
public sealed class ToolPhotoViewModel(Photo photo, bool isBest = false) : ViewModelBase
{
    private Uri? _thumbnail;
    public Photo Photo { get; } = photo;
    public bool IsBest { get; } = isBest;
    public string FileName => Photo.FileName;
    public string Folder => Path.GetDirectoryName(Photo.CurrentPath) ?? string.Empty;
    public string Details => string.Join("  ·  ", new[]
    {
        Photo.Width is > 0 && Photo.Height is > 0 ? $"{Photo.Width}×{Photo.Height}" : null,
        LibraryStats.FormatBytes(Photo.FileSize),
        Photo.Sharpness is { } s && !Photo.IsVideo ? $"nitidez {s:0}" : null,
        Photo.Rating > 0 ? new string('★', Photo.Rating) : null,
        Photo.Pick switch { PickFlag.Picked => "escolhida", PickFlag.Rejected => "rejeitada", _ => null }
    }.Where(x => x is not null));
    public Uri? Thumbnail { get => _thumbnail; set { _thumbnail = value; OnPropertyChanged(); } }
    public void Refresh() => OnPropertyChanged(nameof(Details));
}

public sealed class SimilarGroupViewModel(SimilarGroup group) : ViewModelBase
{
    public SimilarGroup Group { get; } = group;
    public IReadOnlyList<ToolPhotoViewModel> Photos { get; } = group.Photos.OrderByDescending(p => p.Id == group.Best.Id).Select(p => new ToolPhotoViewModel(p, p.Id == group.Best.Id)).ToList();
    public string Title => $"{Group.Photos.Count} parecidas · liberaria {LibraryStats.FormatBytes(Group.ReclaimableBytes)}";
    public string BestText => $"Sugestão: manter “{Group.Best.FileName}”";
}

public sealed record BarItem(string Label, string Value, double Percent, string ToolTip = "");

public sealed class BackupTargetViewModel(BackupTarget target) : ViewModelBase
{
    private BackupTarget _target = target;
    private BackupReport? _report;
    private bool _isBusy;
    private string _progress = string.Empty;

    public BackupTarget Target { get => _target; set { _target = value; Notify(); } }
    public BackupReport? Report { get => _report; set { _report = value; Notify(); } }
    public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); } }
    public string ProgressText { get => _progress; set { _progress = value; OnPropertyChanged(); } }
    public string Source => Target.SourceFolder;
    public string Destination => Target.TargetFolder;
    public bool HasProblems => Report is { IsComplete: false } || Target is { IsVerified: true, IsHealthy: false };
    public bool CanCopyMissing => Report is { IsComplete: false };
    public string StatusText
    {
        get
        {
            if (Report is { } report)
                return report.IsComplete ? $"Completo: {report.Checked:N0} arquivo(s) conferido(s)."
                    : $"{report.Missing.Count:N0} faltando e {report.Different.Count:N0} diferente(s) de {report.Checked:N0} ({LibraryStats.FormatBytes(report.MissingBytes)} a copiar).";
            if (Target.LastVerifiedUtc is not { } when) return "Nunca conferido.";
            var age = DateTime.UtcNow - when;
            var ago = age.TotalDays >= 1 ? $"há {(int)age.TotalDays} dia(s)" : age.TotalHours >= 1 ? $"há {(int)age.TotalHours} h" : "agora há pouco";
            return Target.IsHealthy ? $"Completo na última conferência ({ago})." : $"Na última conferência ({ago}): {Target.MissingCount} faltando, {Target.DifferentCount} diferente(s).";
        }
    }
    public string MissingPreview => Report is { IsComplete: false } r ? string.Join("\n", r.Missing.Concat(r.Different).Take(8)) + (r.Missing.Count + r.Different.Count > 8 ? $"\n… e mais {r.Missing.Count + r.Different.Count - 8}" : string.Empty) : string.Empty;

    private void Notify()
    {
        foreach (var name in new[] { nameof(Target), nameof(Report), nameof(Source), nameof(Destination), nameof(StatusText), nameof(HasProblems), nameof(CanCopyMissing), nameof(MissingPreview) }) OnPropertyChanged(name);
    }
}

/// <summary>Ferramentas "pró": parecidas/desfocadas, painel de armazenamento e backup verificado.</summary>
public sealed class ProToolsViewModel : ViewModelBase
{
    private readonly LibraryViewModel _library;
    private readonly LibraryProServices _pro;
    private string _similarStatus = "Clique em “Procurar parecidas”.", _storageTitle = string.Empty, _backupStatus = string.Empty;
    private bool _isBusy;

    public ProToolsViewModel(LibraryViewModel library, LibraryProServices pro)
    {
        _library = library;
        _pro = pro;
        FindSimilarCommand = new RelayCommand(_ => _ = FindSimilarAsync(), _ => !IsBusy);
        KeepBestCommand = new RelayCommand(p => { if (p is SimilarGroupViewModel group) _ = KeepBestAsync(group); });
        KeepAllBestCommand = new RelayCommand(_ => _ = KeepAllBestAsync(), _ => SimilarGroups.Count > 0);
        RejectBlurryCommand = new RelayCommand(_ => _ = RejectBlurryAsync(), _ => BlurryCount > 0);
        ShowBlurryCommand = new RelayCommand(_ => { _library.ShowSmartList("blurry"); NavigateToLibrary?.Invoke(); }, _ => BlurryCount > 0);
        ShowRejectedCommand = new RelayCommand(_ => { _library.ShowSmartList("rejected"); NavigateToLibrary?.Invoke(); });
        RefreshStorageCommand = new RelayCommand(_ => RefreshStorage());
        AddBackupCommand = new RelayCommand(_ => _ = AddBackupAsync());
        RemoveBackupCommand = new RelayCommand(p => { if (p is BackupTargetViewModel target) _ = RemoveBackupAsync(target); });
        VerifyBackupCommand = new RelayCommand(p => { if (p is BackupTargetViewModel target) _ = VerifyAsync(target); }, p => p is BackupTargetViewModel { IsBusy: false } && _pro.Backup is not null);
        CopyMissingCommand = new RelayCommand(p => { if (p is BackupTargetViewModel target) _ = CopyMissingAsync(target); }, p => p is BackupTargetViewModel { CanCopyMissing: true, IsBusy: false });
        VerifyAllCommand = new RelayCommand(_ => _ = VerifyAllAsync(), _ => Backups.Count > 0 && _pro.Backup is not null);
        RefreshStorage();
        _ = LoadBackupsAsync();
    }

    /// <summary>Volta para a Biblioteca (o shell define).</summary>
    public Action? NavigateToLibrary { get; set; }
    /// <summary>Escolher pasta (substituível nos testes).</summary>
    public Func<string, string?> PickFolder { get; set; } = title =>
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    };

    public bool IsBusy { get => _isBusy; private set { _isBusy = value; OnPropertyChanged(); FindSimilarCommand.RaiseCanExecuteChanged(); } }

    // ---------- parecidas e desfocadas ----------

    public RelayCommand FindSimilarCommand { get; }
    public RelayCommand KeepBestCommand { get; }
    public RelayCommand KeepAllBestCommand { get; }
    public RelayCommand RejectBlurryCommand { get; }
    public RelayCommand ShowBlurryCommand { get; }
    public RelayCommand ShowRejectedCommand { get; }
    public ObservableCollection<SimilarGroupViewModel> SimilarGroups { get; } = [];
    public string SimilarStatus { get => _similarStatus; private set { _similarStatus = value; OnPropertyChanged(); } }
    public int BlurryCount => _library.AllPhotos.Count(p => !p.IsVideo && !p.IsMissing && p.Sharpness is { } s && s < LibraryQuery.BlurThreshold && p.Pick != PickFlag.Rejected);
    public string BlurryText => $"{BlurryCount:N0} foto(s) com pouca nitidez (prováveis tremidas/desfocadas) ainda não rejeitadas.";
    public string AnalysisCoverage
    {
        get
        {
            var all = _library.AllPhotos.Count(p => !p.IsMissing);
            var done = _library.AllPhotos.Count(p => !p.IsMissing && p.PerceptualHash.HasValue);
            return done >= all ? $"Todos os {all:N0} itens já foram analisados." : $"{done:N0} de {all:N0} itens analisados — a análise continua em segundo plano; procure de novo depois para incluir o restante.";
        }
    }

    public async Task FindSimilarAsync()
    {
        IsBusy = true;
        SimilarStatus = "Comparando…";
        try
        {
            var photos = _library.AllPhotos.ToList();
            var groups = await Task.Run(() => SimilarityFinder.Find(photos));
            SimilarGroups.Clear();
            foreach (var group in groups.Take(300)) SimilarGroups.Add(new SimilarGroupViewModel(group));
            SimilarStatus = groups.Count == 0 ? "Nenhum grupo de itens parecidos." : $"{groups.Count:N0} grupo(s) · {groups.Sum(g => g.Photos.Count):N0} itens · até {LibraryStats.FormatBytes(groups.Sum(g => g.ReclaimableBytes))} a liberar.";
            foreach (var name in new[] { nameof(AnalysisCoverage), nameof(BlurryCount), nameof(BlurryText) }) OnPropertyChanged(name);
            KeepAllBestCommand.RaiseCanExecuteChanged(); RejectBlurryCommand.RaiseCanExecuteChanged(); ShowBlurryCommand.RaiseCanExecuteChanged();
            _ = LoadThumbnailsAsync(SimilarGroups.SelectMany(g => g.Photos).ToList());
        }
        finally { IsBusy = false; }
    }

    /// <summary>A melhor do grupo fica escolhida e as outras rejeitadas — nada é apagado; "Rejeitadas" › Lixeira quando quiser.</summary>
    public async Task KeepBestAsync(SimilarGroupViewModel group)
    {
        await _library.SetPickAsync([CardFor(group.Group.Best)], PickFlag.Picked, advance: false);
        await _library.SetPickAsync(group.Group.Photos.Where(p => p.Id != group.Group.Best.Id).Select(CardFor).ToList(), PickFlag.Rejected, advance: false);
        foreach (var photo in group.Photos) photo.Refresh();
        SimilarGroups.Remove(group);
        KeepAllBestCommand.RaiseCanExecuteChanged();
    }

    private async Task KeepAllBestAsync()
    {
        var total = SimilarGroups.Sum(g => g.Group.Photos.Count - 1);
        if (!_library.ConfirmAction($"Manter a sugestão de cada um dos {SimilarGroups.Count} grupos e rejeitar os outros {total} itens?\n\nNada é apagado agora: os rejeitados ficam na lista “Rejeitadas”.")) return;
        foreach (var group in SimilarGroups.ToList()) await KeepBestAsync(group);
        SimilarStatus = $"Pronto: {total} item(ns) rejeitado(s). Revise em “Rejeitadas” antes de enviar para a Lixeira.";
    }

    private async Task RejectBlurryAsync()
    {
        var blurry = _library.AllPhotos.Where(p => !p.IsVideo && !p.IsMissing && p.Sharpness is { } s && s < LibraryQuery.BlurThreshold && p.Pick != PickFlag.Rejected).ToList();
        if (!_library.ConfirmAction($"Marcar {blurry.Count} foto(s) desfocada(s) como rejeitadas?\n\nNada é apagado agora; confira em “Rejeitadas”.")) return;
        await _library.SetPickAsync(blurry.Select(CardFor).ToList(), PickFlag.Rejected, advance: false);
        OnPropertyChanged(nameof(BlurryCount)); OnPropertyChanged(nameof(BlurryText));
        RejectBlurryCommand.RaiseCanExecuteChanged();
    }

    private PhotoCardViewModel CardFor(Photo photo) => _library.Photos.FirstOrDefault(c => c.Photo.Id == photo.Id) ?? new PhotoCardViewModel(photo);

    private async Task LoadThumbnailsAsync(IReadOnlyList<ToolPhotoViewModel> items)
    {
        foreach (var item in items)
        {
            try { if (await _library.Thumbnails.GetOrCreateAsync(item.Photo.Id, item.Photo.CurrentPath) is { } path) item.Thumbnail = new Uri(path); }
            catch (Exception) { }
        }
    }

    // ---------- armazenamento ----------

    public RelayCommand RefreshStorageCommand { get; }
    public string StorageTitle { get => _storageTitle; private set { _storageTitle = value; OnPropertyChanged(); } }
    public IReadOnlyList<SummaryLine> StorageLines { get; private set; } = [];
    public IReadOnlyList<BarItem> ByYear { get; private set; } = [];
    public IReadOnlyList<BarItem> ByFolder { get; private set; } = [];
    public IReadOnlyList<BarItem> ByType { get; private set; } = [];
    public IReadOnlyList<ToolPhotoViewModel> LargestVideos { get; private set; } = [];

    public void RefreshStorage()
    {
        var stats = LibraryStats.Compute(_library.AllPhotos);
        StorageTitle = $"{LibraryStats.FormatBytes(stats.TotalBytes)} em {stats.Total:N0} itens";
        StorageLines =
        [
            new("Fotos", $"{stats.Photos:N0} · {LibraryStats.FormatBytes(stats.PhotoBytes)}"),
            new("Vídeos", $"{stats.Videos:N0} · {LibraryStats.FormatBytes(stats.VideoBytes)} · {LibraryStats.FormatHours(stats.VideoSeconds)}"),
            new("Shorts / Reels", $"{stats.ShortForm:N0}"),
            new("Rejeitadas", $"{stats.Rejected:N0} · {LibraryStats.FormatBytes(stats.RejectedBytes)}"),
            new("Duplicatas exatas", $"{stats.DuplicateExtraCopies:N0} cópia(s) extra · {LibraryStats.FormatBytes(stats.DuplicateReclaimableBytes)} (só itens com hash calculado)"),
            new("Pode liberar", LibraryStats.FormatBytes(stats.ReclaimableBytes)),
            new("Sem avaliação", $"{stats.Unrated:N0}"),
            new("Ausentes", $"{stats.Missing:N0}")
        ];
        ByYear = Bars(stats.ByYear, stats.TotalBytes);
        ByFolder = Bars(stats.ByFolder, stats.TotalBytes);
        ByType = Bars(stats.ByExtension, stats.TotalBytes);
        LargestVideos = stats.LargestVideos.Select(v => new ToolPhotoViewModel(v)).ToList();
        foreach (var name in new[] { nameof(StorageLines), nameof(ByYear), nameof(ByFolder), nameof(ByType), nameof(LargestVideos) }) OnPropertyChanged(name);
        _ = LoadThumbnailsAsync(LargestVideos);
    }

    private static IReadOnlyList<BarItem> Bars(IReadOnlyList<StatBucket> buckets, long total)
    {
        var max = Math.Max(1, buckets.Count == 0 ? 1 : buckets.Max(b => b.Bytes));
        return buckets.Select(b => new BarItem(b.Label, $"{LibraryStats.FormatBytes(b.Bytes)} · {b.Count:N0}", b.Bytes * 100.0 / max,
            total > 0 ? string.Create(CultureInfo.CurrentCulture, $"{b.Bytes * 100.0 / total:0.#}% do total") + (b.Seconds > 0 ? $" · {LibraryStats.FormatHours(b.Seconds)} de vídeo" : string.Empty) : string.Empty)).ToList();
    }

    // ---------- backup ----------

    public ObservableCollection<BackupTargetViewModel> Backups { get; } = [];
    public RelayCommand AddBackupCommand { get; }
    public RelayCommand RemoveBackupCommand { get; }
    public RelayCommand VerifyBackupCommand { get; }
    public RelayCommand CopyMissingCommand { get; }
    public RelayCommand VerifyAllCommand { get; }
    public string BackupStatus { get => _backupStatus; private set { _backupStatus = value; OnPropertyChanged(); } }

    private async Task LoadBackupsAsync()
    {
        Backups.Clear();
        foreach (var target in await _pro.Repository.GetBackupTargetsAsync()) Backups.Add(new BackupTargetViewModel(target));
        UpdateBackupStatus();
    }

    private void UpdateBackupStatus()
    {
        VerifyAllCommand.RaiseCanExecuteChanged();
        var never = Backups.Count(b => !b.Target.IsVerified && b.Report is null);
        var problems = Backups.Count(b => b.HasProblems);
        var oldest = Backups.Where(b => b.Target.LastVerifiedUtc.HasValue).Select(b => b.Target.LastVerifiedUtc!.Value).DefaultIfEmpty().Min();
        BackupStatus = Backups.Count == 0 ? "Nenhum backup cadastrado. Adicione: pasta do catálogo → disco/pasta de backup."
            : problems > 0 ? $"{problems} backup(s) incompleto(s)." : never > 0 ? $"{never} backup(s) nunca conferido(s)."
            : oldest != default && DateTime.UtcNow - oldest > TimeSpan.FromDays(30) ? "Todos completos, mas há conferências com mais de 30 dias." : "Todos os backups completos.";
    }

    public async Task AddBackupAsync()
    {
        if (PickFolder("Pasta a proteger (origem)") is not { } source) return;
        if (PickFolder($"Onde está o backup de “{Path.GetFileName(source.TrimEnd('\\'))}”?") is not { } target) return;
        if (PhotoManager.Application.Transfer.TransferNames.IsSameOrInside(target, source) || PhotoManager.Application.Transfer.TransferNames.IsSameOrInside(source, target)) { BackupStatus = "O backup não pode ficar dentro da própria pasta (nem o contrário)."; return; }
        Backups.Add(new BackupTargetViewModel(await _pro.Repository.SaveBackupTargetAsync(null, source, target)));
        UpdateBackupStatus();
    }

    private async Task RemoveBackupAsync(BackupTargetViewModel target)
    {
        if (!_library.ConfirmAction("Deixar de conferir este backup? Nenhum arquivo é apagado.")) return;
        await _pro.Repository.DeleteBackupTargetAsync(target.Target.Id);
        Backups.Remove(target);
        UpdateBackupStatus();
    }

    public async Task VerifyAsync(BackupTargetViewModel target)
    {
        if (_pro.Backup is not { } verifier) return;
        target.IsBusy = true;
        try
        {
            var progress = new Progress<int>(n => target.ProgressText = $"Conferindo… {n:N0} arquivos");
            var report = await verifier.VerifyAsync(target.Target, progress);
            var now = DateTime.UtcNow;
            await _pro.Repository.UpdateBackupStatusAsync(target.Target.Id, now, report.Checked, report.Missing.Count, report.Different.Count);
            target.Target = target.Target with { LastVerifiedUtc = now, CheckedCount = report.Checked, MissingCount = report.Missing.Count, DifferentCount = report.Different.Count };
            target.Report = report;
            target.ProgressText = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { target.ProgressText = ex.Message; }
        finally { target.IsBusy = false; CopyMissingCommand.RaiseCanExecuteChanged(); VerifyBackupCommand.RaiseCanExecuteChanged(); UpdateBackupStatus(); }
    }

    private async Task VerifyAllAsync()
    {
        foreach (var target in Backups.ToList()) await VerifyAsync(target);
    }

    public async Task CopyMissingAsync(BackupTargetViewModel target)
    {
        if (_pro.Backup is not { } verifier || target.Report is not { } report) return;
        var count = report.Missing.Count + report.Different.Count;
        if (!_library.ConfirmAction($"Copiar {count:N0} arquivo(s) ({LibraryStats.FormatBytes(report.MissingBytes)}) para {target.Destination}?\n\nArquivos “diferentes” no backup são substituídos pela versão atual.")) return;
        target.IsBusy = true;
        try
        {
            var progress = new Progress<int>(n => target.ProgressText = $"Copiando {n:N0} de {count:N0}…");
            await verifier.CopyMissingAsync(target.Target, report, progress);
            target.ProgressText = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { target.ProgressText = $"Parou: {ex.Message}"; }
        finally { target.IsBusy = false; }
        await VerifyAsync(target);
    }
}
