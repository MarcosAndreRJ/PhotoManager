using System.Collections.ObjectModel;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Metadata;
using PhotoManager.Application.Microstock;
using PhotoManager.Application.Navigation;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Commands;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

public enum MicrostockFilter
{
    All,
    NotPrepared,
    MetadataIncomplete,
    ReadyForSubmission,
    PartiallyUploaded,
    UploadedToAll,
    Error,
    ChangedAfterUpload
}

public sealed class MicrostockPhotoRowViewModel : ViewModelBase
{
    public MicrostockPhotoRowViewModel(MicrostockPhotoEvaluation evaluation)
    {
        Evaluation = evaluation;
        Photo = evaluation.Photo;
    }

    public MicrostockPhotoEvaluation Evaluation { get; private set; }
    public Photo Photo { get; }
    public string FileName => Photo.FileName;
    public string Format => Photo.Extension.TrimStart('.').ToUpperInvariant();
    public string Dimensions => Photo.Width.HasValue && Photo.Height.HasValue ? $"{Photo.Width} × {Photo.Height}" : "—";
    public string MetadataSummary => Evaluation.Validation.IsValid ? "✓ Válida" : Evaluation.Validation.HasMinimumMetadata ? "⚠ Incompleta" : "— Sem metadata";
    public string MetadataDetails => Evaluation.Validation.IsValid ? "Passa no perfil ativo." : string.Join(" ", Evaluation.Validation.Issues.Select(issue => issue.Message));
    public MicrostockPreparationStatus Status => Evaluation.Status;
    public string StatusText => Status switch
    {
        MicrostockPreparationStatus.NotPrepared => "Não preparada",
        MicrostockPreparationStatus.MetadataIncomplete => "Metadata incompleto",
        MicrostockPreparationStatus.ReadyForSubmission => "Pronta para envio",
        MicrostockPreparationStatus.PartiallyUploaded => "Enviada parcialmente",
        MicrostockPreparationStatus.UploadedToAll => "Enviada para todos",
        MicrostockPreparationStatus.Error => "Com erro",
        MicrostockPreparationStatus.ChangedAfterUpload => "Alterada após envio",
        _ => Status.ToString()
    };
    public string AdobeStockStatus => GetAgencyStatus("Adobe Stock");
    public string ShutterstockStatus => GetAgencyStatus("Shutterstock");
    public string DepositphotosStatus => GetAgencyStatus("Depositphotos");
    public string DreamstimeStatus => GetAgencyStatus("Dreamstime");
    public string OneTwoThreeRfStatus => GetAgencyStatus("123RF");
    public string AgencyStatusSummary => Evaluation.Uploads is null || Evaluation.Uploads.Count == 0
        ? "Nenhum envio registrado"
        : string.Join(" · ", Evaluation.Uploads.OrderBy(upload => upload.AgencyName).Select(upload => $"{upload.AgencyName}: {FormatUploadStatus(upload.Status)}"));
    public IReadOnlyList<UploadRecordSnapshot> Uploads => Evaluation.Uploads ?? [];
    public string GetAgencyStatus(string agencyName)
    {
        var upload = Uploads.FirstOrDefault(item => string.Equals(item.AgencyName, agencyName, StringComparison.OrdinalIgnoreCase));
        return upload is null ? "— Não enviado" : FormatUploadStatus(upload.Status, upload.UploadedAt);
    }
    public void Update(MicrostockPhotoEvaluation evaluation)
    {
        Evaluation = evaluation;
        foreach (var property in new[] { nameof(MetadataSummary), nameof(MetadataDetails), nameof(Status), nameof(StatusText), nameof(AdobeStockStatus), nameof(ShutterstockStatus), nameof(DepositphotosStatus), nameof(DreamstimeStatus), nameof(OneTwoThreeRfStatus), nameof(AgencyStatusSummary), nameof(Uploads) }) OnPropertyChanged(property);
    }

    private static string FormatUploadStatus(UploadStatusSnapshot status, DateTime? at = null) => status switch
    {
        UploadStatusSnapshot.Uploaded => $"✓ Enviado{(at.HasValue ? $" {at.Value:dd/MM}" : string.Empty)}",
        UploadStatusSnapshot.Rejected => "✗ Rejeitado",
        UploadStatusSnapshot.Error => "✗ Erro",
        _ => "— Pendente"
    };
    private static string FormatUploadStatus(UploadRecordStatus status) => status switch
    {
        UploadRecordStatus.Uploaded => "✓ Enviado",
        UploadRecordStatus.Rejected => "✗ Rejeitado",
        UploadRecordStatus.Error => "✗ Erro",
        _ => "— Pendente"
    };
}

public sealed class AgencyViewModel : ViewModelBase
{
    private string _name;
    private bool _isActive;
    private bool _isSelected;
    private int _order;

    public AgencyViewModel(Agency agency) { Id = agency.Id; _name = agency.Name; _isActive = agency.IsActive; _order = agency.Order; }
    public long Id { get; }
    public string Name { get => _name; set { if (_name == value) return; _name = value; OnPropertyChanged(); } }
    public bool IsActive { get => _isActive; private set { if (_isActive == value) return; _isActive = value; OnPropertyChanged(); } }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); } }
    public int Order { get => _order; private set { if (_order == value) return; _order = value; OnPropertyChanged(); } }
    public void Apply(Agency agency) { Name = agency.Name; IsActive = agency.IsActive; Order = agency.Order; }
}

public sealed record AgencyFilterOption(long? Id, string Name);

public sealed class UploadHistoryRowViewModel(UploadRecord record)
{
    public string AgencyName => record.AgencyName;
    public string Status => record.Status switch { UploadRecordStatus.Uploaded => "Enviado", UploadRecordStatus.Rejected => "Rejeitado", UploadRecordStatus.Error => "Erro", _ => "Pendente" };
    public string DateText => record.UploadedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public string Details => string.Join(" · ", new[] { record.RemoteFileName, record.LastError, record.Notes }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public int MetadataVersion => record.MetadataVersion;
}

public sealed class MicrostockViewModel : ViewModelBase
{
    private readonly ICatalogService _catalog;
    private readonly IMetadataReader _reader;
    private readonly IValidationProfileRepository _profilesRepository;
    private readonly IMicrostockEvaluationService _evaluation;
    private readonly INavigationService _navigation;
    private readonly IUploadHistoryService? _uploadHistory;
    private readonly Dictionary<long, MicrostockPhotoRowViewModel> _rowsByPhoto = [];
    private CancellationTokenSource? _refreshCancellation;
    private List<Photo> _photos = [];
    private ValidationProfile? _activeProfile;
    private MicrostockFilter _filter;
    private MicrostockPhotoRowViewModel? _selectedRow;
    private readonly List<MicrostockPhotoRowViewModel> _selectedRows = [];
    private AgencyViewModel? _selectedAgency;
    private string _agencyNameDraft = string.Empty;
    private long? _agencyFilterId;
    private string _uploadStatusFilter = "Todos";
    private bool _agenciesLoaded;
    private bool _isLoading;
    private int _progressValue;
    private string _profileName = string.Empty;
    private bool _suppressProfileChange;
    private bool _titleRequired = true, _descriptionRequired, _authorRequired, _copyrightRequired, _requireEditableFormat;
    private string _titleMin = "1", _titleMax = "200", _descriptionMin = "0", _descriptionMax = "2000", _keywordsMin = "5", _keywordsMax = "50", _minimumMegapixels = string.Empty, _minimumRating = "0";

    public MicrostockViewModel(LibraryViewModel library, ICatalogService catalog, IMetadataReader reader, IValidationProfileRepository? profiles, IMicrostockEvaluationService? evaluation, INavigationService navigation, IUploadHistoryService? uploadHistory = null)
    {
        Library = library;
        _catalog = catalog;
        _reader = reader;
        _profilesRepository = profiles ?? new InMemoryValidationProfileRepository();
        _evaluation = evaluation ?? new MicrostockEvaluationService(reader);
        _navigation = navigation;
        _uploadHistory = uploadHistory;
        SelectFilterCommand = new RelayCommand(parameter => SelectFilter(parameter));
        OpenMetadataCommand = new RelayCommand(parameter => OpenMetadata(parameter as MicrostockPhotoRowViewModel), parameter => parameter is MicrostockPhotoRowViewModel);
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(true), _ => !IsLoading);
        NewProfileCommand = new RelayCommand(_ => NewProfile(), _ => !IsLoading);
        DuplicateProfileCommand = new RelayCommand(_ => _ = DuplicateProfileAsync(), _ => ActiveProfile is not null && !IsLoading);
        SaveProfileCommand = new RelayCommand(_ => _ = SaveProfileAsync(), _ => ActiveProfile is not null && !IsLoading);
        DeleteProfileCommand = new RelayCommand(_ => _ = DeleteProfileAsync(), _ => Profiles.Count > 1 && ActiveProfile is not null && !IsLoading);
        MarkUploadedCommand = new RelayCommand(_ => _ = MarkUploadedAsync(), _ => CanMark());
        MarkErrorCommand = new RelayCommand(_ => _ = MarkIssueAsync(UploadRecordStatus.Error), _ => CanMark());
        MarkRejectedCommand = new RelayCommand(_ => _ = MarkIssueAsync(UploadRecordStatus.Rejected), _ => CanMark());
        UndoMarkingCommand = new RelayCommand(_ => _ = UndoMarkingAsync(), _ => CanMark());
        AddAgencyCommand = new RelayCommand(_ => _ = AddAgencyAsync(), _ => !IsLoading && !string.IsNullOrWhiteSpace(AgencyNameDraft));
        SaveAgencyCommand = new RelayCommand(_ => _ = SaveAgencyAsync(), _ => !IsLoading && SelectedAgency is not null && !string.IsNullOrWhiteSpace(AgencyNameDraft));
        ToggleAgencyCommand = new RelayCommand(_ => _ = ToggleAgencyAsync(), _ => !IsLoading && SelectedAgency is not null);
        MoveAgencyUpCommand = new RelayCommand(_ => _ = MoveAgencyAsync(-1), _ => !IsLoading && SelectedAgency is not null);
        MoveAgencyDownCommand = new RelayCommand(_ => _ = MoveAgencyAsync(1), _ => !IsLoading && SelectedAgency is not null);
    }

    public LibraryViewModel Library { get; }
    public ObservableCollection<ValidationProfile> Profiles { get; } = [];
    public ObservableCollection<MicrostockPhotoRowViewModel> Rows { get; } = [];
    public ObservableCollection<MicrostockPhotoRowViewModel> FilteredRows { get; } = [];
    public ObservableCollection<AgencyViewModel> Agencies { get; } = [];
    public ObservableCollection<AgencyFilterOption> AgencyFilters { get; } = [new(null, "Todos os bancos")];
    public ObservableCollection<UploadHistoryRowViewModel> HistoryRows { get; } = [];
    public IReadOnlyList<string> UploadStatusFilters { get; } = ["Todos", "Pendente", "Enviado", "Rejeitado", "Erro"];
    public RelayCommand SelectFilterCommand { get; }
    public RelayCommand OpenMetadataCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand NewProfileCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand MarkUploadedCommand { get; }
    public RelayCommand MarkErrorCommand { get; }
    public RelayCommand MarkRejectedCommand { get; }
    public RelayCommand UndoMarkingCommand { get; }
    public RelayCommand AddAgencyCommand { get; }
    public RelayCommand SaveAgencyCommand { get; }
    public RelayCommand ToggleAgencyCommand { get; }
    public RelayCommand MoveAgencyUpCommand { get; }
    public RelayCommand MoveAgencyDownCommand { get; }
    public IReadOnlyList<string> AgencyColumns { get; } = ["Adobe Stock", "Shutterstock", "Depositphotos", "Dreamstime", "123RF"];

    public ValidationProfile? ActiveProfile
    {
        get => _activeProfile;
        set
        {
            if (ReferenceEquals(_activeProfile, value)) return;
            _activeProfile = value;
            OnPropertyChanged();
            LoadDraft(value);
            OnPropertyChanged(nameof(ActiveProfileText));
            if (!_suppressProfileChange && value is { Id: > 0 }) _ = ActivateProfileAsync(value);
        }
    }
    public string ActiveProfileText => ActiveProfile?.Name ?? "Nenhum perfil";
    public MicrostockFilter Filter { get => _filter; private set { _filter = value; OnPropertyChanged(); ApplyFilter(); } }
    public MicrostockPhotoRowViewModel? SelectedRow { get => _selectedRow; set { _selectedRow = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedIssues)); OnPropertyChanged(nameof(SelectedCountText)); OpenMetadataCommand.RaiseCanExecuteChanged(); _ = LoadHistoryAsync(); RaiseUploadCommands(); } }
    public IReadOnlyList<MicrostockPhotoRowViewModel> SelectedRows => _selectedRows;
    public string SelectedCountText => _selectedRows.Count == 0 ? "Nenhuma foto selecionada" : $"{_selectedRows.Count} foto(s) selecionada(s)";
    public AgencyViewModel? SelectedAgency { get => _selectedAgency; set { _selectedAgency = value; AgencyNameDraft = value?.Name ?? string.Empty; OnPropertyChanged(); ToggleAgencyCommand.RaiseCanExecuteChanged(); SaveAgencyCommand.RaiseCanExecuteChanged(); MoveAgencyUpCommand.RaiseCanExecuteChanged(); MoveAgencyDownCommand.RaiseCanExecuteChanged(); } }
    public string AgencyNameDraft { get => _agencyNameDraft; set { _agencyNameDraft = value; OnPropertyChanged(); AddAgencyCommand.RaiseCanExecuteChanged(); SaveAgencyCommand.RaiseCanExecuteChanged(); } }
    public long? AgencyFilterId { get => _agencyFilterId; set { if (_agencyFilterId == value) return; _agencyFilterId = value; OnPropertyChanged(); ApplyFilter(); } }
    public string UploadStatusFilter { get => _uploadStatusFilter; set { if (_uploadStatusFilter == value) return; _uploadStatusFilter = value; OnPropertyChanged(); ApplyFilter(); } }
    public string RemoteFileName { get; set; } = string.Empty;
    public string UploadNotes { get; set; } = string.Empty;
    public string IssueReason { get; set; } = string.Empty;
    public string OperationText { get; private set; } = string.Empty;
    public Func<string, bool> ConfirmAction { get; set; } = _ => true;
    public string SelectedIssues => SelectedRow is null || SelectedRow.Evaluation.Validation.IsValid ? "Nenhuma pendência no perfil ativo." : string.Join(Environment.NewLine, SelectedRow.Evaluation.Validation.Issues.Select(issue => "• " + issue.Message));
    public bool IsLoading { get => _isLoading; private set { _isLoading = value; OnPropertyChanged(); RefreshCommand.RaiseCanExecuteChanged(); SaveProfileCommand.RaiseCanExecuteChanged(); DeleteProfileCommand.RaiseCanExecuteChanged(); DuplicateProfileCommand.RaiseCanExecuteChanged(); RaiseUploadCommands(); AddAgencyCommand.RaiseCanExecuteChanged(); SaveAgencyCommand.RaiseCanExecuteChanged(); ToggleAgencyCommand.RaiseCanExecuteChanged(); MoveAgencyUpCommand.RaiseCanExecuteChanged(); MoveAgencyDownCommand.RaiseCanExecuteChanged(); } }
    public int ProgressValue { get => _progressValue; private set { _progressValue = value; OnPropertyChanged(); } }
    public int TotalCount => Rows.Count;
    public int NotPreparedCount => Rows.Count(row => row.Status == MicrostockPreparationStatus.NotPrepared);
    public int MetadataIncompleteCount => Rows.Count(row => row.Status == MicrostockPreparationStatus.MetadataIncomplete);
    public int ReadyCount => Rows.Count(row => row.Status == MicrostockPreparationStatus.ReadyForSubmission);
    public int PartialCount => Rows.Count(row => row.Status == MicrostockPreparationStatus.PartiallyUploaded);
    public int UploadedCount => Rows.Count(row => row.Status == MicrostockPreparationStatus.UploadedToAll);
    public int ErrorCount => Rows.Count(row => row.Status == MicrostockPreparationStatus.Error);
    public int ChangedCount => Rows.Count(row => row.Status == MicrostockPreparationStatus.ChangedAfterUpload);
    public string AllCountText => $"Todas  {TotalCount}";
    public string NotPreparedCountText => $"Sem metadata  {NotPreparedCount}";
    public string MetadataIncompleteCountText => $"Incompletas  {MetadataIncompleteCount}";
    public string ReadyCountText => $"Prontas  {ReadyCount}";
    public string PartialCountText => $"Parciais  {PartialCount}";
    public string UploadedCountText => $"Enviadas  {UploadedCount}";
    public string ErrorCountText => $"Com erro  {ErrorCount}";
    public string ChangedCountText => $"Alteradas  {ChangedCount}";
    public string ProgressText => IsLoading ? $"Lendo metadata… {ProgressValue}/{_photos.Count}" : $"{FilteredRows.Count} foto(s) exibida(s)";

    public string ProfileName { get => _profileName; set { _profileName = value; OnPropertyChanged(); } }
    public bool TitleRequired { get => _titleRequired; set { _titleRequired = value; OnPropertyChanged(); } }
    public bool DescriptionRequired { get => _descriptionRequired; set { _descriptionRequired = value; OnPropertyChanged(); } }
    public bool AuthorRequired { get => _authorRequired; set { _authorRequired = value; OnPropertyChanged(); } }
    public bool CopyrightRequired { get => _copyrightRequired; set { _copyrightRequired = value; OnPropertyChanged(); } }
    public bool RequireEditableFormat { get => _requireEditableFormat; set { _requireEditableFormat = value; OnPropertyChanged(); } }
    public string TitleMin { get => _titleMin; set { _titleMin = value; OnPropertyChanged(); } }
    public string TitleMax { get => _titleMax; set { _titleMax = value; OnPropertyChanged(); } }
    public string DescriptionMin { get => _descriptionMin; set { _descriptionMin = value; OnPropertyChanged(); } }
    public string DescriptionMax { get => _descriptionMax; set { _descriptionMax = value; OnPropertyChanged(); } }
    public string KeywordsMin { get => _keywordsMin; set { _keywordsMin = value; OnPropertyChanged(); } }
    public string KeywordsMax { get => _keywordsMax; set { _keywordsMax = value; OnPropertyChanged(); } }
    public string MinimumMegapixels { get => _minimumMegapixels; set { _minimumMegapixels = value; OnPropertyChanged(); } }
    public string MinimumRating { get => _minimumRating; set { _minimumRating = value; OnPropertyChanged(); } }

    public void SetActive(bool active)
    {
        if (active && !IsLoading && Profiles.Count == 0) _ = LoadProfilesAsync();
        else if (active && !IsLoading) _ = RefreshAsync(true);
        else if (!active) _refreshCancellation?.Cancel();
    }

    public Task RefreshAsync(bool reloadCatalog) => RefreshCoreAsync(reloadCatalog);

    public void SetSelectedRows(IEnumerable<MicrostockPhotoRowViewModel> rows)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(rows.Distinct());
        if (_selectedRows.Count > 0) _selectedRow = _selectedRows[0];
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(SelectedRows));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SelectedIssues));
        _ = LoadHistoryAsync();
        RaiseUploadCommands();
    }

    private async Task LoadProfilesAsync()
    {
        try
        {
            var profiles = await _profilesRepository.GetAllAsync();
            Profiles.Clear();
            foreach (var profile in profiles) Profiles.Add(profile);
            _suppressProfileChange = true;
            ActiveProfile = Profiles.FirstOrDefault(profile => profile.IsActive) ?? Profiles.FirstOrDefault();
            if (ActiveProfile is null)
            {
                var saved = await _profilesRepository.SaveAsync(ValidationProfile.GenericDefault with { IsActive = true });
                Profiles.Add(saved);
                ActiveProfile = saved;
            }
            _suppressProfileChange = false;
            await RefreshCoreAsync(true);
        }
        catch (Exception) { OnPropertyChanged(nameof(ProgressText)); }
    }

    private async Task RefreshCoreAsync(bool reloadCatalog)
    {
        if (ActiveProfile is null) { await LoadProfilesAsync(); return; }
        _refreshCancellation?.Cancel();
        _refreshCancellation = new CancellationTokenSource();
        var cancellationToken = _refreshCancellation.Token;
        IsLoading = true;
        ProgressValue = 0;
        try
        {
            if (reloadCatalog || _photos.Count == 0) _photos = (await _catalog.GetPhotosAsync(null, cancellationToken)).ToList();
            await LoadAgenciesAsync(cancellationToken);
            var progress = new Progress<int>(value => ProgressValue = value);
            var photoIds = _photos.Select(photo => photo.Id).ToList();
            var uploads = _uploadHistory is null ? [] : await _uploadHistory.GetCurrentSnapshotsAsync(photoIds, cancellationToken);
            var activeAgencyIds = Agencies.Where(agency => agency.IsActive).Select(agency => agency.Id).ToList();
            var evaluations = await _evaluation.EvaluateAsync(_photos, ActiveProfile, uploads, activeAgencyIds, progress, cancellationToken);
            var rows = evaluations.Select(evaluation => _rowsByPhoto.TryGetValue(evaluation.Photo.Id, out var existing) ? UpdateRow(existing, evaluation) : AddRow(evaluation)).ToList();
            Rows.Clear();
            foreach (var row in rows.OrderBy(row => row.FileName, StringComparer.CurrentCultureIgnoreCase)) Rows.Add(row);
            ApplyFilter();
            NotifyCounts();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally { IsLoading = false; OnPropertyChanged(nameof(ProgressText)); }
    }

    private async Task LoadAgenciesAsync(CancellationToken cancellationToken)
    {
        if (_uploadHistory is null)
        {
            if (_agenciesLoaded) return;
            foreach (var agency in new[] { "Adobe Stock", "Shutterstock", "Depositphotos", "Dreamstime", "123RF" }.Select((name, index) => new Agency(index + 1, name, true, index))) Agencies.Add(new AgencyViewModel(agency) { IsSelected = true });
            RebuildAgencyFilters();
            _agenciesLoaded = true;
            return;
        }

        var persisted = await _uploadHistory.GetAgenciesAsync(false, cancellationToken);
        var selectedIds = Agencies.Where(agency => agency.IsSelected).Select(agency => agency.Id).ToHashSet();
        Agencies.Clear();
        foreach (var agency in persisted.OrderBy(agency => agency.Order).ThenBy(agency => agency.Name, StringComparer.CurrentCultureIgnoreCase)) Agencies.Add(new AgencyViewModel(agency) { IsSelected = !_agenciesLoaded ? agency.IsActive : selectedIds.Contains(agency.Id) });
        RebuildAgencyFilters();
        _agenciesLoaded = true;
        if (SelectedAgency is not null) SelectedAgency = Agencies.FirstOrDefault(agency => agency.Id == SelectedAgency.Id);
        RaiseUploadCommands();
    }

    private void RebuildAgencyFilters()
    {
        var previous = AgencyFilterId;
        AgencyFilters.Clear();
        AgencyFilters.Add(new(null, "Todos os bancos"));
        foreach (var agency in Agencies.Where(agency => agency.IsActive).OrderBy(agency => agency.Order)) AgencyFilters.Add(new(agency.Id, agency.Name));
        AgencyFilterId = AgencyFilters.Any(option => option.Id == previous) ? previous : null;
    }

    private MicrostockPhotoRowViewModel AddRow(MicrostockPhotoEvaluation evaluation)
    {
        var row = new MicrostockPhotoRowViewModel(evaluation);
        _rowsByPhoto[evaluation.Photo.Id] = row;
        return row;
    }
    private static MicrostockPhotoRowViewModel UpdateRow(MicrostockPhotoRowViewModel row, MicrostockPhotoEvaluation evaluation) { row.Update(evaluation); return row; }
    private void ApplyFilter()
    {
        var filtered = Rows.Where(row => (_filter == MicrostockFilter.All || MatchesFilter(row.Status)) && MatchesUploadFilters(row));
        FilteredRows.Clear();
        foreach (var row in filtered) FilteredRows.Add(row);
        OnPropertyChanged(nameof(ProgressText));
    }
    private bool MatchesUploadFilters(MicrostockPhotoRowViewModel row)
    {
        if (AgencyFilterId.HasValue && !row.Uploads.Any(upload => upload.AgencyId == AgencyFilterId.Value)) return false;
        if (UploadStatusFilter == "Todos") return true;
        return row.Uploads.Any(upload => upload.Status switch
        {
            UploadStatusSnapshot.Pending => UploadStatusFilter == "Pendente",
            UploadStatusSnapshot.Uploaded => UploadStatusFilter == "Enviado",
            UploadStatusSnapshot.Rejected => UploadStatusFilter == "Rejeitado",
            UploadStatusSnapshot.Error => UploadStatusFilter == "Erro",
            _ => false
        });
    }
    private bool MatchesFilter(MicrostockPreparationStatus status) => _filter switch
    {
        MicrostockFilter.NotPrepared => status == MicrostockPreparationStatus.NotPrepared,
        MicrostockFilter.MetadataIncomplete => status == MicrostockPreparationStatus.MetadataIncomplete,
        MicrostockFilter.ReadyForSubmission => status == MicrostockPreparationStatus.ReadyForSubmission,
        MicrostockFilter.PartiallyUploaded => status == MicrostockPreparationStatus.PartiallyUploaded,
        MicrostockFilter.UploadedToAll => status == MicrostockPreparationStatus.UploadedToAll,
        MicrostockFilter.Error => status == MicrostockPreparationStatus.Error,
        MicrostockFilter.ChangedAfterUpload => status == MicrostockPreparationStatus.ChangedAfterUpload,
        _ => true
    };
    private void SelectFilter(object? parameter) { if (parameter is MicrostockFilter filter) Filter = filter; else if (Enum.TryParse<MicrostockFilter>(parameter?.ToString(), out var parsed)) Filter = parsed; }
    private void NotifyCounts()
    {
        foreach (var property in new[] { nameof(TotalCount), nameof(NotPreparedCount), nameof(MetadataIncompleteCount), nameof(ReadyCount), nameof(PartialCount), nameof(UploadedCount), nameof(ErrorCount), nameof(ChangedCount), nameof(AllCountText), nameof(NotPreparedCountText), nameof(MetadataIncompleteCountText), nameof(ReadyCountText), nameof(PartialCountText), nameof(UploadedCountText), nameof(ErrorCountText), nameof(ChangedCountText) }) OnPropertyChanged(property);
    }

    private bool CanMark() => !IsLoading && _uploadHistory is not null && _selectedRows.Count > 0 && Agencies.Any(agency => agency.IsSelected && agency.IsActive);
    private IReadOnlyList<long> SelectedPhotoIds => _selectedRows.Select(row => row.Photo.Id).Distinct().ToList();
    private IReadOnlyList<long> SelectedAgencyIds => Agencies.Where(agency => agency.IsSelected && agency.IsActive).Select(agency => agency.Id).ToList();
    private void RaiseUploadCommands()
    {
        MarkUploadedCommand?.RaiseCanExecuteChanged(); MarkErrorCommand?.RaiseCanExecuteChanged(); MarkRejectedCommand?.RaiseCanExecuteChanged(); UndoMarkingCommand?.RaiseCanExecuteChanged();
    }

    private async Task MarkUploadedAsync()
    {
        if (!CanMark() || _uploadHistory is null || !ConfirmAction("Registrar esta seleção como enviada?")) return;
        IsLoading = true;
        try
        {
            var result = await _uploadHistory.MarkUploadedAsync(SelectedPhotoIds, SelectedAgencyIds, DateTime.UtcNow, RemoteFileName, UploadNotes);
            OperationText = $"{result.RecordsCreated} marcação(ões) registrada(s).";
            await RefreshCoreAsync(false);
        }
        catch (Exception exception) { OperationText = "Falha ao registrar envio: " + exception.Message; }
        finally { IsLoading = false; OnPropertyChanged(nameof(OperationText)); }
    }

    private async Task MarkIssueAsync(UploadRecordStatus status)
    {
        if (!CanMark() || _uploadHistory is null || !ConfirmAction($"Registrar esta seleção como {status switch { UploadRecordStatus.Error => "erro", _ => "rejeitada" }}?")) return;
        IsLoading = true;
        try
        {
            var reason = string.IsNullOrWhiteSpace(IssueReason) ? "Problema registrado manualmente." : IssueReason.Trim();
            var result = await _uploadHistory.MarkIssueAsync(SelectedPhotoIds, SelectedAgencyIds, status, reason, DateTime.UtcNow, UploadNotes);
            OperationText = $"{result.RecordsCreated} problema(s) registrado(s).";
            await RefreshCoreAsync(false);
        }
        catch (Exception exception) { OperationText = "Falha ao registrar problema: " + exception.Message; }
        finally { IsLoading = false; OnPropertyChanged(nameof(OperationText)); }
    }

    private async Task UndoMarkingAsync()
    {
        if (!CanMark() || _uploadHistory is null || !ConfirmAction("Desfazer a marcação desta seleção?")) return;
        IsLoading = true;
        try
        {
            var result = await _uploadHistory.UndoMarkingAsync(SelectedPhotoIds, SelectedAgencyIds, DateTime.UtcNow, UploadNotes);
            OperationText = $"{result.RecordsCreated} marcação(ões) desfeita(s).";
            await RefreshCoreAsync(false);
        }
        catch (Exception exception) { OperationText = "Falha ao desfazer marcação: " + exception.Message; }
        finally { IsLoading = false; OnPropertyChanged(nameof(OperationText)); }
    }

    private async Task LoadHistoryAsync()
    {
        HistoryRows.Clear();
        if (_uploadHistory is null || SelectedRow is null) return;
        try { foreach (var record in await _uploadHistory.GetHistoryAsync(SelectedRow.Photo.Id)) HistoryRows.Add(new UploadHistoryRowViewModel(record)); }
        catch (Exception exception) { OperationText = "Falha ao carregar histórico: " + exception.Message; OnPropertyChanged(nameof(OperationText)); }
    }

    private async Task AddAgencyAsync()
    {
        if (_uploadHistory is null || string.IsNullOrWhiteSpace(AgencyNameDraft)) return;
        IsLoading = true;
        try { await _uploadHistory.SaveAgencyAsync(new Agency(0, AgencyNameDraft.Trim(), true, Agencies.Count)); await LoadAgenciesAsync(CancellationToken.None); SelectedAgency = Agencies.LastOrDefault(); OperationText = "Banco adicionado."; }
        catch (Exception exception) { OperationText = "Falha ao adicionar banco: " + exception.Message; }
        finally { IsLoading = false; OnPropertyChanged(nameof(OperationText)); }
    }

    private async Task SaveAgencyAsync()
    {
        if (_uploadHistory is null || SelectedAgency is null || string.IsNullOrWhiteSpace(AgencyNameDraft)) return;
        IsLoading = true;
        try { await _uploadHistory.SaveAgencyAsync(new Agency(SelectedAgency.Id, AgencyNameDraft.Trim(), SelectedAgency.IsActive, SelectedAgency.Order)); await LoadAgenciesAsync(CancellationToken.None); OperationText = "Banco salvo."; }
        catch (Exception exception) { OperationText = "Falha ao salvar banco: " + exception.Message; }
        finally { IsLoading = false; OnPropertyChanged(nameof(OperationText)); }
    }

    private async Task ToggleAgencyAsync()
    {
        if (_uploadHistory is null || SelectedAgency is null) return;
        IsLoading = true;
        try { await _uploadHistory.SetAgencyActiveAsync(SelectedAgency.Id, !SelectedAgency.IsActive); await LoadAgenciesAsync(CancellationToken.None); RebuildAgencyFilters(); OperationText = "Estado do banco atualizado."; }
        catch (Exception exception) { OperationText = "Falha ao atualizar banco: " + exception.Message; }
        finally { IsLoading = false; OnPropertyChanged(nameof(OperationText)); }
    }

    private async Task MoveAgencyAsync(int direction)
    {
        if (_uploadHistory is null || SelectedAgency is null) return;
        var index = Agencies.IndexOf(SelectedAgency); var target = index + direction;
        if (index < 0 || target < 0 || target >= Agencies.Count) return;
        var ids = Agencies.Select(agency => agency.Id).ToList(); (ids[index], ids[target]) = (ids[target], ids[index]);
        IsLoading = true;
        try { await _uploadHistory.SetAgencyOrderAsync(ids); await LoadAgenciesAsync(CancellationToken.None); SelectedAgency = Agencies.FirstOrDefault(agency => agency.Id == ids[target]); OperationText = "Ordem dos bancos atualizada."; }
        catch (Exception exception) { OperationText = "Falha ao reordenar bancos: " + exception.Message; }
        finally { IsLoading = false; OnPropertyChanged(nameof(OperationText)); }
    }
    private void OpenMetadata(MicrostockPhotoRowViewModel? row)
    {
        if (row is null) return;
        Library.SelectPhoto(row.Photo.Id);
        _navigation.Navigate("Metadata");
    }

    private void LoadDraft(ValidationProfile? profile)
    {
        if (profile is null) return;
        ProfileName = profile.Name;
        var rules = profile.Rules;
        TitleRequired = rules.TitleRequired; TitleMin = rules.TitleMinLength.ToString(); TitleMax = rules.TitleMaxLength.ToString();
        DescriptionRequired = rules.DescriptionRequired; DescriptionMin = rules.DescriptionMinLength.ToString(); DescriptionMax = rules.DescriptionMaxLength.ToString();
        KeywordsMin = rules.KeywordsMinCount.ToString(); KeywordsMax = rules.KeywordsMaxCount.ToString(); AuthorRequired = rules.AuthorRequired; CopyrightRequired = rules.CopyrightRequired;
        MinimumMegapixels = rules.MinimumMegapixels?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty; MinimumRating = rules.MinimumRating.ToString(); RequireEditableFormat = rules.RequireEditableFormat;
    }
    private ValidationRules ReadDraft() => new()
    {
        TitleRequired = TitleRequired, TitleMinLength = ParseInt(TitleMin, 1), TitleMaxLength = ParseInt(TitleMax, 200),
        DescriptionRequired = DescriptionRequired, DescriptionMinLength = ParseInt(DescriptionMin, 0), DescriptionMaxLength = ParseInt(DescriptionMax, 2000),
        KeywordsMinCount = Math.Max(0, ParseInt(KeywordsMin, 5)), KeywordsMaxCount = Math.Max(0, ParseInt(KeywordsMax, 50)),
        AuthorRequired = AuthorRequired, CopyrightRequired = CopyrightRequired, RequireEditableFormat = RequireEditableFormat,
        MinimumMegapixels = double.TryParse(MinimumMegapixels, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var megapixels) ? megapixels : null,
        MinimumRating = Math.Clamp(ParseInt(MinimumRating, 0), 0, 5)
    };
    private static int ParseInt(string value, int fallback) => int.TryParse(value, out var parsed) ? Math.Max(0, parsed) : fallback;
    private void NewProfile() { ActiveProfile = new ValidationProfile(0, "Novo perfil", new()); }
    private async Task ActivateProfileAsync(ValidationProfile profile)
    {
        try
        {
            await _profilesRepository.SetActiveAsync(profile.Id);
            _suppressProfileChange = true;
            Profiles.Clear(); foreach (var item in await _profilesRepository.GetAllAsync()) Profiles.Add(item);
            ActiveProfile = Profiles.FirstOrDefault(item => item.Id == profile.Id);
            _suppressProfileChange = false;
            await RefreshCoreAsync(false);
        }
        catch { _suppressProfileChange = false; }
    }
    private async Task DuplicateProfileAsync()
    {
        if (ActiveProfile is null) return;
        var copy = await _profilesRepository.SaveAsync(ActiveProfile with { Id = 0, Name = ActiveProfile.Name + " (cópia)", IsActive = false });
        Profiles.Add(copy); ActiveProfile = copy;
    }
    private async Task SaveProfileAsync()
    {
        if (ActiveProfile is null) return;
        var saved = await _profilesRepository.SaveAsync(ActiveProfile with { Name = ProfileName, Rules = ReadDraft() });
        if (ActiveProfile.Id == 0) Profiles.Add(saved); else for (var i = 0; i < Profiles.Count; i++) if (Profiles[i].Id == saved.Id) Profiles[i] = saved;
        ActiveProfile = saved;
        await _profilesRepository.SetActiveAsync(saved.Id);
        var activeProfiles = await _profilesRepository.GetAllAsync();
        Profiles.Clear(); foreach (var profile in activeProfiles) Profiles.Add(profile);
        ActiveProfile = Profiles.First(profile => profile.Id == saved.Id);
        await RefreshCoreAsync(false);
    }
    private async Task DeleteProfileAsync()
    {
        if (ActiveProfile is null || Profiles.Count <= 1) return;
        var deletedId = ActiveProfile.Id;
        await _profilesRepository.DeleteAsync(deletedId);
        Profiles.Clear(); foreach (var profile in await _profilesRepository.GetAllAsync()) Profiles.Add(profile);
        ActiveProfile = Profiles.FirstOrDefault(profile => profile.IsActive) ?? Profiles.First();
        await RefreshCoreAsync(false);
    }
}
