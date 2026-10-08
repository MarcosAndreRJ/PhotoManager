using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Commands;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

public enum SidebarKind { Smart, Folder, Category, Tag, Collection, SmartCollection, Person }

public sealed class SidebarEntry(SidebarKind kind, string key, string label, string icon, int count, bool isVirtual = false, string? toolTip = null, bool hasSeparatorAfter = false, long? id = null) : ViewModelBase
{
    private int _count = count;
    private bool _isSelected;
    public SidebarKind Kind { get; } = kind;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    public bool IsVirtual { get; } = isVirtual;
    public string? ToolTip { get; } = toolTip;
    public bool HasSeparatorAfter { get; } = hasSeparatorAfter;
    public long? Id { get; } = id;
    public string ToolTipText => !string.IsNullOrEmpty(ToolTip) ? ToolTip : Key;
    public int Count { get => _count; set { _count = value; OnPropertyChanged(); } }
    private bool _showCount = true;
    /// <summary>Contagem igual ao total do catálogo não informa nada: fica escondida (exceto em "Todas").</summary>
    public bool ShowCount { get => _showCount; set { if (_showCount == value) return; _showCount = value; OnPropertyChanged(); } }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
}

public sealed partial class LibraryViewModel : ViewModelBase
{
    public const string AllChoice = "Todas";
    public const string AnyCollectionChoice = "Qualquer";
    public const string AllCollectionsKey = "__todas_colecoes__";
    public const string NoCollectionKey = "__sem_colecao__";
    private const int MaxTagEntries = 40;
    private const int PreviewDecodeWidth = 1600;

    private readonly ICatalogService _catalog;
    private readonly IThumbnailService _thumbnails;
    private readonly IOrganizationService _organization;
    private readonly IFileOperationService _fileOperations;
    private readonly IDuplicateRepository? _duplicateRepository;
    private readonly ICollectionService? _collectionService;
    private long? _collectionIdFilter;
    public const string AnyOrientation = "Qualquer";
    private string _orientationFilter = AnyOrientation;
    /// <summary>Item virtual "Todas" de uma coleção com subcoleções: filtra a coleção e todos os seus descendentes.</summary>
    private long? _subtreeRootId;
    private HashSet<long> _subtreeIds = [];
    private readonly Dictionary<long, Uri?> _thumbnailCache = [];
    private Dictionary<long, PhotoCardViewModel> _cards = [];
    private List<Photo> _all = [];
    private PhotoCardViewModel? _selectedPhoto;
    private ImageSource? _previewImage;
    private string _statusText = "Carregando catálogo…";
    private bool _isBusy;
    private string _searchText = string.Empty;
    private string _categoryFilter = string.Empty;
    private string _tagFilter = string.Empty;
    private string _collectionFilter = string.Empty;
    private string _folderFilter = string.Empty;
    private string _smartList = "all";
    private bool _favoritesOnly;
    private bool _noCollectionOnly;
    private bool _allCollectionsOnly;
    private int _minimumRating;
    private int _loadVersion;
    private int _viewVersion;
    private int _previewVersion;
    private bool _suspendViewUpdates;
    private HashSet<long> _duplicatePhotoIds = [];
    private readonly Dictionary<long, string> _collectionPaths = [];
    private readonly Dictionary<long, bool> _collectionExpansion = [];

    public LibraryViewModel(ICatalogService catalog, IThumbnailService thumbnails, IOrganizationService organization, IFileOperationService fileOperations, IDuplicateRepository? duplicateRepository = null, ICollectionService? collectionService = null, PhotoManager.Application.Location.ILocationService? locationService = null)
    {
        _locationService = locationService;
        _catalog = catalog;
        _thumbnails = thumbnails;
        _organization = organization;
        _fileOperations = fileOperations;
        _duplicateRepository = duplicateRepository;
        _collectionService = collectionService;
        SelectFolderCommand = new RelayCommand(parameter => { if (parameter is FolderNode node) SelectFolder(node); });
        InitFileMenuCommands();
        InitTreeCommands();
        InitColorChoices();
        FindLocationCommand = new RelayCommand(parameter => { if (parameter is PhotoCardViewModel card) _ = FindLocationAsync(card); });
        OpenMapCommand = new RelayCommand(parameter => { if (parameter is PhotoCardViewModel { HasGps: true } card) OpenMap(card); });
        ApplyOrganizationBatchCommand = new RelayCommand(_ => _ = ApplyOrganizationBatchAsync(), _ => CanApplyOrganizationBatch);
        EnterReviewCommand = new RelayCommand(parameter => EnterReview(parameter as PhotoCardViewModel), _ => HasSelectedPhoto);
        ExitReviewCommand = new RelayCommand(_ => ExitReview());
        ToggleFullScreenCommand = new RelayCommand(_ => { if (IsReviewMode) IsFullScreen = !IsFullScreen; });
        SelectSidebarCommand = new RelayCommand(parameter => { if (parameter is SidebarEntry entry) SelectSidebar(entry); });
        ToggleFavoriteCommand = new RelayCommand(parameter => { if (parameter is PhotoCardViewModel card) _ = ToggleFavoriteAsync(card); });
        PreviousCommand = new RelayCommand(_ => MoveSelection(-1), _ => CanMoveSelection(-1));
        NextCommand = new RelayCommand(_ => MoveSelection(1), _ => CanMoveSelection(1));
        ClearFiltersCommand = new RelayCommand(_ => ClearFiltersAndApply());

        CreateRootCollectionCommand = new RelayCommand(_ => _ = CreateRootCollectionAsync());
        CreateSubcollectionCommand = new RelayCommand(parameter => _ = CreateSubcollectionAsync(parameter as CollectionNode));
        RenameCollectionCommand = new RelayCommand(parameter => _ = RenameCollectionAsync(parameter as CollectionNode));
        MoveCollectionCommand = new RelayCommand(parameter => _ = MoveCollectionAsync(parameter as CollectionNode));
        DeleteCollectionCommand = new RelayCommand(parameter => _ = DeleteCollectionAsync(parameter as CollectionNode));
        RemovePhotoFromCollectionCommand = new RelayCommand(parameter =>
        {
            if (parameter is CollectionItemViewModel item && SelectedPhoto is not null)
                _ = RemovePhotoFromCollectionAsync(SelectedPhoto, item.Id);
        });
        RemoveBatchCollectionCommand = new RelayCommand(parameter =>
        {
            if (parameter is CollectionItemViewModel item)
                RemoveBatchCollection(item);
        });
        RemoveCommonCollectionCommand = new RelayCommand(parameter =>
        {
            if (parameter is CollectionItemViewModel item) _ = RemoveCommonCollectionsAsync([item.Id]);
        });
        RemoveAllCommonCollectionsCommand = new RelayCommand(_ => _ = RemoveCommonCollectionsAsync(CommonCollections.Select(c => c.Id).ToList()));

        _ = LoadAsync();
    }

    public RangeObservableCollection<PhotoCardViewModel> Photos { get; } = [];
    public ObservableCollection<SidebarEntry> SmartLists { get; } = [];
    /// <summary>Raízes da árvore de pastas (subpastas em <see cref="FolderNode.Children"/>).</summary>
    public ObservableCollection<FolderNode> Folders { get; } = [];
    public RelayCommand SelectFolderCommand { get; }
    public string CurrentFolderFilter => _folderFilter;
    private readonly Dictionary<string, bool> _folderExpansion = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<SidebarEntry> CategoryEntries { get; } = [];
    public ObservableCollection<SidebarEntry> TagEntries { get; } = [];
    public ObservableCollection<SidebarEntry> CollectionEntries { get; } = [];
    public ObservableCollection<SidebarEntry> VirtualCollectionEntries { get; } = [];
    public ObservableCollection<CollectionNode> CollectionNodes { get; } = [];
    private CollectionNode? _selectedCollectionNode;
    public CollectionNode? SelectedCollectionNode
    {
        get => _selectedCollectionNode;
        set
        {
            if (_selectedCollectionNode == value) return;
            _selectedCollectionNode = value;
            OnPropertyChanged();
        }
    }

    private bool _isDragOverRoot;
    public bool IsDragOverRoot
    {
        get => _isDragOverRoot;
        set
        {
            if (_isDragOverRoot == value) return;
            _isDragOverRoot = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<CollectionItemViewModel> SelectedPhotoCollections { get; } = [];
    public ObservableCollection<CollectionItemViewModel> AvailableCollectionsForSelectedPhoto { get; } = [];
    private CollectionItemViewModel? _selectedCollectionToAdd;
    public CollectionItemViewModel? SelectedCollectionToAdd
    {
        get => _selectedCollectionToAdd;
        set
        {
            _selectedCollectionToAdd = value;
            OnPropertyChanged();
            if (value is not null && SelectedPhoto is not null)
            {
                _ = AddPhotoToCollectionAsync(SelectedPhoto, value.Id);
                _selectedCollectionToAdd = null;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<CollectionItemViewModel> BatchSelectedCollections { get; } = [];
    public ObservableCollection<CollectionItemViewModel> AvailableCollectionsForBatch { get; } = [];
    private CollectionItemViewModel? _selectedBatchCollectionToAdd;
    public CollectionItemViewModel? SelectedBatchCollectionToAdd
    {
        get => _selectedBatchCollectionToAdd;
        set
        {
            _selectedBatchCollectionToAdd = value;
            OnPropertyChanged();
            if (value is not null)
            {
                AddBatchCollection(value);
                _selectedBatchCollectionToAdd = null;
                OnPropertyChanged();
            }
        }
    }

    public RelayCommand CreateRootCollectionCommand { get; }
    public RelayCommand CreateSubcollectionCommand { get; }
    public RelayCommand RenameCollectionCommand { get; }
    public RelayCommand MoveCollectionCommand { get; }
    public RelayCommand DeleteCollectionCommand { get; }
    public RelayCommand RemovePhotoFromCollectionCommand { get; }
    public RelayCommand RemoveBatchCollectionCommand { get; }

    public Func<CollectionNameViewModel, bool?> ShowNameDialog { get; set; } = vm => ShowOwned(new CollectionNameDialog(vm));
    public Func<DeleteCollectionViewModel, bool?> ShowDeleteDialog { get; set; } = vm => ShowOwned(new DeleteCollectionDialog(vm));
    public Func<MoveCollectionViewModel, bool?> ShowMoveDialog { get; set; } = vm => ShowOwned(new MoveCollectionDialog(vm));

    /// <summary>Abre o diálogo modal centralizado sobre a janela principal (sem dono ele podia abrir atrás dela).</summary>
    private static bool? ShowOwned(System.Windows.Window dialog)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is { IsLoaded: true } && owner.Dispatcher.CheckAccess() && !ReferenceEquals(owner, dialog)) dialog.Owner = owner;
        return dialog.ShowDialog();
    }
    public Action<string, string> ShowMessage { get; set; } = (msg, title) => System.Windows.MessageBox.Show(msg, title);

    public ObservableCollection<string> CategoryChoices { get; } = [AllChoice];
    public ObservableCollection<string> TagChoices { get; } = [AllChoice];
    public ObservableCollection<string> CollectionChoices { get; } = [AnyCollectionChoice];

    public RelayCommand SelectSidebarCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }

    // Filtros "brutos": alterá-los não refiltra sozinho; ApplyFiltersAsync() recarrega do catálogo e aplica.
    public string SearchText { get => _searchText; set { _searchText = value; OnPropertyChanged(); } }
    public string CategoryFilter { get => _categoryFilter; set { _categoryFilter = value; OnPropertyChanged(); OnPropertyChanged(nameof(CategoryChoice)); } }
    public string TagFilter { get => _tagFilter; set { _tagFilter = value; OnPropertyChanged(); OnPropertyChanged(nameof(TagChoice)); } }
    public string CollectionFilter
    {
        get => _collectionFilter;
        set
        {
            _collectionFilter = value;
            if (!string.IsNullOrEmpty(value))
            {
                _allCollectionsOnly = false;
                _noCollectionOnly = false;
                if (_collectionService is not null)
                {
                    try
                    {
                        var matchingNode = CollectionNodes.SelectMany(r => r.SelfAndDescendants())
                            .FirstOrDefault(n => string.Equals(n.Path, value.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(n.Name, value.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (matchingNode is not null)
                        {
                            _collectionIdFilter = matchingNode.Id;
                        }
                        else
                        {
                            var allCols = _collectionService.GetAllAsync().GetAwaiter().GetResult();
                            var match = allCols.FirstOrDefault(c => string.Equals(c.Name, value.Trim(), StringComparison.OrdinalIgnoreCase));
                            _collectionIdFilter = match?.Id;
                        }
                    }
                    catch { }
                }
            }
            else
            {
                _collectionIdFilter = null;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(CollectionChoice));
        }
    }
    public bool FavoritesOnly { get => _favoritesOnly; set { _favoritesOnly = value; OnPropertyChanged(); } }
    public int MinimumRating { get => _minimumRating; set { _minimumRating = Math.Clamp(value, 0, 5); OnPropertyChanged(); } }

    // Escolhas da barra de filtros: refiltram imediatamente em memória.
    public string CategoryChoice { get => Choice(CategoryFilter); set { if (value is null) return; CategoryFilter = FromChoice(value); ApplyView(); } }
    public string TagChoice { get => Choice(TagFilter); set { if (value is null) return; TagFilter = FromChoice(value); ApplyView(); } }
    public static IReadOnlyList<string> OrientationChoices { get; } = [AnyOrientation, "Paisagem", "Retrato", "Quadrada"];

    /// <summary>Filtro por orientação (paisagem, retrato, quadrada) calculada das dimensões.</summary>
    public string OrientationChoice
    {
        get => _orientationFilter;
        set { if (value is null || value == _orientationFilter) return; _orientationFilter = value; OnPropertyChanged(); OnAdvancedFilterChanged(); }
    }

    public string CollectionChoice
    {
        get => string.IsNullOrEmpty(CollectionFilter) ? AnyCollectionChoice : CollectionFilter;
        set
        {
            if (value is null) return;
            _allCollectionsOnly = false;
            _noCollectionOnly = false;
            if (value == AnyCollectionChoice)
            {
                _collectionIdFilter = null;
                CollectionFilter = string.Empty;
                if (SelectedCollectionNode is not null)
                {
                    SelectedCollectionNode.IsSelected = false;
                    SelectedCollectionNode = null;
                }
            }
            else
            {
                var matchingNode = CollectionNodes.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Path == value);
                if (matchingNode is not null)
                {
                    _collectionIdFilter = matchingNode.Id;
                    CollectionFilter = matchingNode.Path;
                    if (SelectedCollectionNode is not null && SelectedCollectionNode != matchingNode)
                    {
                        SelectedCollectionNode.IsSelected = false;
                    }
                    matchingNode.IsSelected = true;
                    SelectedCollectionNode = matchingNode;
                }
                else
                {
                    var entry = CollectionEntries.FirstOrDefault(e => e.Label == value || e.Key == value);
                    if (entry is not null && entry.Id.HasValue)
                    {
                        _collectionIdFilter = entry.Id.Value;
                        CollectionFilter = entry.Label;
                    }
                    else
                    {
                        _collectionIdFilter = null;
                        CollectionFilter = value;
                    }
                }
            }
            ApplyView();
        }
    }
    public int MinimumRatingChoice { get => MinimumRating; set { MinimumRating = value; OnAdvancedFilterChanged(); } }
    public bool FavoritesChoice { get => FavoritesOnly; set { FavoritesOnly = value; ApplyView(); } }

    public PhotoCardViewModel? SelectedPhoto
    {
        get => _selectedPhoto;
        set
        {
            if (ReferenceEquals(_selectedPhoto, value)) return;
            _selectedPhoto = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedPhoto));
            OnPropertyChanged(nameof(PositionText));
            NotifySelectedVideo();
            EnterReviewCommand?.RaiseCanExecuteChanged();
            PreviousCommand.RaiseCanExecuteChanged();
            NextCommand.RaiseCanExecuteChanged();
            UpdateSelectedPhotoCollections();
            _ = LoadMarkersAsync(value);
            _ = LoadPreviewAsync(value);
        }
    }

    public bool HasSelectedPhoto => _selectedPhoto is not null;

    /// <summary>A seleção única é um vídeo: o painel de preview mostra o player compacto no lugar da imagem.</summary>
    public bool IsSelectedVideo => _selectedPhoto is { IsVideo: true };
    /// <summary>Caminho a reproduzir no painel; nulo na revisão (que tem o próprio player), com seleção múltipla ou arquivo ausente (o player libera o arquivo).</summary>
    private bool _videoSuspended;
    /// <summary>Graus a girar o player: rotação do arquivo (que o MediaElement ignora) + giro manual.</summary>
    public double SelectedVideoRotation => _selectedPhoto?.Photo.VideoDisplayRotation ?? 0;
    public string? SelectedVideoPath => !_videoSuspended && !_isReviewMode && SelectionCount <= 1 && _selectedPhoto is { IsVideo: true, IsMissing: false } card ? card.Photo.CurrentPath : null;
    private void SuspendVideoPlayback() { _videoSuspended = true; NotifySelectedVideo(); }
    private void ResumeVideoPlayback() { _videoSuspended = false; NotifySelectedVideo(); }
    private void NotifySelectedVideo() { OnPropertyChanged(nameof(IsSelectedVideo)); OnPropertyChanged(nameof(SelectedVideoPath)); OnPropertyChanged(nameof(SelectedVideoRotation)); }

    /// <summary>Seleciona uma foto por ID para atalhos vindos de outras áreas, preservando a instância da Biblioteca.</summary>
    public bool SelectPhoto(long photoId)
    {
        if (!_cards.TryGetValue(photoId, out var card)) return false;
        SelectedPhoto = card;
        return true;
    }
    public ImageSource? PreviewImage { get => _previewImage; private set { _previewImage = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); ScheduleStatusClear(value); } }
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApplyOrganizationBatch)); ApplyOrganizationBatchCommand?.RaiseCanExecuteChanged(); } }
    // ---------- seleção múltipla (a lista é mantida pela View a partir do ListBox; a VM guarda e deriva o que o painel precisa)
    private IReadOnlyList<PhotoCardViewModel> _selectedCards = [];
    /// <summary>Fotos marcadas na grade (checkbox, Ctrl, Shift ou "Selecionar tudo").</summary>
    public IReadOnlyList<PhotoCardViewModel> SelectedCards => _selectedCards;
    public RangeObservableCollection<PhotoCardViewModel> SelectionThumbnails { get; } = [];
    public int SelectionCount => _selectedCards.Count;
    public bool HasSelection => SelectionCount > 0;

    private bool _isMultiSelectMode;
    /// <summary>Modo "Multi-seleção": as caixas de marcação aparecem nas fotos e clicar na foto marca/desmarca (sem precisar acertar a caixa).</summary>
    public bool IsMultiSelectMode
    {
        get => _isMultiSelectMode;
        set { if (_isMultiSelectMode == value) return; _isMultiSelectMode = value; OnPropertyChanged(); }
    }

    public bool IsSingleSelection => SelectionCount == 1;
    public bool IsMultiSelection => SelectionCount > 1;
    public string SelectionTitle => SelectionCount switch { 0 => "Nenhuma foto selecionada", 1 => "1 foto selecionada", _ => $"{SelectionCount} fotos selecionadas" };
    public string SelectionBadgeText => SelectionCount switch { 0 => "Nenhuma selecionada", 1 => "1 selecionada", _ => $"{SelectionCount} selecionadas" };
    /// <summary>Texto curto que diz onde a ação do painel vai agir.</summary>
    public string TargetText => SelectionCount switch { 0 => "Selecione fotos na grade (use as caixas de seleção).", 1 => "Será aplicado à foto selecionada.", _ => $"Será aplicado às {SelectionCount} fotos selecionadas." };
    public bool HasEditableSelection => EditableSelectionCount > 0;
    public int EditableSelectionCount => _selectedCards.Count(card => !card.IsMissing && MetadataFormats.CanEdit(card.Photo.Extension));
    public string SelectionDetails
    {
        get
        {
            if (SelectionCount == 0) return string.Empty;
            var types = string.Join(", ", _selectedCards.GroupBy(c => c.StatusText).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"));
            var size = new PhotoCardViewModel(new Photo { FileSize = _selectedCards.Sum(c => c.Photo.FileSize) }).SizeText;
            return $"{types} · {size} no total";
        }
    }

    /// <summary>Pedido à View para remarcar fotos na grade depois que a lista foi reconstruída (filtro, recarga, aplicação em lote).</summary>
    public event Action<IReadOnlyList<PhotoCardViewModel>>? SelectionRestoreRequested;

    public void UpdateSelection(IReadOnlyList<PhotoCardViewModel> cards)
    {
        _selectedCards = cards;
        NotifySelectedVideo();
        UpdateCommonCollections();
        SelectionThumbnails.ReplaceAll(cards.Take(6));
        foreach (var name in new[] { nameof(SelectedCards), nameof(SelectionCount), nameof(HasSelection), nameof(IsSingleSelection), nameof(IsMultiSelection), nameof(SelectionTitle),
                     nameof(SelectionBadgeText), nameof(TargetText), nameof(EditableSelectionCount), nameof(HasEditableSelection), nameof(SelectionDetails), nameof(SummaryText), nameof(CanApplyOrganizationBatch), nameof(OrganizationImpactText) })
            OnPropertyChanged(name);
        ApplyOrganizationBatchCommand.RaiseCanExecuteChanged();
    }

    // ---------- modo de revisão (foto grande + filmstrip + painel); a Biblioteca continua sendo a dona da lista e da seleção
    private bool _isReviewMode, _isFullScreen;
    /// <summary>Dados e comandos do modo de revisão; definido pelo shell (pode ser nulo em hosts/testes mínimos).</summary>
    public ReviewViewModel? Review { get; set; }
    public bool IsReviewMode { get => _isReviewMode; private set { if (_isReviewMode == value) return; _isReviewMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBrowseMode)); NotifySelectedVideo(); } }
    public bool IsBrowseMode => !_isReviewMode;
    /// <summary>Tela cheia: o shell esconde a barra de navegação e a janela vai para modo sem bordas.</summary>
    public bool IsFullScreen { get => _isFullScreen; private set { if (_isFullScreen == value) return; _isFullScreen = value; OnPropertyChanged(); } }
    public RelayCommand EnterReviewCommand { get; private set; } = null!;
    public RelayCommand ExitReviewCommand { get; private set; } = null!;
    public RelayCommand ToggleFullScreenCommand { get; private set; } = null!;

    /// <summary>Abre a revisão na foto indicada (ou na selecionada).</summary>
    public void EnterReview(PhotoCardViewModel? card = null)
    {
        if (card is not null && !ReferenceEquals(card, SelectedPhoto)) SelectedPhoto = card;
        if (SelectedPhoto is null) return;
        IsReviewMode = true;
        Review?.SetActive(true);
    }

    public void ExitReview()
    {
        IsFullScreen = false;
        IsReviewMode = false;
        Review?.SetActive(false);
    }

    /// <summary>Grava a organização (avaliação, favorito, nota…) da foto no catálogo.</summary>
    public async Task SavePhotoAsync(PhotoCardViewModel card)
    {
        try
        {
            await _organization.SaveAsync(card.Photo);
            UpdateSidebarCounts();
        }
        catch (Exception ex) { StatusText = $"Erro ao salvar: {ex.Message}"; }
    }

    // ---------- organização em lote (aba Organização com 2+ fotos): campos vazios = manter
    private string _batchCategory = string.Empty, _batchTags = string.Empty, _batchNote = string.Empty;
    private int _batchRatingIndex, _batchFavoriteIndex;
    public RelayCommand ApplyOrganizationBatchCommand { get; private set; } = null!;
    public string BatchCategory { get => _batchCategory; set { _batchCategory = value; OnBatchChanged(); } }
    public string BatchTagsText { get => _batchTags; set { _batchTags = value; OnBatchChanged(); } }
    public string BatchNote { get => _batchNote; set { _batchNote = value; OnBatchChanged(); } }
    /// <summary>0 = manter; 1..6 = definir 0..5 estrelas.</summary>
    public int BatchRatingIndex { get => _batchRatingIndex; set { _batchRatingIndex = value; OnBatchChanged(); } }
    /// <summary>0 = manter; 1 = marcar; 2 = desmarcar.</summary>
    public int BatchFavoriteIndex { get => _batchFavoriteIndex; set { _batchFavoriteIndex = value; OnBatchChanged(); } }

    public OrganizationBatch OrganizationBatchPlan() => new()
    {
        Category = BatchCategory,
        TagsToAdd = OrganizationBatch.ParseList(BatchTagsText),
        CollectionsToAdd = BatchSelectedCollections.Select(c => c.Id).ToList(),
        Rating = BatchRatingIndex > 0 ? BatchRatingIndex - 1 : null,
        Favorite = BatchFavoriteIndex switch { 1 => true, 2 => false, _ => null },
        Note = BatchNote
    };

    public bool CanApplyOrganizationBatch => HasSelection && !IsBusy && !OrganizationBatchPlan().IsNoOp;

    public string OrganizationImpactText
    {
        get
        {
            var plan = OrganizationBatchPlan();
            if (plan.IsNoOp) return "Nada será alterado: preencha ao menos um campo (os vazios são mantidos).";
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(plan.Category)) parts.Add($"categoria “{plan.Category.Trim()}”");
            if (plan.TagsToAdd.Count > 0) parts.Add($"+{plan.TagsToAdd.Count} tag(s)");
            if (plan.CollectionsToAdd.Count > 0) parts.Add($"+{plan.CollectionsToAdd.Count} coleção(ões)");
            if (plan.Rating is { } r) parts.Add($"{r} estrela(s)");
            if (plan.Favorite is { } f) parts.Add(f ? "marcar favorita" : "desmarcar favorita");
            if (!string.IsNullOrWhiteSpace(plan.Note)) parts.Add("nota");
            return $"Serão alteradas {SelectionCount} foto(s): {string.Join(", ", parts)}.";
        }
    }

    private void OnBatchChanged()
    {
        foreach (var name in new[] { nameof(BatchCategory), nameof(BatchTagsText), nameof(BatchNote), nameof(BatchRatingIndex), nameof(BatchFavoriteIndex), nameof(CanApplyOrganizationBatch), nameof(OrganizationImpactText) })
            OnPropertyChanged(name);
        ApplyOrganizationBatchCommand.RaiseCanExecuteChanged();
    }

    public Task ApplyOrganizationBatchAsync() => ApplyOrganizationBatchAsync(SelectedCards, OrganizationBatchPlan());

    public async Task ApplyOrganizationBatchAsync(IReadOnlyList<PhotoCardViewModel> cards, OrganizationBatch plan)
    {
        if (cards.Count == 0 || plan.IsNoOp) return;
        IsBusy = true;
        var changed = 0;
        string message;
        try
        {
            foreach (var card in cards)
            {
                if (plan.ApplyTo(card.Photo))
                {
                    await _organization.SaveAsync(card.Photo);
                    changed++;
                }
            }
            if (plan.CollectionsToAdd.Count > 0 && _collectionService is not null)
            {
                var cardIds = cards.Select(c => c.Photo.Id).ToList();
                foreach (var colId in plan.CollectionsToAdd)
                {
                    await _collectionService.AddPhotosAsync(colId, cardIds);
                    foreach (var card in cards)
                    {
                        if (!card.Photo.CollectionIds.Contains(colId)) card.Photo.CollectionIds.Add(colId);
                    }
                }
                await _collectionService.SyncPhotoCollectionsAsync(cards.Select(c => c.Photo));
            }
            BatchSelectedCollections.Clear();
            UpdateBatchAvailableCollections();
            RebuildSidebar();
            ApplyView();
            message = changed == cards.Count ? $"Organização aplicada a {changed} foto(s)." : $"Organização aplicada a {changed} de {cards.Count} foto(s) ({cards.Count - changed} já estavam assim).";
            _batchCategory = _batchTags = _batchNote = string.Empty; _batchRatingIndex = _batchFavoriteIndex = 0;
            OnBatchChanged();
        }
        catch (Exception ex) { message = $"Erro na organização em lote: {ex.Message}"; }
        finally { IsBusy = false; }
        StatusText = message;
    }

    /// <summary>Renomeia a seleção: 1 foto sem variáveis usa o texto como nome; caso contrário, o texto é um template ({name} {date} {sequence}…).</summary>
    public Task RenameSelectionAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return Task.CompletedTask;
        return SelectedCards.Count == 1 && !text.Contains('{') ? RenameSelectedAsync(SelectedCards, text) : RenameBatchAsync(SelectedCards, text);
    }
    public string SummaryText => $"{SelectionCount} selecionada(s)  |  {Photos.Count} de {_all.Count} fotos";
    public string PositionText
    {
        get
        {
            var index = _selectedPhoto is null ? -1 : Photos.IndexOf(_selectedPhoto);
            return index < 0 ? string.Empty : $"{index + 1:N0} de {Photos.Count:N0}";
        }
    }
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrEmpty(CategoryFilter) || !string.IsNullOrEmpty(TagFilter) || !string.IsNullOrEmpty(CollectionFilter)
        || !string.IsNullOrEmpty(_folderFilter) || FavoritesOnly || MinimumRating > 0 || _smartList != "all" || _noCollectionOnly || _allCollectionsOnly || _collectionIdFilter.HasValue || _subtreeRootId.HasValue || _orientationFilter != AnyOrientation || AdvancedFilterCount > 0 || HasProFilters;

    public Task ImportFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return Task.CompletedTask;
        return ImportFolderCoreAsync(folderPath);
    }

    /// <summary>Recarrega o catálogo e aplica os filtros atuais.</summary>
    public async Task ApplyFiltersAsync()
    {
        await ReloadAsync();
    }

    /// <summary>Refiltra o conjunto já carregado, sem tocar no banco.</summary>
    public void ApplyFilters() => ApplyView();

    public async Task SaveSelectedAsync()
    {
        if (SelectedPhoto is null) return;
        IsBusy = true;
        try
        {
            await _organization.SaveAsync(SelectedPhoto.Photo);
            RebuildSidebar();
            ApplyView();
            StatusText = "Organização da foto salva.";
        }
        catch (Exception ex) { StatusText = $"Erro ao salvar organização: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    public async Task ToggleFavoriteAsync(PhotoCardViewModel card)
    {
        card.IsFavorite = !card.IsFavorite;
        try
        {
            await _organization.SaveAsync(card.Photo);
            UpdateSidebarCounts();
        }
        catch (Exception ex)
        {
            card.IsFavorite = !card.IsFavorite;
            StatusText = $"Erro ao atualizar favorito: {ex.Message}";
        }
    }

    public async Task ApplyBatchAsync(IEnumerable<PhotoCardViewModel> selected, string? category, string? tag, string? collection, int rating, bool favorite)
    {
        var photos = selected.ToList();
        if (photos.Count == 0) return;
        IsBusy = true;
        try
        {
            foreach (var card in photos)
            {
                if (!string.IsNullOrWhiteSpace(category)) card.Photo.CategoryName = category.Trim();
                if (!string.IsNullOrWhiteSpace(tag) && !card.Photo.Tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase)) card.Photo.Tags.Add(tag.Trim());
                if (rating > 0) card.Photo.Rating = Math.Clamp(rating, 0, 5);
                if (favorite) card.Photo.IsFavorite = true;
                await _organization.SaveAsync(card.Photo);
            }
            if (!string.IsNullOrWhiteSpace(collection) && _collectionService is not null)
            {
                var allCols = await _collectionService.GetAllAsync();
                var col = allCols.FirstOrDefault(c => string.Equals(c.Name, collection.Trim(), StringComparison.OrdinalIgnoreCase));
                if (col is not null)
                {
                    var ids = photos.Select(c => c.Photo.Id).ToList();
                    await _collectionService.AddPhotosAsync(col.Id, ids);
                    foreach (var card in photos)
                    {
                        if (!card.Photo.CollectionIds.Contains(col.Id)) card.Photo.CollectionIds.Add(col.Id);
                    }
                    await _collectionService.SyncPhotoCollectionsAsync(photos.Select(c => c.Photo));
                }
            }
            RebuildSidebar();
            ApplyView();
            StatusText = $"Organização aplicada a {photos.Count} foto(s).";
        }
        catch (Exception ex) { StatusText = $"Erro na operação em lote: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    public void AddBatchCollection(CollectionItemViewModel item)
    {
        if (!BatchSelectedCollections.Any(c => c.Id == item.Id))
        {
            BatchSelectedCollections.Add(item);
            OnBatchChanged();
            UpdateBatchAvailableCollections();
        }
    }

    public void RemoveBatchCollection(CollectionItemViewModel item)
    {
        var existing = BatchSelectedCollections.FirstOrDefault(c => c.Id == item.Id);
        if (existing is not null)
        {
            BatchSelectedCollections.Remove(existing);
            OnBatchChanged();
            UpdateBatchAvailableCollections();
        }
    }

    public async Task AddPhotoToCollectionAsync(PhotoCardViewModel card, long collectionId)
    {
        if (_collectionService is null) return;
        try
        {
            await _collectionService.AddPhotosAsync(collectionId, [card.Photo.Id]);
            if (!card.Photo.CollectionIds.Contains(collectionId))
            {
                card.Photo.CollectionIds.Add(collectionId);
            }
            await _collectionService.SyncPhotoCollectionsAsync([card.Photo]);
            card.NotifyCollectionsChanged();
            UpdateSelectedPhotoCollections();
            RebuildSidebar();
            await RebuildCollectionNodesAsync();
            ApplyView();
        }
        catch (Exception ex)
        {
            StatusText = $"Erro ao adicionar à coleção: {ex.Message}";
        }
    }

    // ---------- coleções em comum da seleção (painel de lote): só aparecem quando TODAS as fotos têm exatamente as mesmas coleções
    public ObservableCollection<CollectionItemViewModel> CommonCollections { get; } = [];
    public bool HasCommonCollections => CommonCollections.Count > 0;
    public RelayCommand RemoveCommonCollectionCommand { get; private set; } = null!;
    public RelayCommand RemoveAllCommonCollectionsCommand { get; private set; } = null!;

    private void UpdateCommonCollections()
    {
        CommonCollections.Clear();
        var ids = CollectionSelectionHelper.CommonWhenIdentical(_selectedCards.Select(c => (IReadOnlyCollection<long>)c.Photo.CollectionIds));
        if (ids.Count > 0)
        {
            var nodes = CollectionNodes.SelectMany(r => r.SelfAndDescendants()).ToDictionary(n => n.Id);
            foreach (var id in ids)
                if (nodes.TryGetValue(id, out var node)) CommonCollections.Add(new CollectionItemViewModel(node.Id, node.Name, node.Path, node.ParentId));
        }
        OnPropertyChanged(nameof(HasCommonCollections));
    }

    /// <summary>Tira as coleções indicadas de todas as fotos selecionadas (só a associação; nada é apagado das fotos nem do disco).</summary>
    public async Task RemoveCommonCollectionsAsync(IReadOnlyList<long> collectionIds)
    {
        if (_collectionService is null || collectionIds.Count == 0 || _selectedCards.Count == 0) return;
        try
        {
            var cards = _selectedCards.ToList();
            var photoIds = cards.Select(c => c.Photo.Id).ToList();
            foreach (var collectionId in collectionIds)
            {
                await _collectionService.RemovePhotosAsync(collectionId, photoIds);
                foreach (var card in cards) card.Photo.CollectionIds.Remove(collectionId);
            }
            await _collectionService.SyncPhotoCollectionsAsync(cards.Select(c => c.Photo));
            foreach (var card in cards) card.NotifyCollectionsChanged();
            UpdateSelectedPhotoCollections();
            RebuildSidebar();
            await RebuildCollectionNodesAsync();
            ApplyView();
            StatusText = $"{collectionIds.Count} coleção(ões) removida(s) de {cards.Count} foto(s).";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = $"Erro ao remover das coleções: {ex.Message}";
        }
    }

    public async Task RemovePhotoFromCollectionAsync(PhotoCardViewModel card, long collectionId)
    {
        if (_collectionService is null) return;
        try
        {
            await _collectionService.RemovePhotosAsync(collectionId, [card.Photo.Id]);
            card.Photo.CollectionIds.Remove(collectionId);
            await _collectionService.SyncPhotoCollectionsAsync([card.Photo]);
            card.NotifyCollectionsChanged();
            UpdateSelectedPhotoCollections();
            RebuildSidebar();
            await RebuildCollectionNodesAsync();
            ApplyView();
        }
        catch (Exception ex)
        {
            StatusText = $"Erro ao remover da coleção: {ex.Message}";
        }
    }

    public async Task CreateRootCollectionAsync()
    {
        if (_collectionService is null) return;
        var existingSiblings = CollectionNodes.Select(n => n.Name).ToList();
        var vm = new CollectionNameViewModel("Nova coleção", "", existingSiblings, null, "Criar");
        var dialogResult = ShowNameDialog(vm);
        if (dialogResult != true) return;

        try
        {
            var created = await _collectionService.CreateAsync(vm.Name.Trim(), null);
            await RebuildCollectionNodesAsync();
            RefillCollectionChoices();
            UpdateSidebarCounts();
            StatusText = $"Coleção «{created.Name}» criada.";
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, "Erro ao criar coleção");
        }
    }

    public async Task CreateSubcollectionAsync(CollectionNode? parentNode)
    {
        if (_collectionService is null) return;
        parentNode ??= SelectedCollectionNode;
        if (parentNode is null || parentNode.IsAggregate) return;

        var existingSiblings = parentNode.Children.Select(c => c.Name).ToList();
        var vm = new CollectionNameViewModel($"Nova subcoleção em «{parentNode.Name}»", "", existingSiblings, null, "Criar");
        var dialogResult = ShowNameDialog(vm);
        if (dialogResult != true) return;

        try
        {
            var created = await _collectionService.CreateAsync(vm.Name.Trim(), parentNode.Id);
            _collectionExpansion[parentNode.Id] = true;
            parentNode.IsExpanded = true;
            await RebuildCollectionNodesAsync();
            RefillCollectionChoices();
            UpdateSidebarCounts();
            StatusText = $"Subcoleção «{created.Name}» criada.";
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, "Erro ao criar subcoleção");
        }
    }

    public async Task RenameCollectionAsync(CollectionNode? node)
    {
        if (_collectionService is null) return;
        node ??= SelectedCollectionNode;
        if (node is null || node.IsAggregate) return;

        List<string> existingSiblings;
        if (node.ParentId is null)
        {
            existingSiblings = CollectionNodes.Where(n => n.Id != node.Id).Select(n => n.Name).ToList();
        }
        else
        {
            var parent = CollectionNodes.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Id == node.ParentId.Value);
            existingSiblings = parent?.Children.Where(c => c.Id != node.Id).Select(c => c.Name).ToList() ?? [];
        }

        var vm = new CollectionNameViewModel($"Renomear «{node.Name}»", node.Name, existingSiblings, node.Name, "Salvar");
        var dialogResult = ShowNameDialog(vm);
        if (dialogResult != true) return;

        try
        {
            var updated = await _collectionService.RenameAsync(node.Id, vm.Name.Trim());
            await _collectionService.SyncPhotoCollectionsAsync(_all);
            foreach (var card in _cards.Values) card.NotifyCollectionsChanged();
            await RebuildCollectionNodesAsync();
            RefillCollectionChoices();
            UpdateSidebarCounts();
            StatusText = $"Coleção renomeada para «{updated.Name}».";
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, "Erro ao renomear coleção");
        }
    }

    public async Task MoveCollectionAsync(CollectionNode? node)
    {
        if (_collectionService is null) return;
        node ??= SelectedCollectionNode;
        if (node is null || node.IsAggregate) return;

        var vm = new MoveCollectionViewModel(node, CollectionNodes.ToList());
        var dialogResult = ShowMoveDialog(vm);
        if (dialogResult != true || !vm.CanMove) return;

        try
        {
            var result = await _collectionService.MoveAsync(node.Id, vm.TargetParentId);
            if (!result.Success)
            {
                ShowMessage(result.Message ?? "Não foi possível mover a coleção.", "Aviso");
                return;
            }

            if (vm.TargetParentId.HasValue)
            {
                _collectionExpansion[vm.TargetParentId.Value] = true;
            }
            await _collectionService.SyncPhotoCollectionsAsync(_all);
            foreach (var card in _cards.Values) card.NotifyCollectionsChanged();
            await RebuildCollectionNodesAsync();
            RefillCollectionChoices();
            UpdateSidebarCounts();
            StatusText = $"Coleção «{node.Name}» movida com sucesso.";
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, "Erro ao mover coleção");
        }
    }

    public async Task DeleteCollectionAsync(CollectionNode? node)
    {
        if (_collectionService is null) return;
        node ??= SelectedCollectionNode;
        if (node is null || node.IsAggregate) return;

        var descendants = node.SelfAndDescendants().Where(n => n.Id != node.Id).ToList();
        var vm = new DeleteCollectionViewModel(
            node.Id,
            node.Name,
            node.DirectCount,
            descendants.Count,
            node.SubtreeCount);

        var dialogResult = ShowDeleteDialog(vm);
        if (dialogResult != true) return;

        try
        {
            var result = await _collectionService.DeleteAsync(node.Id, vm.SelectedMode);

            var deletedIds = new HashSet<long>();
            if (vm.SelectedMode == DeleteMode.WithDescendants)
            {
                foreach (var d in node.SelfAndDescendants()) deletedIds.Add(d.Id);
            }
            else
            {
                deletedIds.Add(node.Id);
            }

            foreach (var photo in _all)
            {
                photo.CollectionIds.RemoveAll(id => deletedIds.Contains(id));
            }

            await _collectionService.SyncPhotoCollectionsAsync(_all);
            foreach (var card in _cards.Values) card.NotifyCollectionsChanged();

            if (_collectionIdFilter.HasValue && deletedIds.Contains(_collectionIdFilter.Value))
            {
                _collectionIdFilter = null;
                CollectionFilter = string.Empty;
                SelectedCollectionNode = null;
                ApplyView();
            }

            await RebuildCollectionNodesAsync();
            RefillCollectionChoices();
            UpdateSidebarCounts();

            if (result.RenamedChildren.Count > 0)
            {
                var renamedList = string.Join(", ", result.RenamedChildren.Select(r => $"«{r.OldName}» renomeada para «{r.NewName}»"));
                ShowMessage($"A coleção foi excluída. Algumas subcoleções promovidas foram renomeadas para evitar conflitos:\n{renamedList}", "Aviso de Renomeação");
            }

            StatusText = $"Coleção «{node.Name}» excluída ({result.DeletedCount} coleção(ões) removida(s)).";
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, "Erro ao excluir coleção");
        }
    }


    // ---------- menus de contexto: Explorer, excluir e atualizar pasta ----------
    public RelayCommand ShowInExplorerCommand { get; private set; } = null!;
    public RelayCommand RecycleFromMenuCommand { get; private set; } = null!;
    public RelayCommand RotateLeftCommand { get; private set; } = null!;
    public RelayCommand RotateRightCommand { get; private set; } = null!;
    public RelayCommand RefreshFolderCommand { get; private set; } = null!;
    public RelayCommand RefreshFoldersCommand { get; private set; } = null!;
    public RelayCommand ShowFolderInExplorerCommand { get; private set; } = null!;

    /// <summary>Abre uma pasta no Explorer (ou seleciona um arquivo nela). Substituível nos testes.</summary>
    public Action<string, bool> OpenInExplorer { get; set; } = (path, selectFile) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", selectFile ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });

    /// <summary>Pergunta antes de enviar arquivos para a Lixeira. Substituível nos testes.</summary>
    public Func<int, bool> ConfirmRecycle { get; set; } = count =>
        System.Windows.MessageBox.Show($"{count} arquivo(s) serão enviados para a Lixeira do Windows. Continuar?", "Confirmar exclusão", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

    /// <summary>Botão direito numa miniatura: se ela faz parte da seleção, vale para toda a seleção; senão, só para ela.</summary>
    public IReadOnlyList<PhotoCardViewModel> ContextTargets(PhotoCardViewModel card) =>
        _selectedCards.Contains(card) ? _selectedCards.ToList() : [card];

    private void InitFileMenuCommands()
    {
        ShowInExplorerCommand = new RelayCommand(parameter => { if (parameter is PhotoCardViewModel card) ShowInExplorer(card); });
        RotateLeftCommand = new RelayCommand(parameter => { if (parameter is PhotoCardViewModel card) _ = RotateAsync(card, -90); });
        RotateRightCommand = new RelayCommand(parameter => { if (parameter is PhotoCardViewModel card) _ = RotateAsync(card, 90); });
        RecycleFromMenuCommand = new RelayCommand(parameter => { if (parameter is PhotoCardViewModel card) _ = RecycleFromMenuAsync(card); });
        RefreshFolderCommand = new RelayCommand(parameter => { if (parameter is FolderNode node) _ = RefreshFolderAsync(node.Path); });
        RefreshFoldersCommand = new RelayCommand(_ => _ = RefreshFolderAsync(string.IsNullOrEmpty(_folderFilter) ? null : _folderFilter));
        ShowFolderInExplorerCommand = new RelayCommand(parameter => { if (parameter is FolderNode node) ShowFolderInExplorer(node.Path); });
        RemoveFromCatalogCommand = new RelayCommand(parameter => { if (parameter is PhotoCardViewModel card) _ = RemoveFromCatalogMenuAsync(card); });
    }

    public void ShowInExplorer(PhotoCardViewModel card)
    {
        var path = card.Photo.CurrentPath;
        try
        {
            if (File.Exists(path)) OpenInExplorer(path, true);
            else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder)) { OpenInExplorer(folder, false); StatusText = "O arquivo não existe mais; a pasta foi aberta."; }
            else StatusText = "O arquivo e a pasta não estão disponíveis.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { StatusText = $"Não foi possível abrir o Explorer: {ex.Message}"; }
    }

    public void ShowFolderInExplorer(string folder)
    {
        try
        {
            if (DirectoryExists(folder)) OpenInExplorer(folder, false);
            else StatusText = "A pasta não existe mais. Use “Atualizar” para marcar os arquivos como ausentes.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { StatusText = $"Não foi possível abrir o Explorer: {ex.Message}"; }
    }


    // ---------- pastas inacessíveis e expandir/recolher tudo ----------
    /// <summary>Existência de pasta; substituível nos testes (e para tratar caminhos de rede lentos).</summary>
    public Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;
    public RelayCommand ExpandAllCommand { get; private set; } = null!;
    public RelayCommand CollapseAllCommand { get; private set; } = null!;
    private readonly Dictionary<string, bool> _folderAccess = new(StringComparer.OrdinalIgnoreCase);
    private int _accessVersion;
    private static readonly TimeSpan FolderCheckTimeout = TimeSpan.FromSeconds(4);

    private void InitTreeCommands()
    {
        ExpandAllCommand = new RelayCommand(parameter => SetExpandedRecursive(parameter, true));
        CollapseAllCommand = new RelayCommand(parameter => SetExpandedRecursive(parameter, false));
    }

    /// <summary>Expande ou recolhe o nó e TODOS os seus descendentes (pastas ou coleções).</summary>
    public void SetExpandedRecursive(object? node, bool expanded)
    {
        switch (node)
        {
            case FolderNode folder: foreach (var item in folder.SelfAndDescendants()) item.IsExpanded = expanded; break;
            case CollectionNode { IsAggregate: false } collection: foreach (var item in collection.SelfAndDescendants()) item.IsExpanded = expanded; break;
        }
    }

    /// <summary>Marca como inacessíveis as pastas que não existem mais / estão desconectadas e some o conteúdo delas da árvore. Roda em segundo plano (disco de rede lento não trava a tela).</summary>
    private async Task CheckFolderAccessAsync()
    {
        var version = ++_accessVersion;
        foreach (var node in Folders.SelectMany(r => r.SelfAndDescendants()))
            if (_folderAccess.TryGetValue(node.Path, out var known)) node.IsAccessible = known;   // sem piscar: mostra o último resultado já conhecido

        using var gate = new SemaphoreSlim(6);
        async Task Check(FolderNode node)
        {
            bool exists;
            await gate.WaitAsync();
            try { exists = await ExistsWithTimeoutAsync(node.Path); }
            finally { gate.Release(); }
            if (version != _accessVersion) return;
            _folderAccess[node.Path] = exists;
            node.IsAccessible = exists;
            if (!exists) return;                                    // dentro de uma pasta inacessível não há o que verificar
            await Task.WhenAll(node.Children.ToList().Select(Check));
        }
        try { await Task.WhenAll(Folders.ToList().Select(Check)); }
        catch (Exception) { /* verificação é só um aviso visual */ }
    }

    private async Task<bool> ExistsWithTimeoutAsync(string path)
    {
        var probe = Task.Run(() => { try { return DirectoryExists(path); } catch (Exception) { return false; } });
        var finished = await Task.WhenAny(probe, Task.Delay(FolderCheckTimeout));
        return finished == probe && await probe;
    }

    // ---------- remover do catálogo (somente itens cujo arquivo não existe mais) ----------
    public RelayCommand RemoveFromCatalogCommand { get; private set; } = null!;

    /// <summary>Mostra o relatório "não encontrados" depois de Atualizar. Substituível nos testes.</summary>
    public Action<MissingItemsViewModel> ShowMissingDialog { get; set; } = viewModel => ShowOwned(new MissingItemsDialog(viewModel));

    public Func<int, bool> ConfirmRemoveAll { get; set; } = count =>
        System.Windows.MessageBox.Show($"Remover {count} arquivo(s) do catálogo?\n\nTags, coleções, notas e histórico de metadados deles serão apagados. Nenhum arquivo é excluído (eles já não existem).",
            "Remover todos", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

    public Func<int, bool> ConfirmRemoveFromCatalog { get; set; } = count =>
        System.Windows.MessageBox.Show($"Remover {count} item(ns) do catálogo?\n\nTags, coleções, notas e histórico de metadados deles serão apagados. Nenhum arquivo é excluído (eles já não existem).",
            "Remover do catálogo", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

    /// <summary>Remove do catálogo (reconferindo no disco que o arquivo realmente sumiu), apaga as miniaturas em cache e recarrega.</summary>
    public async Task<int> RemoveFromCatalogByIdsAsync(IReadOnlyList<long> photoIds)
    {
        var wanted = photoIds.ToHashSet();
        var photos = _all.Where(p => wanted.Contains(p.Id)).ToList();
        var removed = await _catalog.RemoveMissingAsync(photos);
        foreach (var id in removed) _thumbnails.Remove(id);
        await ReloadAsync();
        StatusText = removed.Count == photos.Count
            ? $"{removed.Count} item(ns) removido(s) do catálogo."
            : $"{removed.Count} removido(s); {photos.Count - removed.Count} voltaram a existir e foram mantidos.";
        return removed.Count;
    }

    public async Task RemoveFromCatalogMenuAsync(PhotoCardViewModel card)
    {
        var targets = ContextTargets(card).Where(c => c.IsMissing && _all.Any(p => p.Id == c.Photo.Id)).ToList();
        if (targets.Count == 0) { StatusText = "Só itens com arquivo ausente podem ser removidos do catálogo."; return; }
        if (!ConfirmRemoveFromCatalog(targets.Count)) return;
        try { await RemoveFromCatalogByIdsAsync(targets.Select(c => c.Photo.Id).ToList()); }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText = $"Não foi possível remover do catálogo: {ex.Message}"; }
    }

    /// <summary>
    /// Gira a exibição dos itens (botão direito → Girar). Corrige arquivos sem metadado de orientação ou com metadado "errado" (alguns drones);
    /// é guardado no catálogo e nunca altera o arquivo. Vale para a seleção quando o item clicado faz parte dela.
    /// </summary>
    public async Task RotateAsync(PhotoCardViewModel card, int delta)
    {
        var targets = ContextTargets(card).ToList();
        try
        {
            foreach (var group in targets.GroupBy(c => ((c.Photo.UserRotation + delta) % 360 + 360) % 360))
                await _catalog.SetUserRotationAsync(group.Select(c => c.Photo).ToList(), group.Key);
            foreach (var target in targets) target.NotifyMediaInfoChanged();
            if (_orientationFilter != AnyOrientation) ApplyView();
            if (_selectedPhoto is not null && targets.Contains(_selectedPhoto))
            {
                _ = LoadPreviewAsync(_selectedPhoto);
                NotifySelectedVideo();
                Review?.RefreshAfterRotation();
            }
            StatusText = $"{targets.Count} item(ns) girado(s) {(delta < 0 ? "à esquerda" : "à direita")}. O arquivo não foi alterado.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText = $"Não foi possível girar: {ex.Message}"; }
    }

    // ---------- localização (GPS + nome do lugar) ----------
    private readonly PhotoManager.Application.Location.ILocationService? _locationService;
    public RelayCommand FindLocationCommand { get; }
    public RelayCommand OpenMapCommand { get; }
    public bool CanFindLocation => _locationService is not null;

    /// <summary>Pergunta se pode enviar as coordenadas ao serviço público de mapas. Substituível nos testes.</summary>
    public Func<bool> ConfirmGeocoding { get; set; } = () => System.Windows.MessageBox.Show(
        "Para descobrir o nome do lugar, as COORDENADAS GPS dos arquivos serão enviadas ao serviço público e gratuito OpenStreetMap/Nominatim (nominatim.openstreetmap.org). Nenhuma foto, nome de arquivo ou outro dado é enviado.\n\nAs consultas são espaçadas em 1 por segundo e o resultado fica guardado no catálogo.\n\nAutorizar?",
        "Obter nome do lugar", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    /// <summary>Lê o GPS gravado nos arquivos (sem rede) e, com autorização, descobre o nome do lugar. Vale para a seleção quando o item clicado faz parte dela.</summary>
    public async Task FindLocationAsync(PhotoCardViewModel card)
    {
        if (_locationService is null) { StatusText = "Localização indisponível."; return; }
        var targets = ContextTargets(card).Where(c => !c.IsMissing).ToList();
        if (targets.Count == 0) { StatusText = "Nenhum arquivo disponível para ler a localização."; return; }
        try
        {
            var photos = targets.Select(c => c.Photo).ToList();
            var read = await _locationService.ReadGpsAsync(photos);
            foreach (var target in targets) target.NotifyLocationChanged();
            if (read.WithGps == 0) { StatusText = $"Nenhum dos {targets.Count} arquivo(s) tem coordenadas GPS gravadas."; return; }
            var pending = photos.Count(p => p.HasGps && string.IsNullOrEmpty(p.PlaceName));
            if (pending == 0) { StatusText = $"{read.WithGps} arquivo(s) com GPS; o nome do lugar já estava descoberto."; return; }
            if (!_locationService.HasConsent)
            {
                if (!ConfirmGeocoding()) { StatusText = $"{read.WithGps} arquivo(s) com GPS lido(s). O nome do lugar não foi consultado."; return; }
                _locationService.SetConsent(true);
            }
            StatusText = $"Consultando o nome de {pending} lugar(es)…";
            var progress = new Progress<int>(done => StatusText = $"Consultando o nome do lugar… {done} de {photos.Count}");
            var result = await _locationService.ResolvePlacesAsync(photos, progress);
            foreach (var target in targets) target.NotifyLocationChanged();
            if (!string.IsNullOrWhiteSpace(SearchText)) ApplyView();
            StatusText = $"Localização: {result.WithGps} com GPS, {result.Named + result.FromCache} nome(s) obtido(s)" + (result.Failed > 0 ? $", {result.Failed} sem resposta do serviço." : ".");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText = $"Não foi possível obter a localização: {ex.Message}"; }
    }

    private void OpenMap(PhotoCardViewModel card)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(new PhotoManager.Application.Location.GeoPoint(card.Photo.Latitude!.Value, card.Photo.Longitude!.Value).MapUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { StatusText = $"Não foi possível abrir o mapa: {ex.Message}"; }
    }

    // ---------- arrastar e soltar com o Explorer ----------
    private bool _isExternalDragOver;
    private string _externalDragText = string.Empty;
    /// <summary>Mostra o aviso "Solte para adicionar…" sobre a grade enquanto arquivos do Explorer são arrastados.</summary>
    public bool IsExternalDragOver { get => _isExternalDragOver; set { if (_isExternalDragOver == value) return; _isExternalDragOver = value; OnPropertyChanged(); } }
    public string ExternalDragText { get => _externalDragText; set { if (_externalDragText == value) return; _externalDragText = value; OnPropertyChanged(); } }

    /// <summary>Soltou numa pasta sem Ctrl/Shift: copiar (Sim), mover (Não) ou cancelar. Substituível nos testes.</summary>
    public Func<ExternalDropPlan, ExternalDropAction?> AskCopyOrMove { get; set; } = plan =>
        System.Windows.MessageBox.Show($"{plan.Files.Count} arquivo(s) para a pasta “{plan.TargetLabel}”.\n\nSim = Copiar (os originais ficam onde estão)\nNão = Mover\nCancelar = não fazer nada",
            "Copiar ou mover?", System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question) switch
        {
            System.Windows.MessageBoxResult.Yes => ExternalDropAction.Copy,
            System.Windows.MessageBoxResult.No => ExternalDropAction.Move,
            _ => null
        };

    public ExternalDropPlan PlanExternalDrop(IReadOnlyList<string> paths, FolderNode? target, bool ctrl, bool shift) =>
        ExternalDropPlanner.Plan(paths, target?.Path, target?.Label ?? string.Empty, ctrl, shift, Directory.Exists);

    /// <summary>
    /// Executa o que foi solto: na grade cataloga no lugar (nada muda no disco); numa pasta copia ou move (levando o sidecar .xmp).
    /// Mover um arquivo que já está no catálogo usa o serviço normal (a linha acompanha o arquivo e o PhotoId não muda).
    /// </summary>
    public async Task ExecuteExternalDropAsync(ExternalDropPlan plan)
    {
        IsExternalDragOver = false;
        if (!plan.CanDrop) { if (!string.IsNullOrEmpty(plan.Message)) StatusText = plan.Message; return; }
        var action = plan.Action;
        if (action == ExternalDropAction.AskCopyOrMove)
        {
            if (AskCopyOrMove(plan) is not { } choice) return;
            action = choice;
        }

        IsBusy = true;
        SuspendVideoPlayback();
        string message;
        try
        {
            if (action == ExternalDropAction.AddToCatalog)
            {
                var progress = new Progress<CatalogProgress>(value => StatusText = $"Indexando {value.Processed} arquivos… {value.Imported} novos");
                var result = await _catalog.ImportPathsAsync(plan.Files.Concat(plan.Folders).ToList(), progress);
                await ReloadAsync();
                var already = result.Scanned - result.Imported - result.Failed;
                message = $"{result.Imported} novo(s) no catálogo" + (already > 0 ? $"; {already} já estava(m)" : string.Empty) + (result.Failed > 0 ? $"; {result.Failed} com erro" : string.Empty) + ".";
            }
            else
            {
                var move = action == ExternalDropAction.Move;
                var target = plan.TargetFolder!;
                var known = move ? _all.Where(p => plan.Files.Contains(p.CurrentPath, StringComparer.OrdinalIgnoreCase)).ToList() : [];
                var done = 0;
                var skipped = 0;
                var failed = 0;
                foreach (var photo in known)
                {
                    try { await _fileOperations.MoveAsync(photo, target); done++; }
                    catch (IOException) { skipped++; }                                       // já existe um arquivo com esse nome no destino
                }
                var rest = plan.Files.Where(f => known.All(p => !string.Equals(p.CurrentPath, f, StringComparison.OrdinalIgnoreCase))).ToList();
                var transfer = await _fileOperations.TransferExternalAsync(rest, target, move);
                if (transfer.Created.Count > 0) await _catalog.ImportPathsAsync(transfer.Created);
                done += transfer.Created.Count;
                skipped += transfer.SkippedExisting.Count;
                failed += transfer.Failed.Count;
                await ReloadAsync();
                message = $"{done} arquivo(s) {(move ? "movido(s)" : "copiado(s)")} para “{plan.TargetLabel}”" + (skipped > 0 ? $"; {skipped} já existiam (mantidos)" : string.Empty) + (failed > 0 ? $"; {failed} falharam" : string.Empty) + ".";
            }
        }
        catch (OperationCanceledException) { message = "Operação cancelada."; }
        catch (Exception ex) { await TryReloadAsync(); message = $"Não foi possível concluir: {ex.Message}"; }
        finally { IsBusy = false; ResumeVideoPlayback(); }
        StatusText = message;
    }

    // ---------- filtros avançados: tipo, extensão, data e cor (a barra principal fica só com os essenciais) ----------
    public const string AllTypes = "Todos";
    public const string AllExtensions = "Todas";
    public static IReadOnlyList<string> TypeChoices { get; } = [AllTypes, "Fotos", "Vídeos"];
    public static IReadOnlyList<string> ColorFilterChoices => PhotoColors.FilterChoices;
    public ObservableCollection<string> ExtensionChoices { get; } = [AllExtensions];

    private string _typeFilter = AllTypes, _extensionFilter = AllExtensions, _colorFilter = "Qualquer";
    private DateTime? _dateFrom, _dateTo;
    private bool _isAdvancedOpen;

    public bool IsAdvancedOpen { get => _isAdvancedOpen; set { if (_isAdvancedOpen == value) return; _isAdvancedOpen = value; OnPropertyChanged(); } }
    public string TypeChoice { get => _typeFilter; set { if (value is null || value == _typeFilter) return; _typeFilter = value; OnAdvancedFilterChanged(); } }
    public string ExtensionChoice { get => _extensionFilter; set { if (value is null || value == _extensionFilter) return; _extensionFilter = value; OnAdvancedFilterChanged(); } }
    public string ColorFilterChoice { get => _colorFilter; set { if (value is null || value == _colorFilter) return; _colorFilter = value; OnAdvancedFilterChanged(); } }
    public DateTime? DateFrom { get => _dateFrom; set { if (_dateFrom == value) return; _dateFrom = value; OnAdvancedFilterChanged(); } }
    public DateTime? DateTo { get => _dateTo; set { if (_dateTo == value) return; _dateTo = value; OnAdvancedFilterChanged(); } }

    /// <summary>Quantos filtros avançados estão ativos (aparece no botão, para não esquecer que há filtro escondido).</summary>
    public int AdvancedFilterCount =>
        (MinimumRating > 0 ? 1 : 0) + (_orientationFilter != AnyOrientation ? 1 : 0) + (_typeFilter != AllTypes ? 1 : 0)
        + (_extensionFilter != AllExtensions ? 1 : 0) + (_colorFilter != "Qualquer" ? 1 : 0) + (_dateFrom.HasValue || _dateTo.HasValue ? 1 : 0);
    public string AdvancedFiltersLabel => AdvancedFilterCount == 0 ? "Filtros avançados" : $"Filtros avançados ({AdvancedFilterCount})";

    private void OnAdvancedFilterChanged()
    {
        OnPropertyChanged(nameof(AdvancedFilterCount));
        OnPropertyChanged(nameof(AdvancedFiltersLabel));
        ApplyView();
    }

    private bool MatchesAdvanced(Photo photo)
    {
        if (_typeFilter == "Fotos" && photo.IsVideo) return false;
        if (_typeFilter == "Vídeos" && !photo.IsVideo) return false;
        if (_extensionFilter != AllExtensions && !string.Equals(photo.Extension, _extensionFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (PhotoColors.FromFilterChoice(_colorFilter) is { } wanted && photo.ColorLabel != wanted) return false;
        if (_dateFrom is not null || _dateTo is not null)
        {
            var date = photo.DisplayDate.Date;
            if (_dateFrom is { } from && date < from.Date) return false;
            if (_dateTo is { } to && date > to.Date) return false;
        }
        return true;
    }

    private void ResetAdvancedFilters()
    {
        _typeFilter = AllTypes; _extensionFilter = AllExtensions; _colorFilter = "Qualquer"; _dateFrom = _dateTo = null;
        foreach (var name in new[] { nameof(TypeChoice), nameof(ExtensionChoice), nameof(ColorFilterChoice), nameof(DateFrom), nameof(DateTo), nameof(AdvancedFilterCount), nameof(AdvancedFiltersLabel) })
            OnPropertyChanged(name);
    }

    /// <summary>Extensões presentes no catálogo (para o filtro), preservando a escolha atual se ela ainda existir.</summary>
    private void RebuildExtensionChoices()
    {
        var wanted = _all.Select(p => p.Extension.ToLowerInvariant()).Where(e => e.Length > 0).Distinct().OrderBy(e => e, StringComparer.Ordinal).ToList();
        if (ExtensionChoices.Skip(1).SequenceEqual(wanted)) return;
        ExtensionChoices.Clear();
        ExtensionChoices.Add(AllExtensions);
        foreach (var extension in wanted) ExtensionChoices.Add(extension);
        if (_extensionFilter != AllExtensions && !wanted.Contains(_extensionFilter.ToLowerInvariant())) _extensionFilter = AllExtensions;
        OnPropertyChanged(nameof(ExtensionChoice));
    }

    // ---------- etiquetas de cor ----------
    public IReadOnlyList<ColorChoice> ColorChoices { get; private set; } = [];
    /// <summary>Mesmas cores para botões fora do menu de contexto (barra da revisão): agem sobre a seleção, ou sobre a foto aberta.</summary>
    public IReadOnlyList<ColorChoice> SelectionColorChoices { get; private set; } = [];
    private PhotoCardViewModel? _contextCard;

    /// <summary>Guarda a miniatura sobre a qual o menu de contexto foi aberto (os itens do submenu "Cor" agem sobre ela ou sobre a seleção dela).</summary>
    public void SetContextCard(PhotoCardViewModel? card) => _contextCard = card;

    private void InitColorChoices()
    {
        ColorChoices = PhotoColors.All.Prepend(PhotoColor.None).Select(color => new ColorChoice(color, new RelayCommand(_ => _ = SetColorForContextAsync(color)))).ToList();
        SelectionColorChoices = PhotoColors.All.Prepend(PhotoColor.None)
            .Select(color => new ColorChoice(color, new RelayCommand(_ => _ = SetColorAsync(_selectedCards.Count > 0 ? _selectedCards : _selectedPhoto is null ? [] : [_selectedPhoto], color)))).ToList();
    }

    private Task SetColorForContextAsync(PhotoColor color)
    {
        var targets = _contextCard is not null ? ContextTargets(_contextCard) : _selectedCards;
        _contextCard = null;   // uso único: um menu que foi fechado não pode "vazar" para a próxima ação
        return SetColorAsync(targets, color);
    }

    /// <summary>Marca (ou limpa, com None) a cor dos itens. Só organização: o arquivo não é tocado.</summary>
    public async Task SetColorAsync(IReadOnlyList<PhotoCardViewModel> targets, PhotoColor color)
    {
        if (targets.Count == 0) return;
        try
        {
            await _catalog.SetColorLabelAsync(targets.Select(c => c.Photo).ToList(), color);
            foreach (var target in targets) target.NotifyColorChanged();
            if (_colorFilter != "Qualquer") ApplyView();
            StatusText = color == PhotoColor.None
                ? $"Cor removida de {targets.Count} item(ns)."
                : $"{targets.Count} item(ns) marcado(s) como {PhotoColors.Name(color).ToLowerInvariant()}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText = $"Não foi possível marcar a cor: {ex.Message}"; }
    }

    /// <summary>
    /// A Transferência mexeu no catálogo. Só cor → atualiza os cartões no lugar (sem reler o banco);
    /// caminhos renomeados, itens novos ou enviados para a Lixeira → relê o catálogo.
    /// </summary>
    public async Task ApplyTransferChangeAsync(PhotoManager.Application.Transfer.TransferCatalogChange change)
    {
        if (change.Color is { } color)
        {
            var paths = new HashSet<string>(change.Paths, StringComparer.OrdinalIgnoreCase);
            foreach (var photo in _all.Where(p => paths.Contains(p.CurrentPath)))
            {
                photo.ColorLabel = color;
                if (_cards.TryGetValue(photo.Id, out var card)) card.NotifyColorChanged();
            }
            if (_colorFilter != "Qualquer") ApplyView();
            return;
        }
        await TryReloadAsync();
    }

    /// <summary>Atalho de teclado (1-6 / 0) sobre a seleção da grade.</summary>
    public Task SetColorForSelectionAsync(PhotoColor color) => SetColorAsync(_selectedCards, color);

    public async Task RecycleFromMenuAsync(PhotoCardViewModel card)
    {
        var targets = ContextTargets(card).Where(c => !c.IsMissing).ToList();
        if (targets.Count == 0) { StatusText = "O arquivo não está disponível para excluir."; return; }
        if (!ConfirmRecycle(targets.Count)) return;
        await RecycleSelectedAsync(targets);
    }

    /// <summary>
    /// Reconcilia o catálogo com a pasta (inclui subpastas; sem pasta = todas as pastas importadas): arquivos novos são importados e os que
    /// sumiram ficam marcados como ausentes (continuam no catálogo com tags/coleções/histórico).
    /// </summary>
    public async Task RefreshFolderAsync(string? folderPath)
    {
        if (IsBusy) return;
        IsBusy = true;
        string message;
        IReadOnlyList<MissingEntry> report = [];
        try
        {
            var roots = folderPath is null ? Folders.Select(f => f.Path).ToList() : [folderPath];
            var imported = 0;
            var gone = 0;
            foreach (var root in roots)
            {
                if (!DirectoryExists(root)) { gone++; continue; }
                var progress = new Progress<CatalogProgress>(value => StatusText = $"Atualizando… {value.Processed} arquivos, {value.Imported} novos");
                imported += (await _catalog.ImportFolderAsync(root, progress)).Imported;
            }
            await ReloadAsync();                                       // recalcula quais arquivos ficaram ausentes
            var scope = roots.Select(r => _all.Where(p => IsInFolder(p.CurrentPath, r))).SelectMany(x => x).DistinctBy(p => p.Id).ToList();
            var missing = scope.Count(p => p.IsMissing);
            report = MissingReport.Build(scope.Where(p => p.IsMissing), DirectoryExists);
            message = $"Atualizado: {imported} novo(s), {missing} ausente(s)" + (gone > 0 ? $"; {gone} pasta(s) não existem mais." : ".");
        }
        catch (OperationCanceledException) { message = "Atualização cancelada."; }
        catch (Exception ex) { await TryReloadAsync(); message = $"Erro ao atualizar: {ex.Message}"; }
        finally { IsBusy = false; }
        StatusText = message;
        // O que não foi encontrado é listado no fim, com a opção de remover do catálogo (item a item ou tudo).
        if (report.Count > 0) ShowMissingDialog(new MissingItemsViewModel(report, RemoveFromCatalogByIdsAsync, ConfirmRemoveAll));
    }
    public async Task MoveSelectedAsync(IEnumerable<PhotoCardViewModel> selected, string folder)
    {
        await RunFileOperationAsync(selected, photo => _fileOperations.MoveAsync(photo, folder), "movida(s)");
    }

    public async Task CopySelectedAsync(IEnumerable<PhotoCardViewModel> selected, string folder, bool addToCatalog)
    {
        var photos = selected.ToList();
        if (photos.Count == 0) return;
        IsBusy = true;
        string message;
        try
        {
            var added = 0;
            foreach (var card in photos) if (await _fileOperations.CopyAsync(card.Photo, folder, addToCatalog) is not null) added++;
            if (added > 0) await ReloadAsync();
            message = addToCatalog ? $"{photos.Count} cópia(s) criada(s); {added} adicionada(s) ao catálogo." : $"{photos.Count} cópia(s) criada(s), sem adicionar ao catálogo.";
        }
        catch (Exception ex) { message = $"Erro ao copiar: {ex.Message}"; }
        finally { IsBusy = false; }
        StatusText = message;
    }

    public async Task RenameSelectedAsync(IEnumerable<PhotoCardViewModel> selected, string name)
    {
        var card = selected.FirstOrDefault();
        if (card is null) return;
        await RunFileOperationAsync([card], photo => _fileOperations.RenameAsync(photo, name), "renomeada(s)");
    }

    public async Task RenameBatchAsync(IEnumerable<PhotoCardViewModel> selected, string template)
    {
        var photos = selected.Select(card => card.Photo).ToList();
        if (photos.Count == 0) return;
        IsBusy = true;
        string message;
        SuspendVideoPlayback();
        try { await _fileOperations.RenameBatchAsync(photos, template); await ReloadAsync(); message = $"{photos.Count} arquivo(s) renomeado(s)."; }
        catch (Exception ex) { await TryReloadAsync(); message = $"Erro ao renomear em lote: {ex.Message}"; }
        finally { IsBusy = false; ResumeVideoPlayback(); }
        StatusText = message;
    }

    public async Task RecycleSelectedAsync(IEnumerable<PhotoCardViewModel> selected)
    {
        await RunFileOperationAsync(selected, photo => _fileOperations.MoveToRecycleBinAsync(photo), "enviada(s) para a Lixeira");
    }

    private async Task RunFileOperationAsync(IEnumerable<PhotoCardViewModel> selected, Func<Photo, Task> operation, string completedText)
    {
        var photos = selected.ToList();
        if (photos.Count == 0) return;
        IsBusy = true;
        SuspendVideoPlayback();   // o player do painel segura o arquivo aberto: precisa soltá-lo antes de mover/renomear/excluir
        string message;
        try { foreach (var card in photos) await operation(card.Photo); await ReloadAsync(); message = $"{photos.Count} arquivo(s) {completedText}."; }
        catch (Exception ex) { await TryReloadAsync(); message = $"Operação interrompida: {ex.Message}"; }
        finally { IsBusy = false; ResumeVideoPlayback(); }
        StatusText = message;
    }

    private async Task LoadAsync()
    {
        try { await ReloadAsync(); }
        catch (Exception ex) { StatusText = $"Não foi possível carregar o catálogo: {ex.Message}"; }
    }

    private async Task TryReloadAsync()
    {
        try { await ReloadAsync(); } catch (Exception) { /* a mensagem da operação original é mais útil */ }
    }

    private async Task ImportFolderCoreAsync(string folderPath)
    {
        IsBusy = true;
        string message;
        try
        {
            var progress = new Progress<CatalogProgress>(value => StatusText = $"Indexando {value.Processed} arquivos… {value.Imported} novos");
            var result = await _catalog.ImportFolderAsync(folderPath, progress);
            await ReloadAsync();
            message = $"{result.Imported} arquivo(s) importado(s). {result.Failed} com erro.";
        }
        catch (OperationCanceledException) { message = "Indexação cancelada."; }
        catch (Exception ex) { message = $"Erro na indexação: {ex.Message}"; }
        finally { IsBusy = false; }
        StatusText = message;
    }

    /// <summary>Lê o catálogo do banco (única consulta pesada) e reconstrói barra lateral e grade.</summary>
    private async Task ReloadAsync()
    {
        var version = ++_loadVersion;
        var photos = (await _catalog.GetPhotosAsync()).ToList();
        if (version != _loadVersion) return;
        _all = photos;
        if (_collectionService is not null)
        {
            try { await _collectionService.SyncPhotoCollectionsAsync(_all); }
            catch { /* resiliência a falhas de coleção */ }
        }
        _cards = [];
        RebuildSidebar();
        await RefreshDuplicateStateAsync();
        ApplyView();
        if (_all.Count == 0) StatusText = "Nenhuma foto catalogada.";
        else if (StatusText.StartsWith("Carregando", StringComparison.Ordinal)) StatusText = $"{_all.Count} foto(s) no catálogo.";
        _ = RefreshStaleMediaInfoAsync();
        _ = OnCatalogReloadedAsync();
    }


    private int _videoInfoVersion;
    private readonly Dictionary<long, int> _thumbnailBust = [];

    /// <summary>
    /// Itens catalogados por versões antigas guardaram dimensões sem a rotação: foto de celular em retrato (orientação EXIF) saía como paisagem
    /// e vídeo em retrato (matriz do MP4) como 1920×1080. Relê só o cabeçalho de cada um em segundo plano, em lotes, corrige dimensões/orientação,
    /// grava no banco (não repete) e refaz as miniaturas que estavam deitadas.
    /// </summary>
    private async Task RefreshStaleMediaInfoAsync()
    {
        var version = ++_videoInfoVersion;
        var stale = _all.Where(p => !p.IsMissing && p.MediaInfoRevision < CatalogService.CurrentMediaInfoRevision && (p.IsVideo || ImageFormats.IsJpeg(p.Extension))).ToList();
        if (stale.Count == 0) return;
        try
        {
            var changedAny = false;
            foreach (var batch in stale.Chunk(100))
            {
                if (version != _videoInfoVersion) return;
                var regenerate = await Task.Run(() => _catalog.RefreshMediaInfoAsync(batch));
                foreach (var photo in regenerate)
                {
                    _thumbnails.Remove(photo.Id);                                  // a miniatura antiga estava deitada
                    _thumbnailCache.Remove(photo.Id);
                    _thumbnailBust[photo.Id] = _thumbnailBust.GetValueOrDefault(photo.Id) + 1;   // o WPF reaproveita imagem pelo mesmo Uri: o sufixo força recarregar
                    if (_cards.TryGetValue(photo.Id, out var thumbCard)) thumbCard.ThumbnailUri = null;
                }
                foreach (var photo in batch) if (_cards.TryGetValue(photo.Id, out var card)) card.NotifyMediaInfoChanged();
                if (regenerate.Count > 0)
                {
                    changedAny = true;
                    _ = LoadThumbnailsAsync(Photos.Where(c => c.ThumbnailUri is null).ToList(), _viewVersion);
                }
            }
            if (changedAny && version == _videoInfoVersion && _orientationFilter != AnyOrientation) ApplyView();
        }
        catch (Exception) { /* é um ajuste de exibição: falhar não pode atrapalhar o uso */ }
    }
    public async Task RefreshDuplicateStateAsync()
    {
        if (_duplicateRepository is null) return;
        try
        {
            _duplicatePhotoIds = (await _duplicateRepository.GetDuplicatePhotoIdsAsync()).ToHashSet();
            var entry = SmartLists.FirstOrDefault(item => item.Key == "duplicates");
            if (entry is not null) entry.Count = _duplicatePhotoIds.Count;
            OnPropertyChanged(nameof(SummaryText));
        }
        catch (Exception) { /* a stale duplicate count must not block the catalog */ }
    }

    private void ApplyView()
    {
        if (_suspendViewUpdates) return;
        var filter = new PhotoFilter(null, CategoryFilter, TagFilter, CollectionFilter, FavoritesOnly, MinimumRating > 0 ? MinimumRating : null, _collectionIdFilter);
        var query = CurrentQuery();
        var visible = ArrangeView(_all.Where(filter.Matches).Where(p => query.Matches(p, QueryContext)).Where(MatchesProFilters).Where(MatchesSmartList).Where(MatchesAllCollections).Where(MatchesNoCollection).Where(MatchesSubtree).Where(MatchesOrientation).Where(MatchesAdvanced).Where(MatchesFolder).ToList());
        var previousId = _selectedPhoto?.Photo.Id;
        var previouslySelected = _selectedCards.Select(c => c.Photo.Id).ToHashSet();
        Photos.ReplaceAll(visible);
        SelectedPhoto = previousId is null ? null : visible.FirstOrDefault(card => card.Photo.Id == previousId);
        if (previouslySelected.Count > 0)
        {
            var restored = visible.Where(card => previouslySelected.Contains(card.Photo.Id)).ToList();
            if (SelectionRestoreRequested is not null) SelectionRestoreRequested.Invoke(restored);
            else UpdateSelection(restored);   // sem tela escutando: a seleção não pode ficar apontando para cartões que já não existem
        }
        UpdateSidebarSelection();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(HasActiveFilters));
        AfterViewApplied(visible);
        _ = LoadThumbnailsAsync(visible, ++_viewVersion);
    }

    private bool MatchesSmartList(Photo photo) => _smartList switch
    {
        "recent" => photo.ImportedAt >= DateTime.UtcNow.AddDays(-30),
        "uncategorized" => string.IsNullOrWhiteSpace(photo.CategoryName),
        "missing" => photo.IsMissing,
        "videos" => photo.IsVideo,
        "duplicates" => _duplicatePhotoIds.Contains(photo.Id),
        _ when ProSmartLists.TryGetValue(_smartList, out var rule) => rule(photo),
        _ => true
    };

    private bool MatchesFolder(Photo photo) =>
        string.IsNullOrEmpty(_folderFilter) || IsInFolder(photo.CurrentPath, _folderFilter);

    private bool MatchesAllCollections(Photo photo) =>
        !_allCollectionsOnly || (photo.CollectionIds.Count > 0 || photo.Collections.Count > 0);

    private bool MatchesNoCollection(Photo photo) =>
        !_noCollectionOnly || (photo.CollectionIds.Count == 0 && photo.Collections.Count == 0);

    private bool MatchesOrientation(Photo photo) => _orientationFilter switch
    {
        "Paisagem" => photo.Orientation == PhotoOrientation.Landscape,
        "Retrato" => photo.Orientation == PhotoOrientation.Portrait,
        "Quadrada" => photo.Orientation == PhotoOrientation.Square,
        _ => true
    };

    private bool MatchesSubtree(Photo photo) =>
        !_subtreeRootId.HasValue || photo.CollectionIds.Any(_subtreeIds.Contains);

    /// <summary>A pasta selecionada inclui as subpastas.</summary>
    private static bool IsInFolder(string photoPath, string folder)
    {
        var directory = Path.GetDirectoryName(photoPath) ?? string.Empty;
        return directory.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || directory.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private PhotoCardViewModel GetCard(Photo photo)
    {
        if (!_cards.TryGetValue(photo.Id, out var card))
        {
            card = new PhotoCardViewModel(photo);
            if (_thumbnailCache.TryGetValue(photo.Id, out var cached)) card.ThumbnailUri = cached;
            _cards[photo.Id] = card;
        }
        return card;
    }

    /// <summary>Miniaturas são resolvidas em segundo plano; a grade aparece de imediato e cada card é preenchido quando pronto.</summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<PhotoCardViewModel> cards, int version)
    {
        foreach (var card in cards)
        {
            if (version != _viewVersion) return;
            if (card.ThumbnailUri is not null) continue;
            try
            {
                var path = await _thumbnails.GetOrCreateAsync(card.Photo.Id, card.Photo.CurrentPath);
                if (path is null) continue;
                var uri = _thumbnailBust.TryGetValue(card.Photo.Id, out var bust) ? new Uri(new Uri(path).AbsoluteUri + "?r=" + bust) : new Uri(path);
                _thumbnailCache[card.Photo.Id] = uri;
                card.ThumbnailUri = uri;
            }
            catch (Exception) { /* uma miniatura com falha não deve interromper as demais */ }
        }
    }

    private async Task LoadPreviewAsync(PhotoCardViewModel? card)
    {
        var version = ++_previewVersion;
        PreviewImage = null;
        if (card is null || card.IsMissing) return;
        var rotation = card.Photo.UserRotation;
        var image = await Task.Run(() => LoadPreviewBitmap(card.Photo.CurrentPath) is { } bitmap ? PhotoManager.Infrastructure.Images.ExifOrientation.Rotate(bitmap, rotation) : null);
        if (version == _previewVersion) PreviewImage = image;
    }

    /// <summary>Decodifica o original já reduzido (nunca carrega a resolução cheia) e libera o arquivo imediatamente.</summary>
    private static BitmapSource? LoadPreviewBitmap(string path) => PhotoManager.Infrastructure.Images.ImageLoader.LoadPreview(path, PreviewDecodeWidth);

    private bool CanMoveSelection(int delta)
    {
        var index = _selectedPhoto is null ? -1 : Photos.IndexOf(_selectedPhoto);
        return index >= 0 && index + delta >= 0 && index + delta < Photos.Count;
    }

    private void MoveSelection(int delta)
    {
        if (CanMoveSelection(delta)) SelectedPhoto = Photos[Photos.IndexOf(_selectedPhoto!) + delta];
    }

    private void SelectSidebar(SidebarEntry entry)
    {
        _suspendViewUpdates = true;
        try
        {
            // "Todas as fotos" volta ao início (limpa tudo). Os demais itens só trocam o LOCAL (lista, pasta, coleção): os filtros da barra
            // (busca, categoria, tag, avaliação, orientação, favoritas) que o usuário definiu são mantidos.
            if (entry.Kind == SidebarKind.Smart && entry.Key == "all") ClearFilters(keepSearch: true);
            else ResetLocation(clearCollection: entry.Kind == SidebarKind.Collection);
            switch (entry.Kind)
            {
                case SidebarKind.Smart when entry.Key == "favorites": FavoritesOnly = true; break;
                case SidebarKind.Smart: _smartList = entry.Key; break;
                case SidebarKind.Category: CategoryFilter = entry.Key; break;
                case SidebarKind.Tag: TagFilter = entry.Key; break;
                case SidebarKind.SmartCollection or SidebarKind.Person: SelectProLocation(entry); break;
                case SidebarKind.Collection when entry.Key == AllCollectionsKey: _allCollectionsOnly = true; break;
                case SidebarKind.Collection when entry.Key == NoCollectionKey: _noCollectionOnly = true; break;
                case SidebarKind.Collection:
                    _collectionIdFilter = entry.Id;
                    CollectionFilter = entry.Label;
                    break;
            }
        }
        finally { _suspendViewUpdates = false; }
        NotifyFilterChoices();
        ApplyView();
    }

    /// <summary>Filtra pela pasta (e subpastas) escolhida na árvore.</summary>
    public void SelectFolder(FolderNode node)
    {
        if (string.Equals(_folderFilter, node.Path, StringComparison.OrdinalIgnoreCase)) return;
        _suspendViewUpdates = true;
        try { ResetLocation(clearCollection: false); _folderFilter = node.Path; }   // mudar de pasta não limpa os filtros da barra
        finally { _suspendViewUpdates = false; }
        NotifyFilterChoices();
        ApplyView();
    }

    /// <summary>Filtra pela coleção selecionada na árvore da barra lateral (regra direta: sem somar descendentes).</summary>
    public void SelectCollectionNode(CollectionNode? node)
    {
        // Mudar CollectionNode.IsSelected (ClearFilters/UpdateSidebarSelection) faz o TreeView disparar SelectedItemChanged, que volta aqui:
        // sem esta trava o ciclo seleciona→limpa→seleciona estoura a pilha assim que uma coleção está selecionada e a árvore é reconstruída.
        if (_selectingCollectionNode) return;
        _selectingCollectionNode = true;
        try { SelectCollectionNodeCore(node); }
        finally { _selectingCollectionNode = false; }
    }

    private bool _selectingCollectionNode;

    private void SelectCollectionNodeCore(CollectionNode? node)
    {
        if (node is null)
        {
            if (SelectedCollectionNode is not null)
            {
                SelectedCollectionNode.IsSelected = false;
                SelectedCollectionNode = null;
            }
            return;
        }

        if (SelectedCollectionNode is not null && SelectedCollectionNode != node)
        {
            SelectedCollectionNode.IsSelected = false;
        }
        SelectedCollectionNode = node;
        node.IsSelected = true;

        _suspendViewUpdates = true;
        try
        {
            ResetLocation(clearCollection: true);
            SelectedCollectionNode = node;   // ClearFilters zera a seleção da árvore; F2/Delete e os comandos dependem dela
            if (node.IsAggregate)
            {
                // "Todas" da coleção: ela e todas as subcoleções (o filtro por Id único fica desligado).
                var owner = CollectionNodes.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Id == node.Id);
                _subtreeRootId = node.Id;
                _subtreeIds = owner is null ? [node.Id] : owner.SelfAndDescendants().Select(n => n.Id).ToHashSet();
            }
            else
            {
                _collectionFilter = node.Path;
                _collectionIdFilter = node.Id;
            }
            _allCollectionsOnly = false;
            _noCollectionOnly = false;
            OnPropertyChanged(nameof(CollectionFilter));
            OnPropertyChanged(nameof(CollectionChoice));
        }
        finally { _suspendViewUpdates = false; }
        NotifyFilterChoices();
        ApplyView();
    }

    /// <summary>Reconstrói a árvore preservando o que o usuário expandiu/recolheu. Raízes e primeiro nível começam expandidos.</summary>
    private void RebuildFolderTree()
    {
        var roots = FolderNode.Build(_all,
            (path, depth) => _folderExpansion.TryGetValue(path, out var expanded) ? expanded : depth < 2,
            node => _folderExpansion[node.Path] = node.IsExpanded);
        Folders.Clear();
        foreach (var root in roots) Folders.Add(root);
        _ = CheckFolderAccessAsync();
    }

    private void ClearFiltersAndApply()
    {
        ClearFilters(keepSearch: false);
        NotifyFilterChoices();
        ApplyView();
    }

    /// <summary>Troca só o local de navegação (lista inteligente, pasta, coleção virtual/subárvore) e preserva os filtros da barra.</summary>
    private void ResetLocation(bool clearCollection)
    {
        _smartList = "all";
        ClearProFilters();
        _folderFilter = string.Empty;
        _allCollectionsOnly = false;
        _noCollectionOnly = false;
        _subtreeRootId = null;
        _subtreeIds = [];
        if (!clearCollection) return;
        _collectionIdFilter = null;
        CollectionFilter = string.Empty;
        if (SelectedCollectionNode is not null)
        {
            SelectedCollectionNode.IsSelected = false;
            SelectedCollectionNode = null;
        }
    }

    private void ClearFilters(bool keepSearch)
    {
        if (!keepSearch) SearchText = string.Empty;
        CategoryFilter = TagFilter = CollectionFilter = _folderFilter = string.Empty;
        _collectionIdFilter = null;
        _subtreeRootId = null;
        _subtreeIds = [];
        _orientationFilter = AnyOrientation;
        OnPropertyChanged(nameof(OrientationChoice));
        ResetAdvancedFilters();
        if (SelectedCollectionNode is not null)
        {
            SelectedCollectionNode.IsSelected = false;
            SelectedCollectionNode = null;
        }
        _smartList = "all";
        ClearProFilters();
        FavoritesOnly = false;
        _allCollectionsOnly = false;
        _noCollectionOnly = false;
        MinimumRating = 0;
    }

    private void NotifyFilterChoices()
    {
        OnPropertyChanged(nameof(FavoritesChoice));
        OnPropertyChanged(nameof(MinimumRatingChoice));
    }

    private void RebuildSidebar()
    {
        RebuildExtensionChoices();
        SmartLists.Clear();
        SmartLists.Add(new(SidebarKind.Smart, "all", "Todas as fotos", "", 0));
        SmartLists.Add(new(SidebarKind.Smart, "favorites", "Favoritas", "", 0));
        SmartLists.Add(new(SidebarKind.Smart, "recent", "Recentes", "", 0));
        SmartLists.Add(new(SidebarKind.Smart, "uncategorized", "Sem categoria", "", 0));
        SmartLists.Add(new(SidebarKind.Smart, "missing", "Arquivos ausentes", "", 0));
        SmartLists.Add(new(SidebarKind.Smart, "duplicates", "Duplicadas", "", 0));
        SmartLists.Add(new(SidebarKind.Smart, "videos", "Vídeos", "\uE714", 0));
        AddProSmartLists();
        RebuildFolderTree();
        FillEntries(CategoryEntries, SidebarKind.Category, "", _all.Where(p => !string.IsNullOrWhiteSpace(p.CategoryName)).GroupBy(p => p.CategoryName!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).Select(g => (g.Key, g.Key, g.Count())));
        FillEntries(TagEntries, SidebarKind.Tag, "", _all.SelectMany(p => p.Tags.Select(t => (t, p))).GroupBy(x => x.t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Take(MaxTagEntries).Select(g => (g.Key, g.Key, g.Count())));
        RebuildCollectionEntries();
        UpdateSidebarCounts();
        RefillChoices(CategoryChoices, CategoryEntries, nameof(CategoryChoice));
        RefillChoices(TagChoices, TagEntries, nameof(TagChoice));
        RefillCollectionChoices();
    }

    private void RebuildCollectionEntries()
    {
        CollectionEntries.Clear();
        VirtualCollectionEntries.Clear();
        var allWithCollectionsCount = _all.Count(p => p.CollectionIds.Count > 0 || p.Collections.Count > 0);
        var noCollectionsCount = _all.Count(p => p.CollectionIds.Count == 0 && p.Collections.Count == 0);

        var allEntry = new SidebarEntry(SidebarKind.Collection, AllCollectionsKey, "Todas", "", allWithCollectionsCount, isVirtual: true, toolTip: "Fotos que pertencem a pelo menos uma coleção", hasSeparatorAfter: false);
        var noEntry = new SidebarEntry(SidebarKind.Collection, NoCollectionKey, "Sem coleção", "", noCollectionsCount, isVirtual: true, toolTip: "Fotos sem nenhuma coleção associada", hasSeparatorAfter: true);

        VirtualCollectionEntries.Add(allEntry);
        VirtualCollectionEntries.Add(noEntry);

        CollectionEntries.Add(allEntry);
        CollectionEntries.Add(noEntry);

        if (_collectionService is not null)
        {
            try
            {
                var collections = _collectionService.GetAllAsync().GetAwaiter().GetResult();
                foreach (var col in collections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var count = _all.Count(p => p.CollectionIds.Contains(col.Id));
                    CollectionEntries.Add(new SidebarEntry(SidebarKind.Collection, col.Name, col.Name, "", count, id: col.Id));
                }
                RebuildCollectionNodes();
                return;
            }
            catch { /* fallback para _all.Collections */ }
        }

        var realCollections = _all.SelectMany(p => p.Collections.Select(c => (c, p)))
            .GroupBy(x => x.c, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new SidebarEntry(SidebarKind.Collection, g.Key, g.Key, "", g.Count()));

        foreach (var entry in realCollections)
        {
            CollectionEntries.Add(entry);
        }
    }

    private void RebuildCollectionNodes()
    {
        if (_collectionService is null) return;
        try
        {
            var allCols = _collectionService.GetAllAsync().GetAwaiter().GetResult();
            var directCounts = allCols.ToDictionary(
                c => c.Id,
                c => _all.Count(p => p.CollectionIds.Contains(c.Id)));

            var roots = CollectionNode.Build(
                allCols,
                directCounts,
                id => _collectionExpansion.TryGetValue(id, out var exp) && exp,
                node => _collectionExpansion[node.Id] = node.IsExpanded);

            CollectionNodes.Clear();
            foreach (var root in roots)
            {
                CollectionNodes.Add(root);
            }

            if (_collectionIdFilter.HasValue)
            {
                var matching = CollectionNodes.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Id == _collectionIdFilter.Value);
                if (matching is not null)
                {
                    matching.IsSelected = true;
                    SelectedCollectionNode = matching;
                }
            }

            RefreshSubtreeSelection();

            UpdateAllCollectionPaths(allCols);
            UpdateSelectedPhotoCollections();
            UpdateBatchAvailableCollections();
        }
        catch { }
    }

    public async Task RebuildCollectionNodesAsync()
    {
        if (_collectionService is null) return;
        try
        {
            var allCols = await _collectionService.GetAllAsync();
            var directCounts = allCols.ToDictionary(
                c => c.Id,
                c => _all.Count(p => p.CollectionIds.Contains(c.Id)));

            var roots = CollectionNode.Build(
                allCols,
                directCounts,
                id => _collectionExpansion.TryGetValue(id, out var exp) && exp,
                node => _collectionExpansion[node.Id] = node.IsExpanded);

            CollectionNodes.Clear();
            foreach (var root in roots)
            {
                CollectionNodes.Add(root);
            }

            if (_collectionIdFilter.HasValue)
            {
                var matching = CollectionNodes.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Id == _collectionIdFilter.Value);
                if (matching is not null)
                {
                    matching.IsSelected = true;
                    SelectedCollectionNode = matching;
                }
            }

            RefreshSubtreeSelection();

            UpdateAllCollectionPaths(allCols);
            UpdateSelectedPhotoCollections();
            UpdateBatchAvailableCollections();
        }
        catch { }
    }

    /// <summary>Depois de reconstruir a árvore: recalcula os Ids do filtro "Todas" da coleção e remarca o item virtual (ou desliga o filtro se a coleção sumiu).</summary>
    private void RefreshSubtreeSelection()
    {
        if (!_subtreeRootId.HasValue) return;
        var owner = CollectionNodes.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Id == _subtreeRootId.Value);
        if (owner?.AggregateNode is { } aggregate)
        {
            var ids = owner.SelfAndDescendants().Select(n => n.Id).ToHashSet();
            var changed = !ids.SetEquals(_subtreeIds);
            _subtreeIds = ids;
            aggregate.IsSelected = true;
            SelectedCollectionNode = aggregate;
            if (changed) ApplyView();   // mover/excluir subcoleção muda o que "Todas" lista
        }
        else
        {
            _subtreeRootId = null;
            _subtreeIds = [];
            if (SelectedCollectionNode is { IsAggregate: true }) SelectedCollectionNode = null;
            ApplyView();
        }
    }

    private void UpdateAllCollectionPaths(IEnumerable<PhotoManager.Domain.Collections.Collection> collections)
    {
        _collectionPaths.Clear();
        var byId = collections.ToDictionary(c => c.Id);
        foreach (var col in collections)
        {
            var segments = new List<string>();
            PhotoManager.Domain.Collections.Collection? curr = col;
            while (curr is not null)
            {
                segments.Add(curr.Name);
                curr = curr.ParentCollectionId.HasValue && byId.TryGetValue(curr.ParentCollectionId.Value, out var parent) ? parent : null;
            }
            segments.Reverse();
            _collectionPaths[col.Id] = string.Join(" / ", segments);
        }
    }

    public DropPlan PlanDrop(
        IReadOnlyList<long> photoIds,
        long? targetCollectionId,
        string? targetCollectionName,
        bool targetIsVirtual,
        bool isTargetHeaderOrEmpty,
        bool copyPressed)
    {
        var photosMap = new Dictionary<long, IReadOnlyList<long>>();
        foreach (var pid in photoIds)
        {
            var photo = _all.FirstOrDefault(p => p.Id == pid);
            if (photo is not null)
                photosMap[pid] = photo.CollectionIds.ToList();
            else
                photosMap[pid] = [];
        }

        var colNames = CollectionNodes.SelectMany(r => r.SelfAndDescendants())
            .ToDictionary(n => n.Id, n => n.Name);

        var request = new DropPlanRequest(
            PhotoIds: photoIds,
            ActiveSourceCollectionId: _collectionIdFilter,
            TargetCollectionId: targetCollectionId,
            TargetCollectionName: targetCollectionName,
            TargetIsVirtual: targetIsVirtual,
            IsTargetHeaderOrEmpty: isTargetHeaderOrEmpty,
            CopyPressed: copyPressed,
            PhotosCollectionsMap: photosMap,
            CollectionNames: colNames);

        return DropPlanner.Plan(request);
    }

    /// <summary>Chamado de handlers <c>async void</c> de drop: nenhuma falha de banco pode escapar e derrubar o aplicativo.</summary>
    public async Task ExecuteDropPlanAsync(DropPlan plan, CancellationToken cancellationToken = default)
    {
        try { await ExecuteDropPlanCoreAsync(plan, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = $"Não foi possível concluir a operação: {ex.Message}";
        }
    }

    private async Task ExecuteDropPlanCoreAsync(DropPlan plan, CancellationToken cancellationToken)
    {
        if (_collectionService is null) return;

        if (plan.Action == DropAction.Add)
        {
            if (plan.TargetCollectionId.HasValue && plan.PhotoIdsToAdd.Count > 0)
            {
                var targetId = plan.TargetCollectionId.Value;
                var added = await _collectionService.AddPhotosAsync(targetId, plan.PhotoIdsToAdd, cancellationToken);
                foreach (var pid in plan.PhotoIdsToAdd)
                {
                    var photo = _all.FirstOrDefault(p => p.Id == pid);
                    if (photo is not null && !photo.CollectionIds.Contains(targetId))
                    {
                        photo.CollectionIds.Add(targetId);
                    }
                    if (_cards.TryGetValue(pid, out var card))
                    {
                        card.NotifyCollectionsChanged();
                    }
                }

                await _collectionService.SyncPhotoCollectionsAsync(_all, cancellationToken);
                UpdateSidebarCounts();
                UpdateSelectedPhotoCollections();
                ApplyView();

                var targetName = plan.TargetCollectionName ?? "Coleção";
                StatusText = plan.PhotoIdsAlreadyInTarget.Count > 0
                    ? $"{added} foto(s) adicionada(s) a “{targetName}”, {plan.PhotoIdsAlreadyInTarget.Count} já pertencia(m)."
                    : $"{added} foto(s) adicionada(s) a “{targetName}”.";
            }
        }
        else if (plan.Action == DropAction.Move)
        {
            if (plan.SourceCollectionId.HasValue && plan.TargetCollectionId.HasValue && plan.PhotoIdsToMove.Count > 0)
            {
                var fromId = plan.SourceCollectionId.Value;
                var toId = plan.TargetCollectionId.Value;
                var moveResult = await _collectionService.MovePhotosAsync(fromId, toId, plan.PhotoIdsToMove, cancellationToken);

                foreach (var pid in plan.PhotoIdsToMove)
                {
                    var photo = _all.FirstOrDefault(p => p.Id == pid);
                    if (photo is not null)
                    {
                        photo.CollectionIds.Remove(fromId);
                        if (!photo.CollectionIds.Contains(toId))
                            photo.CollectionIds.Add(toId);
                    }
                    if (_cards.TryGetValue(pid, out var card))
                    {
                        card.NotifyCollectionsChanged();
                    }
                }

                await _collectionService.SyncPhotoCollectionsAsync(_all, cancellationToken);
                UpdateSidebarCounts();
                UpdateSelectedPhotoCollections();
                ApplyView();

                var sourceName = plan.SourceCollectionName ?? "Coleção de origem";
                var targetName = plan.TargetCollectionName ?? "Coleção de destino";
                StatusText = $"{moveResult.MovedCount} foto(s) movida(s) de “{sourceName}” para “{targetName}”.";
            }
        }
        else if (plan.Action == DropAction.NoOp)
        {
            StatusText = plan.Message;
        }
    }

    /// <summary>Soltar fotos numa pasta da árvore: arrastar move o arquivo; Ctrl/Shift copia.</summary>
    public FolderDropPlan PlanFolderDrop(IReadOnlyList<long> photoIds, FolderNode? target, bool copyPressed)
    {
        var photos = photoIds
            .Select(id => _all.FirstOrDefault(p => p.Id == id))
            .Where(p => p is not null)
            .Select(p => new FolderDropPhoto(p!.Id, Path.GetDirectoryName(p.CurrentPath), p.IsMissing))
            .ToList();
        return FolderDropPlanner.Plan(photos, target?.Path, target?.Label ?? string.Empty, copyPressed);
    }

    /// <summary>Executa o plano pelo <see cref="IFileOperationService"/> (nunca mexe em arquivo direto). Chamado de handler async void: não deixa exceção escapar.</summary>
    public async Task ExecuteFolderDropAsync(FolderDropPlan plan)
    {
        if (!plan.CanDrop || plan.TargetFolder is null || plan.PhotoIds.Count == 0)
        {
            if (!string.IsNullOrEmpty(plan.Message)) StatusText = plan.Message;
            return;
        }

        try
        {
            var ids = plan.PhotoIds.ToHashSet();
            var cards = _all.Where(p => ids.Contains(p.Id)).Select(GetCard).ToList();
            if (plan.Action == FolderDropAction.Move) await MoveSelectedAsync(cards, plan.TargetFolder);
            else await CopySelectedAsync(cards, plan.TargetFolder, addToCatalog: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = $"Não foi possível concluir a operação: {ex.Message}";
        }
    }

    public CollectionDropPlan PlanCollectionDrop(
        long draggedCollectionId,
        long? targetCollectionId,
        string? targetCollectionName,
        bool targetIsRoot,
        bool targetIsVirtual,
        string? draggedCollectionName = null)
    {
        var allNodes = CollectionNodes.SelectMany(r => r.SelfAndDescendants()).ToList();
        var treeLookup = allNodes.ToDictionary(n => n.Id, n => (n.Name, n.ParentId));

        draggedCollectionName ??= treeLookup.TryGetValue(draggedCollectionId, out var info) ? info.Name : null;
        targetCollectionName ??= (targetCollectionId.HasValue && treeLookup.TryGetValue(targetCollectionId.Value, out var targetInfo)) ? targetInfo.Name : null;

        var request = new CollectionDropPlanRequest(
            draggedCollectionId,
            draggedCollectionName,
            targetIsRoot,
            targetCollectionId,
            targetCollectionName,
            targetIsVirtual,
            treeLookup);

        return CollectionDropPlanner.Plan(request);
    }

    public async Task<bool> ExecuteCollectionDropPlanAsync(CollectionDropPlan plan, CancellationToken cancellationToken = default)
    {
        try { return await ExecuteCollectionDropPlanCoreAsync(plan, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = $"Não foi possível mover a coleção: {ex.Message}";
            return false;
        }
    }

    private async Task<bool> ExecuteCollectionDropPlanCoreAsync(CollectionDropPlan plan, CancellationToken cancellationToken)
    {
        if (_collectionService is null) return false;

        if (plan.Action == CollectionDropAction.Move)
        {
            var result = await _collectionService.MoveAsync(plan.DraggedCollectionId, plan.TargetParentId, cancellationToken);
            if (!result.Success)
            {
                StatusText = result.Message ?? "Não foi possível mover a coleção.";
                return false;
            }

            _collectionExpansion[plan.DraggedCollectionId] = true;
            if (plan.TargetParentId.HasValue)
                _collectionExpansion[plan.TargetParentId.Value] = true;

            await _collectionService.SyncPhotoCollectionsAsync(_all, cancellationToken);
            foreach (var card in _cards.Values) card.NotifyCollectionsChanged();
            await RebuildCollectionNodesAsync();
            RefillCollectionChoices();
            UpdateSidebarCounts();

            var movedNode = CollectionNodes.SelectMany(r => r.SelfAndDescendants())
                .FirstOrDefault(n => n.Id == plan.DraggedCollectionId);
            if (movedNode is not null)
            {
                SelectCollectionNode(movedNode);
            }

            var destName = plan.TargetParentId.HasValue ? $"«{plan.TargetParentName}»" : "a raiz";
            StatusText = $"«{plan.DraggedCollectionName}» movida para {destName}.";
            return true;
        }

        if (plan.Action == CollectionDropAction.NoOp)
        {
            StatusText = plan.Message;
            return true;
        }

        StatusText = plan.Message;
        return false;
    }

    public void UpdateSelectedPhotoCollections()
    {
        UpdateCommonCollections();
        SelectedPhotoCollections.Clear();
        AvailableCollectionsForSelectedPhoto.Clear();
        if (SelectedPhoto is null) return;

        var photo = SelectedPhoto.Photo;
        var existingIds = photo.CollectionIds.ToHashSet();

        if (_collectionService is not null)
        {
            var allNodes = CollectionNodes.SelectMany(r => r.SelfAndDescendants()).ToList();
            foreach (var node in allNodes.Where(n => existingIds.Contains(n.Id)))
            {
                SelectedPhotoCollections.Add(new CollectionItemViewModel(node.Id, node.Name, node.Path, node.ParentId));
            }
            foreach (var node in allNodes.Where(n => !existingIds.Contains(n.Id)).OrderBy(n => n.Path, StringComparer.CurrentCultureIgnoreCase))
            {
                AvailableCollectionsForSelectedPhoto.Add(new CollectionItemViewModel(node.Id, node.Name, node.Path, node.ParentId));
            }
        }
        else
        {
            foreach (var name in photo.Collections)
            {
                SelectedPhotoCollections.Add(new CollectionItemViewModel(0, name, name));
            }
        }
    }

    public void UpdateBatchAvailableCollections()
    {
        AvailableCollectionsForBatch.Clear();
        var selectedIds = BatchSelectedCollections.Select(c => c.Id).ToHashSet();
        var allNodes = CollectionNodes.SelectMany(r => r.SelfAndDescendants()).ToList();
        foreach (var node in allNodes.Where(n => !selectedIds.Contains(n.Id)).OrderBy(n => n.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            AvailableCollectionsForBatch.Add(new CollectionItemViewModel(node.Id, node.Name, node.Path, node.ParentId));
        }
    }

    private static void FillEntries(ObservableCollection<SidebarEntry> target, SidebarKind kind, string icon, IEnumerable<(string Key, string Label, int Count)> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(new(kind, item.Key, item.Label, icon, item.Count));
    }

    private void RefillChoices(ObservableCollection<string> choices, IEnumerable<SidebarEntry> entries, string choiceProperty)
    {
        choices.Clear();
        choices.Add(AllChoice);
        foreach (var entry in entries.Where(e => !e.IsVirtual).OrderBy(e => e.Label, StringComparer.CurrentCultureIgnoreCase)) choices.Add(entry.Key);
        OnPropertyChanged(choiceProperty);
    }

    private void RefillCollectionChoices()
    {
        CollectionChoices.Clear();
        CollectionChoices.Add(AnyCollectionChoice);
        if (_collectionService is not null)
        {
            var allNodes = CollectionNodes.SelectMany(r => r.SelfAndDescendants()).OrderBy(n => n.Path, StringComparer.CurrentCultureIgnoreCase);
            foreach (var node in allNodes)
            {
                CollectionChoices.Add(node.Path);
            }
        }
        else
        {
            foreach (var entry in CollectionEntries.Where(e => !e.IsVirtual).OrderBy(e => e.Label, StringComparer.CurrentCultureIgnoreCase))
            {
                CollectionChoices.Add(entry.Key);
            }
        }
        OnPropertyChanged(nameof(CollectionChoice));
    }

    private void UpdateSidebarCounts()
    {
        foreach (var entry in SmartLists)
        {
            entry.Count = entry.Key switch
            {
                "favorites" => _all.Count(p => p.IsFavorite),
                "recent" => _all.Count(p => p.ImportedAt >= DateTime.UtcNow.AddDays(-30)),
                "uncategorized" => _all.Count(p => string.IsNullOrWhiteSpace(p.CategoryName)),
                "missing" => _all.Count(p => p.IsMissing),
                "duplicates" => _duplicatePhotoIds.Count,
                "videos" => _all.Count(p => p.IsVideo),
                _ when ProSmartLists.TryGetValue(entry.Key, out var rule) => _all.Count(rule),
                _ => _all.Count
            };
            entry.ShowCount = entry.Key == "all" || entry.Count != _all.Count || _all.Count == 0;
        }

        foreach (var node in CollectionNodes.SelectMany(r => r.SelfAndDescendants()))
        {
            node.DirectCount = _all.Count(p => p.CollectionIds.Contains(node.Id));
        }
        void RecalcSubtree(CollectionNode node)
        {
            var sum = 0;
            foreach (var child in node.Children)
            {
                RecalcSubtree(child);
                sum += child.DirectCount + child.SubtreeCount;
            }
            node.SubtreeCount = sum;
        }
        foreach (var root in CollectionNodes) RecalcSubtree(root);
        foreach (var parent in CollectionNodes.SelectMany(r => r.SelfAndDescendants()))
        {
            if (parent.AggregateNode is not { } aggregate) continue;
            var ids = parent.SelfAndDescendants().Select(n => n.Id).ToHashSet();
            aggregate.DirectCount = _all.Count(p => p.CollectionIds.Any(ids.Contains));
        }

        foreach (var entry in VirtualCollectionEntries)
        {
            if (entry.Key == AllCollectionsKey)
                entry.Count = _all.Count(p => p.CollectionIds.Count > 0 || p.Collections.Count > 0);
            else if (entry.Key == NoCollectionKey)
                entry.Count = _all.Count(p => p.CollectionIds.Count == 0 && p.Collections.Count == 0);
        }

        foreach (var entry in CollectionEntries)
        {
            if (entry.Key == AllCollectionsKey)
                entry.Count = _all.Count(p => p.CollectionIds.Count > 0 || p.Collections.Count > 0);
            else if (entry.Key == NoCollectionKey)
                entry.Count = _all.Count(p => p.CollectionIds.Count == 0 && p.Collections.Count == 0);
            else if (entry.Id.HasValue)
                entry.Count = _all.Count(p => p.CollectionIds.Contains(entry.Id.Value));
            else
                entry.Count = _all.Count(p => p.Collections.Contains(entry.Key, StringComparer.OrdinalIgnoreCase));
        }
    }

    private void UpdateSidebarSelection()
    {
        foreach (var entry in SmartLists)
            entry.IsSelected = entry.Key == "favorites" ? FavoritesOnly : entry.Key == _smartList && (_smartList != "all" || !FavoritesOnly);
        foreach (var node in Folders.SelectMany(root => root.SelfAndDescendants())) node.IsSelected = !string.IsNullOrEmpty(_folderFilter) && string.Equals(node.Path, _folderFilter, StringComparison.OrdinalIgnoreCase);
        foreach (var entry in CategoryEntries) entry.IsSelected = string.Equals(entry.Key, CategoryFilter, StringComparison.OrdinalIgnoreCase);
        foreach (var entry in TagEntries) entry.IsSelected = string.Equals(entry.Key, TagFilter, StringComparison.OrdinalIgnoreCase);
        foreach (var entry in VirtualCollectionEntries)
            entry.IsSelected = entry.Key switch
            {
                AllCollectionsKey => _allCollectionsOnly,
                NoCollectionKey => _noCollectionOnly,
                _ => false
            };
        foreach (var node in CollectionNodes.SelectMany(r => r.SelfAndDescendants(includeAggregates: true)))
            node.IsSelected = node.IsAggregate
                ? _subtreeRootId.HasValue && node.Id == _subtreeRootId.Value
                : _collectionIdFilter.HasValue && node.Id == _collectionIdFilter.Value;
        foreach (var entry in CollectionEntries)
            entry.IsSelected = entry.Key switch
            {
                AllCollectionsKey => _allCollectionsOnly,
                NoCollectionKey => _noCollectionOnly,
                _ => (entry.Id.HasValue && _collectionIdFilter.HasValue)
                        ? _collectionIdFilter.Value == entry.Id.Value
                        : string.Equals(entry.Key, CollectionFilter, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Label, CollectionFilter, StringComparison.OrdinalIgnoreCase)
            };
    }

    private static string Choice(string filter) => string.IsNullOrEmpty(filter) ? AllChoice : filter;
    private static string FromChoice(string choice) => choice == AllChoice ? string.Empty : choice;
}

public sealed partial class PhotoCardViewModel : ViewModelBase
{
    private Uri? _thumbnailUri;

    public PhotoCardViewModel(Photo photo)
    {
        Photo = photo;
        IsMissing = photo.IsMissing || !File.Exists(photo.CurrentPath);
    }

    public Photo Photo { get; }
    public string FileName => Photo.FileName;
    public string FolderPath => Path.GetDirectoryName(Photo.CurrentPath) ?? string.Empty;
    public bool IsVideo => Photo.IsVideo;
    /// <summary>Retrato ou quadrada: a miniatura aparece inteira (como no Explorer) em vez de cortada, o que também deixa a orientação visível.</summary>
    public bool IsPortraitOrSquare => Photo.Orientation is PhotoOrientation.Portrait or PhotoOrientation.Square;
    /// <summary>Giro manual (graus, horário) aplicado só na exibição; o arquivo não muda.</summary>
    public double UserRotation => Photo.UserRotation;
    public bool HasGps => Photo.HasGps;
    public string CoordinatesText => Photo.HasGps ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Photo.Latitude:0.00000}, {Photo.Longitude:0.00000}") : (Photo.GpsChecked ? "Sem GPS no arquivo" : "Ainda não verificado");
    public string PlaceText => !string.IsNullOrEmpty(Photo.PlaceName) ? Photo.PlaceName : (Photo.HasGps ? "Nome não obtido" : "—");
    public void NotifyLocationChanged() { OnPropertyChanged(nameof(HasGps)); OnPropertyChanged(nameof(CoordinatesText)); OnPropertyChanged(nameof(PlaceText)); }
    public PhotoColor ColorLabel => Photo.ColorLabel;
    public bool HasColor => Photo.ColorLabel != PhotoColor.None;
    public string ColorName => PhotoColors.Name(Photo.ColorLabel);
    public System.Windows.Media.Brush ColorBrush => ColorLookup.BrushFor(Photo.ColorLabel);
    public void NotifyColorChanged() { OnPropertyChanged(nameof(ColorLabel)); OnPropertyChanged(nameof(HasColor)); OnPropertyChanged(nameof(ColorName)); OnPropertyChanged(nameof(ColorBrush)); }
    /// <summary>Depois que dimensões/duração são relidas em segundo plano.</summary>
    public void NotifyMediaInfoChanged()
    {
        OnPropertyChanged(nameof(Dimensions)); OnPropertyChanged(nameof(DetailsText)); OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(OrientationText)); OnPropertyChanged(nameof(IsPortraitOrSquare)); OnPropertyChanged(nameof(UserRotation));
    }
    public string OrientationText => Photo.Orientation switch { PhotoOrientation.Landscape => "Paisagem", PhotoOrientation.Portrait => "Retrato", PhotoOrientation.Square => "Quadrada", _ => "—" };
    /// <summary>Duração formatada (m:ss ou h:mm:ss); vazio para fotos.</summary>
    public string DurationText => Photo.IsVideo ? FormatDuration(Photo.DurationSeconds) : string.Empty;
    public string Dimensions => Photo.IsVideo
        ? string.Join("  ·  ", new[] { Photo.Width.HasValue && Photo.Height.HasValue ? $"{Photo.Width} × {Photo.Height}" : null, Photo.DurationSeconds.HasValue ? DurationText : null }.Where(s => s is not null)) is { Length: > 0 } text ? text : "Vídeo"
        : Photo.Width.HasValue && Photo.Height.HasValue ? $"{Photo.Width} × {Photo.Height}" : "Dimensões indisponíveis";

    public static string FormatDuration(double? seconds)
    {
        if (seconds is not { } s || s < 0) return "–:––";
        var t = TimeSpan.FromSeconds(Math.Round(s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
    public string SizeText => FormatSize(Photo.FileSize);
    public string DetailsText => $"{Dimensions}  ·  {SizeText}";
    public string CreatedText => Photo.CreatedAt.ToLocalTime().ToString("g");
    public string ModifiedText => Photo.ModifiedAt.ToLocalTime().ToString("g");
    /// <summary>Calculado uma vez na criação: evita acesso a disco a cada binding da grade.</summary>
    public bool IsMissing { get; }
    public string StatusText => IsMissing ? "Arquivo ausente" : Photo.Extension.TrimStart('.').ToUpperInvariant();
    public Uri? ThumbnailUri { get => _thumbnailUri; set { _thumbnailUri = value; OnPropertyChanged(); } }
    public string Category { get => Photo.CategoryName ?? string.Empty; set { Photo.CategoryName = value; OnPropertyChanged(); } }
    public string TagsText { get => string.Join(", ", Photo.Tags); set { Photo.Tags = Parse(value); OnPropertyChanged(); } }
    public string CollectionsText => string.Join(", ", Photo.Collections);
    public void NotifyCollectionsChanged() => OnPropertyChanged(nameof(CollectionsText));
    public string PersonalNote { get => Photo.PersonalNote ?? string.Empty; set { Photo.PersonalNote = value; OnPropertyChanged(); } }
    public int Rating { get => Photo.Rating; set { Photo.Rating = Math.Clamp(value, 0, 5); OnPropertyChanged(); } }
    public bool IsFavorite { get => Photo.IsFavorite; set { Photo.IsFavorite = value; OnPropertyChanged(); } }

    private static List<string> Parse(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.0} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };
}

