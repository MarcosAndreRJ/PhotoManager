using System.Collections.ObjectModel;
using System.IO;
using PhotoManager.Application.Transfer;
using PhotoManager.Wpf.Commands;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

/// <summary>Um atalho da barra "Locais": disco, pasta do Windows, pasta fixada ou recente.</summary>
public sealed record TransferPlace(string Label, string Path, string Glyph, string Detail = "");

/// <summary>
/// Módulo Transferência: dois painéis independentes lado a lado e tudo que envolve os dois — copiar/mover (botões centrais, arrastar, colar) com
/// progresso e cancelamento, desfazer, comparação, barra de Locais (discos, fixadas, recentes) e o layout guardado entre sessões.
/// Criar/renomear/excluir num lado mantém o outro coerente: mesma pasta → atualiza; dentro da pasta renomeada → segue; dentro da excluída → sobe.
/// </summary>
public sealed class TransferViewModel : ViewModelBase, ITransferPaneHost
{
    private readonly ITransferOrganizer? _organizer;
    private readonly ITransferDialogs _dialogs;
    private readonly ITransferSettings? _settings;
    private readonly TransferUndoStack _undo = new();
    private CancellationTokenSource? _transferCts;
    private TransferPaneViewModel _activePane;
    private bool _isTransferring, _isComparing, _onlyDifferences, _showPlaces = true, _restoring;
    private double _progress, _leftRatio = 0.5;
    private string _progressText = string.Empty, _transferMessage = string.Empty;
    private int _saveVersion;

    public TransferViewModel(IFileThumbnailService? thumbnails = null, string? leftStart = null, string? rightStart = null, ITransferOrganizer? organizer = null,
        ITransferDialogs? dialogs = null, ITransferSettings? settings = null, IFileClipboard? clipboard = null)
    {
        _organizer = organizer;
        _dialogs = dialogs ?? new WpfTransferDialogs();
        _settings = settings;
        Clipboard = clipboard ?? new WpfFileClipboard();
        LeftPane = new TransferPaneViewModel(thumbnails, organizer: organizer, dialogs: _dialogs) { Host = this, IsActive = true };
        RightPane = new TransferPaneViewModel(thumbnails, organizer: organizer, dialogs: _dialogs) { Host = this };
        _activePane = LeftPane;
        foreach (var pane in new[] { LeftPane, RightPane })
        {
            var other = pane == LeftPane ? RightPane : LeftPane;
            pane.FolderChanged += (_, change) => Follow(other, change);
            pane.Navigated += (_, path) => OnPaneNavigated(pane, path);
            pane.LayoutChanged += (_, _) => ScheduleSave();
            pane.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(TransferPaneViewModel.SelectedItems) or nameof(TransferPaneViewModel.CurrentPath) or nameof(TransferPaneViewModel.HasError) or nameof(TransferPaneViewModel.IsBusy))
                    RaiseTransferCommands();
            };
        }

        CopyToRightCommand = new RelayCommand(_ => _ = SendAsync(LeftPane, RightPane, move: false), _ => CanSend(LeftPane, RightPane));
        MoveToRightCommand = new RelayCommand(_ => _ = SendAsync(LeftPane, RightPane, move: true), _ => CanSend(LeftPane, RightPane));
        CopyToLeftCommand = new RelayCommand(_ => _ = SendAsync(RightPane, LeftPane, move: false), _ => CanSend(RightPane, LeftPane));
        MoveToLeftCommand = new RelayCommand(_ => _ = SendAsync(RightPane, LeftPane, move: true), _ => CanSend(RightPane, LeftPane));
        CancelTransferCommand = new RelayCommand(_ => _transferCts?.Cancel(), _ => IsTransferring);
        UndoCommand = new RelayCommand(_ => _ = UndoAsync(), _ => CanUndo);
        SwapPanesCommand = new RelayCommand(_ => SwapPanes(), _ => LeftPane.HasFolder || RightPane.HasFolder);
        OpenPlaceCommand = new RelayCommand(parameter => { if (parameter is TransferPlace place) _ = ActivePane.NavigateAsync(place.Path); });
        UnpinCommand = new RelayCommand(parameter => { if (parameter is TransferPlace place) Unpin(place.Path); });
        ClearRecentCommand = new RelayCommand(_ => { Recent.Clear(); ScheduleSave(); }, _ => Recent.Count > 0);
        RefreshLocationsCommand = new RelayCommand(_ => LoadLocations());
        LoadLocations();

        var layout = _settings?.Load() ?? TransferLayout.Default;
        _restoring = true;
        LeftPane.ApplyLayout(layout.Left);
        RightPane.ApplyLayout(layout.Right);
        _leftRatio = Math.Clamp(layout.LeftRatio, 0.15, 0.85);
        _showPlaces = layout.ShowPlaces;
        foreach (var path in layout.Pinned ?? []) Pinned.Add(PlaceFor(path, ""));
        foreach (var path in layout.Recent ?? []) Recent.Add(PlaceFor(path, ""));
        _restoring = false;

        var start = leftStart ?? layout.Left.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrEmpty(start) && (leftStart is not null || layout.Left.Path is not null || Directory.Exists(start))) _ = LeftPane.NavigateAsync(start);
        var right = rightStart ?? layout.Right.Path;
        if (!string.IsNullOrEmpty(right)) _ = RightPane.NavigateAsync(right);
    }

    public TransferPaneViewModel LeftPane { get; }
    public TransferPaneViewModel RightPane { get; }
    public IFileClipboard Clipboard { get; }

    public RelayCommand CopyToRightCommand { get; }
    public RelayCommand MoveToRightCommand { get; }
    public RelayCommand CopyToLeftCommand { get; }
    public RelayCommand MoveToLeftCommand { get; }
    public RelayCommand CancelTransferCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand SwapPanesCommand { get; }
    public RelayCommand OpenPlaceCommand { get; }
    public RelayCommand UnpinCommand { get; }
    public RelayCommand ClearRecentCommand { get; }
    public RelayCommand RefreshLocationsCommand { get; }

    /// <summary>Painel com o foco: os Locais abrem nele.</summary>
    public TransferPaneViewModel ActivePane
    {
        get => _activePane;
        set
        {
            if (value is null || _activePane == value) return;
            _activePane = value;
            LeftPane.IsActive = value == LeftPane;
            RightPane.IsActive = value == RightPane;
            OnPropertyChanged();
        }
    }

    // ---------- progresso ----------
    public bool IsTransferring { get => _isTransferring; private set { _isTransferring = value; OnPropertyChanged(); RaiseTransferCommands(); } }
    public double ProgressPercent { get => _progress; private set { _progress = value; OnPropertyChanged(); } }
    public string ProgressText { get => _progressText; private set { _progressText = value; OnPropertyChanged(); } }
    /// <summary>Resumo da última transferência/desfazer.</summary>
    public string TransferMessage { get => _transferMessage; private set { _transferMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTransferMessage)); } }
    public bool HasTransferMessage => TransferMessage.Length > 0;

    // ---------- desfazer ----------
    public bool CanUndo => _undo.Count > 0 && !IsTransferring && _organizer is not null;
    public string UndoToolTip => _undo.Peek is { } next ? $"Desfazer: {next.Description} (Ctrl+Z)" : "Nada para desfazer";

    // ---------- comparação ----------
    public bool IsComparing { get => _isComparing; set { if (_isComparing == value) return; _isComparing = value; OnPropertyChanged(); Recompare(); } }
    public bool ShowOnlyDifferences
    {
        get => _onlyDifferences;
        set { if (_onlyDifferences == value) return; _onlyDifferences = value; OnPropertyChanged(); LeftPane.ShowOnlyDifferences = value; RightPane.ShowOnlyDifferences = value; }
    }
    public string CompareSummary { get; private set; } = string.Empty;

    // ---------- locais e layout ----------
    public IReadOnlyList<TransferPlace> Locations { get; private set; } = [];
    public ObservableCollection<TransferPlace> Pinned { get; } = [];
    public ObservableCollection<TransferPlace> Recent { get; } = [];
    public bool ShowPlaces { get => _showPlaces; set { if (_showPlaces == value) return; _showPlaces = value; OnPropertyChanged(); ScheduleSave(); } }
    /// <summary>Fração da largura do painel esquerdo (o divisor central).</summary>
    public double LeftRatio { get => _leftRatio; set { value = Math.Clamp(value, 0.15, 0.85); if (Math.Abs(_leftRatio - value) < 0.005) return; _leftRatio = value; OnPropertyChanged(); ScheduleSave(); } }

    public bool IsPinned(string? path) => path is not null && Pinned.Any(p => SamePath(p.Path, path));

    public void TogglePin(TransferPaneViewModel pane)
    {
        if (!pane.HasFolder) return;
        if (IsPinned(pane.CurrentPath)) Unpin(pane.CurrentPath);
        else { Pinned.Add(PlaceFor(pane.CurrentPath, "")); ScheduleSave(); OnPropertyChanged(nameof(Pinned)); }
        RaisePinState();
    }

    private void Unpin(string path)
    {
        foreach (var place in Pinned.Where(p => SamePath(p.Path, path)).ToList()) Pinned.Remove(place);
        ScheduleSave();
        RaisePinState();
    }

    private void RaisePinState()
    {
        LeftPane.IsPinned = IsPinned(LeftPane.CurrentPath);
        RightPane.IsPinned = IsPinned(RightPane.CurrentPath);
    }

    private void LoadLocations()
    {
        var list = new List<TransferPlace>();
        void Add(Environment.SpecialFolder folder, string label, string glyph)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) list.Add(new(label, path, glyph));
        }
        Add(Environment.SpecialFolder.DesktopDirectory, "Área de trabalho", "");
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) list.Add(new("Downloads", downloads, ""));
        Add(Environment.SpecialFolder.MyDocuments, "Documentos", "");
        Add(Environment.SpecialFolder.MyPictures, "Imagens", "");
        Add(Environment.SpecialFolder.MyVideos, "Vídeos", "");
        foreach (var drive in SafeDrives())
        {
            var name = drive.Name.TrimEnd('\\');
            var label = drive.DriveType switch { DriveType.Removable => "Removível", DriveType.Network => "Rede", DriveType.CDRom => "CD/DVD", _ => "Disco local" };
            try { if (!string.IsNullOrWhiteSpace(drive.VolumeLabel)) label = drive.VolumeLabel; } catch (IOException) { }
            var detail = string.Empty;
            try { detail = $"{TransferItemViewModel.FormatSize(drive.AvailableFreeSpace)} livres de {TransferItemViewModel.FormatSize(drive.TotalSize)}"; } catch (IOException) { }
            list.Add(new($"{label} ({name})", drive.Name, drive.DriveType == DriveType.Removable ? "" : drive.DriveType == DriveType.Network ? "" : "", detail));
        }
        Locations = list;
        OnPropertyChanged(nameof(Locations));
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch (IOException) { yield break; }
        foreach (var drive in drives)
        {
            bool ready;
            try { ready = drive.IsReady; } catch (IOException) { ready = false; }
            if (ready) yield return drive;
        }
    }

    private static TransferPlace PlaceFor(string path, string glyph)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        return new(string.IsNullOrEmpty(name) ? path : name, path, glyph, path);
    }

    private readonly object _navigationLock = new();

    /// <summary>Os dois painéis podem terminar de carregar ao mesmo tempo: recentes e comparação são atualizados um de cada vez.</summary>
    private void OnPaneNavigated(TransferPaneViewModel pane, string path)
    {
        lock (_navigationLock) OnPaneNavigatedCore(pane, path);
    }

    private void OnPaneNavigatedCore(TransferPaneViewModel pane, string path)
    {
        if (!_restoring)
        {
            var updated = TransferPlaces.AddRecent(Recent.Select(r => r.Path).ToList(), path);
            Recent.Clear();
            foreach (var recent in updated) Recent.Add(PlaceFor(recent, ""));
            ClearRecentCommand.RaiseCanExecuteChanged();
        }
        pane.IsPinned = IsPinned(path);
        Recompare();
        ScheduleSave();
    }

    // ---------- comparar ----------

    private void Recompare()
    {
        if (!_isComparing || !LeftPane.HasFolder || !RightPane.HasFolder)
        {
            LeftPane.ApplyCompare(null); RightPane.ApplyCompare(null);
            CompareSummary = _isComparing ? "Abra uma pasta em cada painel para comparar." : string.Empty;
            OnPropertyChanged(nameof(CompareSummary));
            return;
        }
        var (left, right) = FolderComparer.Compare(LeftPane.Entries, RightPane.Entries);
        LeftPane.ApplyCompare(left);
        RightPane.ApplyCompare(right);
        CompareSummary = $"Só à esquerda: {left.Values.Count(s => s == CompareState.OnlyHere)} · só à direita: {right.Values.Count(s => s == CompareState.OnlyHere)} · "
            + $"diferentes: {left.Values.Count(s => s == CompareState.Different)} · iguais: {left.Values.Count(s => s == CompareState.Same)}";
        OnPropertyChanged(nameof(CompareSummary));
    }

    // ---------- copiar / mover ----------

    private bool CanSend(TransferPaneViewModel from, TransferPaneViewModel to) =>
        _organizer is not null && !IsTransferring && from.SelectedItems.Count > 0 && to.HasFolder && !to.HasError && !to.IsBusy;

    /// <summary>Botões centrais: a seleção de um lado vai para a pasta aberta do outro.</summary>
    public Task SendAsync(TransferPaneViewModel from, TransferPaneViewModel to, bool move)
    {
        if (!CanSend(from, to)) return Task.CompletedTask;
        var paths = from.SelectedItems.Select(i => i.Path).ToList();
        return RunTransferAsync([new TransferJob(paths, to.CurrentPath)], move, $"{(move ? "mover" : "copiar")} {paths.Count} item(ns) para “{Path.GetFileName(to.CurrentPath.TrimEnd('\\'))}”");
    }

    public string? OtherPanePath(TransferPaneViewModel pane)
    {
        var other = pane == LeftPane ? RightPane : LeftPane;
        return other.HasFolder && !other.HasError && !SamePath(other.CurrentPath, pane.CurrentPath) ? other.CurrentPath : null;
    }

    public async Task<TransferBatchResult?> RunTransferAsync(IReadOnlyList<TransferJob> jobs, bool move, string description, bool createMissingDestinations = false)
    {
        if (_organizer is null || IsTransferring) return null;
        jobs = jobs.Select(j => new TransferJob(TransferConflicts.Normalize(j.Sources, j.Destination, move), j.Destination)).Where(j => j.Sources.Count > 0).ToList();
        if (jobs.Count == 0) { TransferMessage = move ? "Nada a mover: os itens já estão nesta pasta." : "Nada a copiar."; return null; }

        // Conflitos: perguntados uma única vez, para todos (copiar para a própria pasta não é conflito: vira cópia numerada).
        var conflicts = jobs.SelectMany(j => TransferConflicts.Find(j.Sources.Where(s => !SamePath(Path.GetDirectoryName(s), j.Destination)), j.Destination, p => File.Exists(p) || Directory.Exists(p))).ToList();
        var policy = ConflictPolicy.Skip;
        if (conflicts.Count > 0)
        {
            if (_dialogs.AskConflict(conflicts.Count, Path.GetFileName(conflicts[0]), move) is not { } chosen) { TransferMessage = "Transferência cancelada."; return null; }
            policy = chosen;
        }

        var createdFolders = new List<string>();
        var results = new List<TransferBatchResult>();
        var verb = move ? "Movendo" : "Copiando";
        _transferCts = new CancellationTokenSource();
        IsTransferring = true;
        ProgressPercent = 0;
        try
        {
            if (createMissingDestinations)
                foreach (var destination in jobs.Select(j => j.Destination).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var missing = new List<string>();
                    for (var folder = destination; !Directory.Exists(folder) && folder is not null; folder = Path.GetDirectoryName(folder)) missing.Add(folder);
                    Directory.CreateDirectory(destination);
                    createdFolders.AddRange(missing);
                }
            for (var index = 0; index < jobs.Count; index++)
            {
                var jobIndex = index;
                var progress = new Progress<TransferProgress>(p =>
                {
                    ProgressPercent = (jobIndex + p.Fraction) / jobs.Count * 100;
                    ProgressText = $"{verb} {p.FilesDone} de {p.FilesTotal} · {TransferItemViewModel.FormatSize(p.BytesDone)} de {TransferItemViewModel.FormatSize(p.BytesTotal)} · {Path.GetFileName(p.CurrentFile)}"
                        + (jobs.Count > 1 ? $"  (pasta {jobIndex + 1} de {jobs.Count})" : string.Empty);
                });
                var result = await _organizer.TransferAsync(new TransferRequest(jobs[index].Sources, jobs[index].Destination, move, policy), progress, _transferCts.Token);
                results.Add(result);
                if (result.Cancelled) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TransferMessage = $"Não foi possível {(move ? "mover" : "copiar")}: {ex.Message}"; }
        finally { IsTransferring = false; ProgressText = string.Empty; }

        var merged = new TransferBatchResult(results.SelectMany(r => r.Moves).ToList(), results.SelectMany(r => r.Created).ToList(), results.Sum(r => r.Files), results.Sum(r => r.Skipped),
            results.SelectMany(r => r.Failed).ToList(), results.Any(r => r.Cancelled));
        if (merged.Moves.Count > 0 || merged.Created.Count > 0 || createdFolders.Count > 0) PushUndo(new TransferUndoEntry(description, merged.Moves, merged.Created, createdFolders));
        if (results.Count > 0)
        {
            var text = $"{merged.Files} arquivo(s) {(move ? "movido(s)" : "copiado(s)")}";
            if (merged.Skipped > 0) text += $", {merged.Skipped} ignorado(s)";
            if (merged.Failed.Count > 0) text += $", {merged.Failed.Count} com erro ({merged.Failed[0].Error})";
            if (merged.Cancelled) text += " — cancelado pelo usuário (o que já foi feito pode ser desfeito)";
            TransferMessage = text + ".";
        }
        await AfterDiskChangeAsync(merged.Moves);
        return merged;
    }

    // ---------- desfazer ----------

    public void PushUndo(TransferUndoEntry entry)
    {
        _undo.Push(entry);
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(UndoToolTip));
        UndoCommand.RaiseCanExecuteChanged();
    }

    public async Task UndoAsync()
    {
        if (!CanUndo || _undo.Pop() is not { } entry) return;
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(UndoToolTip));
        IsTransferring = true;
        ProgressText = $"Desfazendo: {entry.Description}…";
        var problems = new List<string>();
        var reversed = entry.Moves.Reverse().Select(m => (m.To, m.From)).ToList();
        try
        {
            problems.AddRange((await _organizer!.MovePathsAsync(reversed)).Select(f => f.Error));
            if (entry.Created.Count > 0) problems.AddRange((await _organizer.RecycleAsync(entry.Created)).Failed.Select(f => f.Error));
            if (entry.CreatedFolders.Count > 0)
            {
                var kept = await _organizer.RemoveEmptyFoldersAsync(entry.CreatedFolders);
                if (kept.Count > 0) problems.Add($"{kept.Count} pasta(s) criada(s) não estavam vazias e foram mantidas");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add(ex.Message); }
        finally { IsTransferring = false; ProgressText = string.Empty; }
        TransferMessage = problems.Count == 0 ? $"Desfeito: {entry.Description}." : $"Desfeito em parte ({entry.Description}): {problems[0]}";
        await AfterDiskChangeAsync(reversed);
    }

    // ---------- apoio ----------

    /// <summary>Depois de mexer no disco: painel dentro de pasta que mudou de lugar segue a pasta; os demais atualizam.</summary>
    private async Task AfterDiskChangeAsync(IReadOnlyList<(string From, string To)> moves)
    {
        var tasks = new List<Task>();
        foreach (var pane in new[] { LeftPane, RightPane })
        {
            if (!pane.HasFolder) continue;
            var moved = moves.FirstOrDefault(m => TransferNames.IsSameOrInside(pane.CurrentPath, m.From) && Directory.Exists(m.To));
            tasks.Add(moved.From is not null ? pane.NavigateAsync(TransferNames.Rebase(pane.CurrentPath, moved.From, moved.To)) : pane.RefreshAsync());
        }
        await Task.WhenAll(tasks);
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(UndoToolTip));
        UndoCommand.RaiseCanExecuteChanged();
    }

    private void RaiseTransferCommands()
    {
        foreach (var command in new[] { CopyToRightCommand, MoveToRightCommand, CopyToLeftCommand, MoveToLeftCommand, CancelTransferCommand, UndoCommand, SwapPanesCommand })
            command?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndo));
    }

    private void SwapPanes()
    {
        var left = LeftPane.HasFolder ? LeftPane.CurrentPath : null;
        var right = RightPane.HasFolder ? RightPane.CurrentPath : null;
        if (right is not null) _ = LeftPane.NavigateAsync(right);
        if (left is not null) _ = RightPane.NavigateAsync(left);
    }

    public Task RefreshColorsAsync() => Task.WhenAll(LeftPane.RefreshColorsAsync(), RightPane.RefreshColorsAsync());

    private static void Follow(TransferPaneViewModel other, TransferFolderChange change)
    {
        if (!other.HasFolder) return;
        if (change.OldPath is { } old && TransferNames.IsSameOrInside(other.CurrentPath, old))
            _ = other.NavigateAsync(change.NewPath is { } renamed ? TransferNames.Rebase(other.CurrentPath, old, renamed) : change.Parent);
        else if (SamePath(other.CurrentPath, change.Parent))
            _ = other.RefreshAsync();
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Guarda o layout meio segundo depois da última mudança (o controle deslizante e o divisor mudam várias vezes por segundo).</summary>
    private async void ScheduleSave()
    {
        if (_settings is null || _restoring) return;
        var version = ++_saveVersion;
        try { await Task.Delay(500); } catch (TaskCanceledException) { return; }
        if (version == _saveVersion) SaveLayoutNow();
    }

    public void SaveLayoutNow()
    {
        if (_settings is null || _restoring) return;
        _settings.Save(new TransferLayout(LeftPane.GetLayout(), RightPane.GetLayout(), LeftRatio, ShowPlaces, Pinned.Select(p => p.Path).ToList(), Recent.Select(r => r.Path).ToList()));
    }
}
