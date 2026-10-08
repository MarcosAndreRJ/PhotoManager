using System.Collections.ObjectModel;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Application.Metadata;
using PhotoManager.Application.Microstock;
using PhotoManager.Application.Navigation;
using PhotoManager.Wpf.Commands;

namespace PhotoManager.Wpf;

public sealed class NavigationTabViewModel(NavigationItem item) : ViewModels.ViewModelBase
{
    private bool _isSelected;
    public string Key { get; } = item.Key;
    public string Label { get; } = item.Label;
    /// <summary>Glifo da fonte de ícones do Windows (Segoe Fluent Icons / MDL2).</summary>
    public string Icon { get; } = item.Key switch
    {
        "Library" => "",
        "Metadata" => "",
        "Microstock" => "",
        "Transfer" => "",
        "Tools" => "",
        "Settings" => "",
        _ => ""
    };
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
}

public sealed class MainViewModel : ViewModels.ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly Views.LibraryViewModel _library;
    private readonly Views.MetadataEditorViewModel _metadata;
    private readonly Views.MicrostockViewModel _microstock;
    private readonly IFileOperationService _fileOperations;
    private readonly IThumbnailService _thumbnails;
    private readonly IOrganizationService _organization;
    private readonly IDuplicateDetectionService? _duplicateDetection;
    private readonly ICollectionService? _collectionService;
    private readonly PhotoManager.Application.Transfer.ITransferOrganizer? _transferOrganizer;
    private readonly PhotoManager.Application.Transfer.ITransferSettings? _transferSettings;
    private readonly Dictionary<string, object> _pages = [];
    private object _currentView;

    public MainViewModel(INavigationService navigation, ICatalogService catalog, IThumbnailService thumbnails, IOrganizationService organization, IFileOperationService fileOperations, IMetadataReader metadataReader, IMetadataEditService metadataEditor, IValidationProfileRepository? validationProfiles = null, IMicrostockEvaluationService? microstockEvaluation = null, IUploadHistoryService? uploadHistory = null, IDuplicateDetectionService? duplicateDetection = null, IDuplicateRepository? duplicateRepository = null, IMetadataPresetRepository? metadataPresets = null, ICollectionService? collectionService = null, PhotoManager.Application.Location.ILocationService? locationService = null, PhotoManager.Application.Transfer.ITransferOrganizer? transferOrganizer = null, PhotoManager.Application.Transfer.ITransferSettings? transferSettings = null, Views.LibraryProServices? libraryPro = null)
    {
        _navigation = navigation;
        _transferOrganizer = transferOrganizer;
        _transferSettings = transferSettings;
        _fileOperations = fileOperations;
        _thumbnails = thumbnails;
        _organization = organization;
        _duplicateDetection = duplicateDetection;
        _collectionService = collectionService;
        _library = new Views.LibraryViewModel(catalog, thumbnails, organization, fileOperations, duplicateRepository, collectionService, locationService);
        if (libraryPro is not null) { _library.Pro = libraryPro; if (libraryPro.Settings is { } librarySettings) ThemeService.Apply(librarySettings.Load().Theme); }
        _metadata = new Views.MetadataEditorViewModel(_library, metadataReader, metadataEditor, metadataPresets);
        _microstock = new Views.MicrostockViewModel(_library, catalog, metadataReader, validationProfiles, microstockEvaluation, navigation, uploadHistory);
        _library.Review = new Views.ReviewViewModel(_library, metadataReader, metadataEditor, validationProfiles, microstockEvaluation, uploadHistory, navigation);
        if (transferOrganizer is not null) transferOrganizer.CatalogChanged += (_, change) => _ = _library.ApplyTransferChangeAsync(change);
        _library.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Views.LibraryViewModel.IsFullScreen)) { OnPropertyChanged(nameof(IsFullScreen)); OnPropertyChanged(nameof(ShowNavigation)); } };
        NavigationItems = new(navigation.Items.Select(item => new NavigationTabViewModel(item)));
        NavigateCommand = new RelayCommand(parameter => _navigation.Navigate(parameter as string ?? "Library"));
        _currentView = _library;
        SelectedNavigationKey = navigation.CurrentKey;
        MarkSelected(SelectedNavigationKey);
        _navigation.Navigated += (_, key) =>
        {
            SelectedNavigationKey = key;
            _metadata.SetActive(key == "Metadata");
            _microstock.SetActive(key == "Microstock");
            MarkSelected(key);
            CurrentView = CreateViewModel(key);
            if (CurrentView is Views.TransferViewModel transfer) _ = transfer.RefreshColorsAsync();   // cores marcadas na Biblioteca aparecem ao voltar
        };
    }

    public ObservableCollection<NavigationTabViewModel> NavigationItems { get; }
    public RelayCommand NavigateCommand { get; }
    /// <summary>Espelha a tela cheia do modo de revisão; a janela reage a isto (sem moldura, maximizada, sem barra de navegação).</summary>
    public bool IsFullScreen => _library.IsFullScreen;
    public bool ShowNavigation => !IsFullScreen;
    public string SelectedNavigationKey { get; private set; }
    public object CurrentView { get => _currentView; private set { _currentView = value; OnPropertyChanged(); } }

    private void MarkSelected(string key)
    {
        foreach (var item in NavigationItems) item.IsSelected = item.Key == key;
    }

    /// <summary>A Biblioteca é única durante a sessão para preservar filtros, seleção e miniaturas ao navegar entre áreas.</summary>
    private object CreateViewModel(string key)
    {
        if (key == "Library") return _library;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "Metadata" => _metadata,
                "Microstock" => _microstock,
                "Transfer" => new Views.TransferViewModel(_thumbnails as PhotoManager.Application.Transfer.IFileThumbnailService, organizer: _transferOrganizer, settings: _transferSettings),
                "Tools" => CreateTools(),
                "Settings" => new Views.SettingsViewModel(_library),
                _ => _library
            };
            _pages[key] = page;
        }
        return page;
    }

    private Views.ToolsViewModel CreateTools()
    {
        var tools = _duplicateDetection is null ? new Views.ToolsViewModel() : new Views.ToolsViewModel(_library, _duplicateDetection, _fileOperations, _thumbnails, _organization, _collectionService);
        if (_library.Pro is { } pro) tools.Pro = new Views.ProToolsViewModel(_library, pro) { NavigateToLibrary = () => _navigation.Navigate("Library") };
        return tools;
    }
}
