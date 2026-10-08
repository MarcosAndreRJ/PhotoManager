using System.Collections.ObjectModel;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Wpf.Commands;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

public sealed class DuplicatePhotoViewModel(PhotoManager.Domain.Photos.Photo photo) : ViewModelBase
{
    private Uri? _thumbnailUri;
    public PhotoManager.Domain.Photos.Photo Photo { get; } = photo;
    public string FileName => Photo.FileName;
    public string Path => Photo.CurrentPath;
    public string SizeText => Photo.FileSize switch { >= 1024 * 1024 => $"{Photo.FileSize / 1024d / 1024:0.0} MB", >= 1024 => $"{Photo.FileSize / 1024d:0} KB", _ => $"{Photo.FileSize} B" };
    public string ModifiedText => Photo.ModifiedAt.ToLocalTime().ToString("g");
    public string OrganizationText => string.Join(" · ", new[] { Photo.PersonalNote, Photo.Rating > 0 ? $"{Photo.Rating}★" : null, Photo.Tags.Count > 0 ? string.Join(", ", Photo.Tags) : null }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public Uri? ThumbnailUri { get => _thumbnailUri; set { _thumbnailUri = value; OnPropertyChanged(); } }
    private bool _isKeep;
    public bool IsKeep { get => _isKeep; set { if (_isKeep == value) return; _isKeep = value; OnPropertyChanged(); } }
}

public sealed class DuplicateGroupViewModel(DuplicateGroup group) : ViewModelBase
{
    public DuplicateGroup Group { get; } = group;
    public string Hash => Group.Hash;
    public string SizeText => Group.Photos[0].FileSize switch { >= 1024 * 1024 => $"{Group.Photos[0].FileSize / 1024d / 1024:0.0} MB", >= 1024 => $"{Group.Photos[0].FileSize / 1024d:0} KB", _ => $"{Group.Photos[0].FileSize} B" };
    public ObservableCollection<DuplicatePhotoViewModel> Photos { get; } = new(group.Photos.Select(photo => new DuplicatePhotoViewModel(photo)));
    public DuplicatePhotoViewModel? KeptPhoto => Photos.FirstOrDefault(photo => photo.IsKeep);
    public string Summary => $"{Photos.Count} cópias · {SizeText}";
    public void Keep(DuplicatePhotoViewModel photo)
    {
        foreach (var item in Photos) item.IsKeep = ReferenceEquals(item, photo);
        OnPropertyChanged(nameof(KeptPhoto));
    }
}

public sealed class ToolsViewModel : ViewModelBase
{
    /// <summary>Parecidas/desfocadas, armazenamento e backup (nulo sem os serviços "pró").</summary>
    public ProToolsViewModel? Pro { get; set; }
    public bool HasPro => Pro is not null;
    private readonly LibraryViewModel? _library;
    private readonly IDuplicateDetectionService? _duplicates;
    private readonly IFileOperationService? _fileOperations;
    private readonly IOrganizationService? _organization;
    private readonly IThumbnailService? _thumbnails;
    private readonly ICollectionService? _collectionService;
    private bool _isBusy;
    private string _statusText = "Pronto para procurar duplicatas exatas.";
    private int _progressValue;
    private DuplicateGroupViewModel? _selectedGroup;
    private string _destinationFolder = string.Empty;
    private bool _mergeOrganization;

    public ToolsViewModel() { ScanCommand = new RelayCommand(_ => { }); }
    public ToolsViewModel(LibraryViewModel library, IDuplicateDetectionService duplicates, IFileOperationService fileOperations, IThumbnailService? thumbnails = null, IOrganizationService? organization = null, ICollectionService? collectionService = null)
    {
        _library = library; _duplicates = duplicates; _fileOperations = fileOperations; _thumbnails = thumbnails; _organization = organization; _collectionService = collectionService;
        ScanCommand = new RelayCommand(_ => _ = ScanAsync(), _ => !IsBusy);
        IgnoreGroupCommand = new RelayCommand(_ => _ = IgnoreGroupAsync(), _ => !IsBusy && SelectedGroup is not null);
        KeepPhotoCommand = new RelayCommand(parameter => KeepPhoto(parameter as DuplicatePhotoViewModel), parameter => !IsBusy && SelectedGroup is not null && parameter is DuplicatePhotoViewModel);
        RecyclePhotoCommand = new RelayCommand(parameter => _ = RecyclePhotoAsync(parameter as DuplicatePhotoViewModel), parameter => !IsBusy && SelectedGroup?.KeptPhoto is not null && parameter is DuplicatePhotoViewModel item && !item.IsKeep);
        MovePhotoCommand = new RelayCommand(parameter => _ = MovePhotoAsync(parameter as DuplicatePhotoViewModel), parameter => !IsBusy && SelectedGroup?.KeptPhoto is not null && parameter is DuplicatePhotoViewModel item && !item.IsKeep && !string.IsNullOrWhiteSpace(DestinationFolder));
    }

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];
    public RelayCommand ScanCommand { get; }
    public RelayCommand IgnoreGroupCommand { get; } = new(_ => { });
    public RelayCommand KeepPhotoCommand { get; } = new(_ => { });
    public RelayCommand RecyclePhotoCommand { get; } = new(_ => { });
    public RelayCommand MovePhotoCommand { get; } = new(_ => { });
    public DuplicateGroupViewModel? SelectedGroup { get => _selectedGroup; set { _selectedGroup = value; OnPropertyChanged(); RaiseActionCommands(); } }
    public string DestinationFolder { get => _destinationFolder; set { _destinationFolder = value; OnPropertyChanged(); MovePhotoCommand.RaiseCanExecuteChanged(); } }
    public bool MergeOrganization { get => _mergeOrganization; set { _mergeOrganization = value; OnPropertyChanged(); } }
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; OnPropertyChanged(); ScanCommand.RaiseCanExecuteChanged(); RaiseActionCommands(); } }
    public int ProgressValue { get => _progressValue; private set { _progressValue = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }
    public string SummaryText => Groups.Count == 0 ? "Nenhum grupo encontrado." : $"{Groups.Count} grupo(s) de duplicatas exatas.";
    public Func<string, bool> ConfirmAction { get; set; } = _ => true;
    public event Action? ScanCompleted;

    public async Task ScanAsync()
    {
        if (_duplicates is null) return;
        IsBusy = true; ProgressValue = 0;
        try
        {
            var progress = new Progress<DuplicateScanProgress>(value => { ProgressValue = value.Total == 0 ? 0 : value.Processed * 100 / value.Total; StatusText = $"Hash SHA-256: {value.Processed}/{value.Total}"; });
            var result = await _duplicates.ScanAsync(progress);
            Groups.Clear(); foreach (var group in result.Groups) Groups.Add(new DuplicateGroupViewModel(group));
            await LoadThumbnailsAsync();
            StatusText = result.FailedCount == 0 ? $"{Groups.Count} grupo(s) encontrado(s)." : $"{Groups.Count} grupo(s); {result.FailedCount} arquivo(s) não puderam ser lidos.";
            OnPropertyChanged(nameof(SummaryText));
            await (_library?.ApplyFiltersAsync() ?? Task.CompletedTask);
            ScanCompleted?.Invoke();
        }
        catch (OperationCanceledException) { StatusText = "Busca de duplicatas cancelada."; }
        catch (Exception exception) { StatusText = "Falha na busca: " + exception.Message; }
        finally { IsBusy = false; }
    }

    private async Task LoadThumbnailsAsync()
    {
        if (_thumbnails is null) return;
        foreach (var photo in Groups.SelectMany(group => group.Photos))
        {
            try
            {
                var path = await _thumbnails.GetOrCreateAsync(photo.Photo.Id, photo.Photo.CurrentPath);
                if (path is not null) photo.ThumbnailUri = new Uri(path);
            }
            catch (Exception) { }
        }
    }

    private void KeepPhoto(DuplicatePhotoViewModel? photo) { if (photo is not null) SelectedGroup?.Keep(photo); RaiseActionCommands(); }
    private async Task IgnoreGroupAsync()
    {
        if (_duplicates is null || SelectedGroup is null) return;
        if (!ConfirmAction("Ignorar este grupo de duplicatas?")) return;
        await _duplicates.IgnoreGroupAsync(SelectedGroup.Hash); Groups.Remove(SelectedGroup); SelectedGroup = Groups.FirstOrDefault(); OnPropertyChanged(nameof(SummaryText)); await (_library?.ApplyFiltersAsync() ?? Task.CompletedTask);
    }
    private async Task RecyclePhotoAsync(DuplicatePhotoViewModel? photo)
    {
        if (_fileOperations is null || SelectedGroup?.KeptPhoto is null || photo is null || photo.IsKeep) return;
        if (!ConfirmAction($"Enviar para a Lixeira:\n\n{photo.Path}\n\nA cópia mantida será preservada.")) return;
        IsBusy = true;
        try
        {
            if (MergeOrganization && _organization is not null)
            {
                var kept = SelectedGroup.KeptPhoto.Photo;
                kept.Tags = kept.Tags.Concat(photo.Photo.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                kept.Rating = Math.Max(kept.Rating, photo.Photo.Rating);
                if (string.IsNullOrWhiteSpace(kept.PersonalNote)) kept.PersonalNote = photo.Photo.PersonalNote;
                await _organization.SaveAsync(kept);

                if (_collectionService is not null)
                {
                    var toAdd = photo.Photo.CollectionIds.Except(kept.CollectionIds).ToList();
                    foreach (var colId in toAdd)
                    {
                        await _collectionService.AddPhotosAsync(colId, [kept.Id]);
                        if (!kept.CollectionIds.Contains(colId)) kept.CollectionIds.Add(colId);
                    }
                    await _collectionService.SyncPhotoCollectionsAsync([kept]);
                }
            }
            await _fileOperations.MoveToRecycleBinAsync(photo.Photo);
            StatusText = $"{photo.FileName} enviado para a Lixeira.";
            await RefreshAfterActionAsync();
        }
        catch (Exception exception) { StatusText = "Falha ao enviar para a Lixeira: " + exception.Message; }
        finally { IsBusy = false; }
    }
    private async Task MovePhotoAsync(DuplicatePhotoViewModel? photo)
    {
        if (_fileOperations is null || SelectedGroup?.KeptPhoto is null || photo is null || photo.IsKeep || string.IsNullOrWhiteSpace(DestinationFolder)) return;
        IsBusy = true;
        try { await _fileOperations.MoveAsync(photo.Photo, DestinationFolder); StatusText = $"{photo.FileName} movido."; await RefreshAfterActionAsync(); }
        catch (Exception exception) { StatusText = "Falha ao mover: " + exception.Message; }
        finally { IsBusy = false; }
    }
    private async Task RefreshAfterActionAsync() { await (_library?.ApplyFiltersAsync() ?? Task.CompletedTask); await ScanAsync(); }
    private void RaiseActionCommands() { IgnoreGroupCommand.RaiseCanExecuteChanged(); KeepPhotoCommand.RaiseCanExecuteChanged(); RecyclePhotoCommand.RaiseCanExecuteChanged(); MovePhotoCommand.RaiseCanExecuteChanged(); }
}

