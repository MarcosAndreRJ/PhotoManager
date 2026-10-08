using System.Collections.ObjectModel;
using System.Globalization;
using PhotoManager.Application.Ai;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Library;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Commands;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

/// <summary>Serviços dos recursos "pró" da Biblioteca (todos opcionais: sem eles a Biblioteca funciona como antes).</summary>
public sealed record LibraryProServices(
    ILibraryRepository Repository,
    ILibrarySettings? Settings = null,
    PhotoAnalysisService? Analysis = null,
    AiIndexService? Ai = null,
    IAiRepository? AiRepository = null,
    IAiModelManager? Models = null,
    IExportService? Export = null,
    IMapTileProvider? Tiles = null,
    IBackupVerifier? Backup = null);

public sealed record FilterChip(string Label, RelayCommand RemoveCommand, string Kind = "filtro");
public sealed record TimelineMark(string Label, string ToolTip, double Offset, int Index, bool IsYear);
public sealed record SummaryLine(string Label, string Value);
public sealed record SortChoice(LibrarySort Value, string Label) { public override string ToString() => Label; }
public sealed record MapPoint(double Latitude, double Longitude, PhotoCardViewModel Card) : PhotoManager.Wpf.Controls.IMapItem;
public sealed record MapBounds(double South, double West, double North, double East)
{
    public bool Contains(double latitude, double longitude) => latitude >= South && latitude <= North && longitude >= West && longitude <= East;
}

/// <summary>Um comando da paleta (Ctrl+K): ação, lugar da barra lateral ou área do app.</summary>
public sealed record PaletteCommand(string Title, string Group, Action Run, string Shortcut = "", string Glyph = "");

public sealed class ClipMarkerViewModel(ClipMarker marker) : ViewModelBase
{
    public ClipMarker Marker { get; private set; } = marker;
    public string Name => Marker.Name;
    public string RangeText => $"{PhotoCardViewModel.FormatDuration(Marker.InSeconds)} → {PhotoCardViewModel.FormatDuration(Marker.OutSeconds)}  ({Marker.Duration:0.#} s)";
    public void Update(ClipMarker marker) { Marker = marker; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(RangeText)); }
}

public sealed partial class LibraryViewModel
{
    private LibraryProServices? _pro;
    private LibraryPreferences _preferences = new(Sort: LibrarySort.Name, Grid: GridStyle.Uniform, ShowFileNames: true, GroupByDay: false, StackPhotos: false);
    private long? _smartCollectionId;
    private LibraryQuery? _smartCollectionQuery;
    private long? _personId;
    private HashSet<long> _personPhotoIds = [];
    private MapBounds? _mapBounds;
    private HashSet<long> _markerPhotoIds = [];
    private readonly HashSet<string> _expandedStacks = [];
    private IReadOnlyDictionary<long, string> _transcripts = new Dictionary<long, string>();
    private IReadOnlyDictionary<long, IReadOnlyList<string>> _aiTags = new Dictionary<long, IReadOnlyList<string>>();
    private Dictionary<long, HashSet<string>> _peopleByPhoto = [];
    private readonly Dictionary<string, IReadOnlySet<long>> _semanticCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _backgroundCts, _exportCts;
    private int _statusVersion;
    private bool _isMapMode, _isPaletteOpen, _isExporting;
    private string _paletteText = string.Empty, _analysisStatus = string.Empty, _exportText = string.Empty, _markerName = string.Empty;
    private double _exportPercent;
    private double? _markerIn, _markerOut;
    private IReadOnlyList<SummaryLine> _viewSummary = [];
    private IReadOnlyList<TimelineMark> _timeline = [];
    private IReadOnlyList<PaletteCommand> _paletteResults = [];
    private IReadOnlyList<MapPoint> _mapPoints = [];

    // ---------- serviços e preferências ----------

    /// <summary>Definido pelo shell: ativa triagem, uso, cortes, coleções inteligentes, exportação, análise e IA.</summary>
    public LibraryProServices? Pro
    {
        get => _pro;
        set
        {
            _pro = value;
            if (value?.Settings is { } settings) ApplyPreferences(settings.Load(), save: false);
            InitProCommands();
            if (_all.Count > 0) { RebuildSidebar(); ApplyView(); }                // o catálogo já carregou sem as listas extras
            OnPropertyChanged(); OnPropertyChanged(nameof(HasPro));
            _ = OnCatalogReloadedAsync();
        }
    }
    public bool HasPro => _pro is not null;

    /// <summary>Todo o catálogo carregado (as Ferramentas analisam a partir dele).</summary>
    public IReadOnlyList<Photo> AllPhotos => _all;

    /// <summary>Abre uma lista inteligente da barra lateral (ex.: "blurry", "rejected") — usado pelas Ferramentas.</summary>
    public void ShowSmartList(string key)
    {
        if (SmartLists.FirstOrDefault(e => e.Key == key) is { } entry) SelectSidebar(entry);
    }

    public IThumbnailService Thumbnails => _thumbnails;

    public LibraryPreferences Preferences => _preferences;
    public bool IsJustifiedGrid => _preferences.Grid == GridStyle.Justified;
    public bool IsUniformGrid => !IsJustifiedGrid;
    public double RowHeight { get => _preferences.RowHeight; set { var v = Math.Clamp(Math.Round(value), 100, 420); if (Math.Abs(v - _preferences.RowHeight) < 1) return; UpdatePreferences(p => p with { RowHeight = v }); } }
    public bool ShowFileNames => _preferences.ShowFileNames;
    public bool HoverPreview => _preferences.HoverPreview;
    public bool ShowRightPanel { get => _preferences.ShowRightPanel; set => UpdatePreferences(p => p with { ShowRightPanel = value }); }

    /// <summary>Muda e grava as preferências; refaz a grade quando algo que muda a lista foi alterado.</summary>
    public void UpdatePreferences(Func<LibraryPreferences, LibraryPreferences> change) => ApplyPreferences(change(_preferences), save: true);

    private void ApplyPreferences(LibraryPreferences preferences, bool save)
    {
        var old = _preferences;
        _preferences = preferences;
        if (save) _pro?.Settings?.Save(preferences);
        foreach (var name in new[] { nameof(Preferences), nameof(IsJustifiedGrid), nameof(IsUniformGrid), nameof(RowHeight), nameof(ShowFileNames), nameof(HoverPreview), nameof(ShowRightPanel), nameof(SortOption), nameof(GroupByDay), nameof(StackPhotos) })
            OnPropertyChanged(name);
        if (old.Sort != preferences.Sort || old.Grid != preferences.Grid || old.GroupByDay != preferences.GroupByDay || old.StackPhotos != preferences.StackPhotos) ApplyView();
        foreach (var card in _cards.Values) card.ShowFileName = preferences.ShowFileNames;
    }

    public static IReadOnlyList<SortChoice> SortChoices { get; } =
    [
        new(LibrarySort.DateNewest, "Data (mais recentes)"), new(LibrarySort.DateOldest, "Data (mais antigas)"), new(LibrarySort.Name, "Nome"),
        new(LibrarySort.Size, "Tamanho"), new(LibrarySort.Rating, "Avaliação"), new(LibrarySort.Duration, "Duração")
    ];
    public SortChoice SortOption { get => SortChoices.First(c => c.Value == _preferences.Sort); set { if (value is not null && value.Value != _preferences.Sort) UpdatePreferences(p => p with { Sort = value.Value }); } }
    public bool GroupByDay { get => _preferences.GroupByDay; set => UpdatePreferences(p => p with { GroupByDay = value }); }
    public bool StackPhotos { get => _preferences.StackPhotos; set => UpdatePreferences(p => p with { StackPhotos = value }); }
    private bool IsDateSort => _preferences.Sort is LibrarySort.DateNewest or LibrarySort.DateOldest;

    // ---------- comandos ----------

    public RelayCommand PickCommand { get; private set; } = null!;
    public RelayCommand RejectCommand { get; private set; } = null!;
    public RelayCommand UnflagCommand { get; private set; } = null!;
    public RelayCommand RecycleRejectedCommand { get; private set; } = null!;
    public RelayCommand MarkUsedCommand { get; private set; } = null!;
    public RelayCommand MarkPublishedCommand { get; private set; } = null!;
    public RelayCommand ClearUsageCommand { get; private set; } = null!;
    public RelayCommand RateSelectionCommand { get; private set; } = null!;
    public RelayCommand SetMarkerInCommand { get; private set; } = null!;
    public RelayCommand SetMarkerOutCommand { get; private set; } = null!;
    public RelayCommand AddMarkerCommand { get; private set; } = null!;
    public RelayCommand DeleteMarkerCommand { get; private set; } = null!;
    public RelayCommand ExportCutsCommand { get; private set; } = null!;
    public RelayCommand SaveSmartCollectionCommand { get; private set; } = null!;
    public RelayCommand DeleteSmartCollectionCommand { get; private set; } = null!;
    public RelayCommand RenamePersonCommand { get; private set; } = null!;
    public RelayCommand ToggleStackCommand { get; private set; } = null!;
    public RelayCommand JumpToCommand { get; private set; } = null!;
    public RelayCommand OpenPaletteCommand { get; private set; } = null!;
    public RelayCommand RunPaletteCommand { get; private set; } = null!;
    public RelayCommand CompareCommand { get; private set; } = null!;
    public RelayCommand ExportCommand { get; private set; } = null!;
    public RelayCommand CancelExportCommand { get; private set; } = null!;
    public RelayCommand ToggleRightPanelCommand { get; private set; } = null!;
    public RelayCommand ToggleMapCommand { get; private set; } = null!;
    public RelayCommand AllowMapCommand { get; private set; } = null!;
    public RelayCommand ToggleGridCommand { get; private set; } = null!;
    public RelayCommand SelectAllCommand { get; private set; } = null!;
    public RelayCommand ClearSelectionCommand { get; private set; } = null!;
    public RelayCommand SemanticSearchCommand { get; private set; } = null!;
    public RelayCommand ApplySuggestionCommand { get; private set; } = null!;
    public RelayCommand MapRegionCommand { get; private set; } = null!;

    /// <summary>A View seleciona tudo/nada na grade (a seleção do ListBox é dela).</summary>
    public event Action? SelectAllRequested;
    public event Action? ClearSelectionRequested;
    /// <summary>Pedido para rolar a grade até um índice (linha do tempo).</summary>
    public event Action<int>? ScrollToIndexRequested;

    // Diálogos substituíveis nos testes.
    public Func<string, bool> ConfirmAction { get; set; } = message => System.Windows.MessageBox.Show(message, "PhotoManager", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
    public Func<string, string, string, string?> PromptText { get; set; } = TextPromptDialog.Ask;
    public Func<string, string, string, string?> PickSaveFile { get; set; } = (title, filter, name) =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = name };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };
    public Action<CompareViewModel> ShowCompare { get; set; } = viewModel => CompareWindow.Open(viewModel);
    public Func<ExportDialogViewModel, bool> ShowExportDialog { get; set; } = viewModel => ExportDialog.Ask(viewModel);
    /// <summary>Comandos de fora da Biblioteca (áreas, tema…) que a paleta também oferece.</summary>
    public Func<IEnumerable<PaletteCommand>>? ExternalPaletteCommands { get; set; }

    private void InitProCommands()
    {
        if (PickCommand is not null) return;
        PickCommand = new RelayCommand(_ => _ = SetPickAsync(ActionTargets(), PickFlag.Picked), _ => HasPro && ActionTargets().Count > 0);
        RejectCommand = new RelayCommand(_ => _ = SetPickAsync(ActionTargets(), PickFlag.Rejected), _ => HasPro && ActionTargets().Count > 0);
        UnflagCommand = new RelayCommand(_ => _ = SetPickAsync(ActionTargets(), PickFlag.None), _ => HasPro && ActionTargets().Count > 0);
        RecycleRejectedCommand = new RelayCommand(_ => _ = RecycleRejectedAsync(), _ => HasPro);
        MarkUsedCommand = new RelayCommand(_ => _ = MarkUsageWithPromptAsync(), _ => HasPro && ActionTargets().Count > 0);
        MarkPublishedCommand = new RelayCommand(_ => _ = SetUsageAsync(ActionTargets(), UsageStatus.Published, null), _ => HasPro && ActionTargets().Count > 0);
        ClearUsageCommand = new RelayCommand(_ => _ = SetUsageAsync(ActionTargets(), UsageStatus.None, null), _ => HasPro && ActionTargets().Count > 0);
        RateSelectionCommand = new RelayCommand(p => { if (int.TryParse(p?.ToString(), out var stars)) _ = RateAsync(ActionTargets(), stars); }, _ => ActionTargets().Count > 0);
        SetMarkerInCommand = new RelayCommand(p => MarkerIn = p is double seconds ? seconds : MarkerIn);
        SetMarkerOutCommand = new RelayCommand(p => MarkerOut = p is double seconds ? seconds : MarkerOut);
        AddMarkerCommand = new RelayCommand(_ => _ = AddMarkerAsync(), _ => CanAddMarker);
        DeleteMarkerCommand = new RelayCommand(p => { if (p is ClipMarkerViewModel marker) _ = DeleteMarkerAsync(marker); });
        ExportCutsCommand = new RelayCommand(_ => _ = ExportCutsAsync(), _ => HasPro && _markerPhotoIds.Count > 0);
        SaveSmartCollectionCommand = new RelayCommand(_ => _ = SaveSearchAsSmartCollectionAsync(), _ => HasPro && !string.IsNullOrWhiteSpace(SearchText));
        DeleteSmartCollectionCommand = new RelayCommand(p => { if (p is SidebarEntry { Kind: SidebarKind.SmartCollection, Id: { } id }) _ = DeleteSmartCollectionAsync(id); });
        RenamePersonCommand = new RelayCommand(p => { if (p is SidebarEntry { Kind: SidebarKind.Person } entry) _ = RenamePersonAsync(entry); });
        ToggleStackCommand = new RelayCommand(p => { if (p is PhotoCardViewModel { StackKey: { } key }) { if (!_expandedStacks.Add(key)) _expandedStacks.Remove(key); ApplyView(); } });
        JumpToCommand = new RelayCommand(p => { if (p is TimelineMark mark) ScrollToIndexRequested?.Invoke(mark.Index); });
        OpenPaletteCommand = new RelayCommand(_ => { PaletteText = string.Empty; IsPaletteOpen = true; });
        RunPaletteCommand = new RelayCommand(p => { if (p is PaletteCommand command) { IsPaletteOpen = false; command.Run(); } });
        CompareCommand = new RelayCommand(_ => OpenCompare(), _ => SelectionCount is >= 2 and <= 4);
        ExportCommand = new RelayCommand(_ => _ = ExportWithDialogAsync(), _ => HasPro && _pro!.Export is not null && ActionTargets().Count > 0 && !IsExporting);
        CancelExportCommand = new RelayCommand(_ => _exportCts?.Cancel(), _ => IsExporting);
        ToggleRightPanelCommand = new RelayCommand(_ => ShowRightPanel = !ShowRightPanel);
        ToggleMapCommand = new RelayCommand(_ => IsMapMode = !IsMapMode);
        AllowMapCommand = new RelayCommand(_ => UpdatePreferences(p => p with { MapAllowed = true }));
        ToggleGridCommand = new RelayCommand(_ => UpdatePreferences(p => p with { Grid = p.Grid == GridStyle.Justified ? GridStyle.Uniform : GridStyle.Justified }));
        SelectAllCommand = new RelayCommand(_ => SelectAllRequested?.Invoke());
        ClearSelectionCommand = new RelayCommand(_ => ClearSelectionRequested?.Invoke(), _ => HasSelection);
        SemanticSearchCommand = new RelayCommand(_ => { if (!string.IsNullOrWhiteSpace(SearchText) && !SearchText.TrimStart().StartsWith("ia:", StringComparison.OrdinalIgnoreCase)) { SearchText = $"ia:\"{SearchText.Trim()}\""; ApplyView(); } }, _ => IsAiSearchReady);
        ApplySuggestionCommand = new RelayCommand(p => { if (p is string text) { SearchText = text; ApplyView(); } });
        MapRegionCommand = new RelayCommand(p => { if (p is PhotoManager.Wpf.Controls.MapRegion r) SetMapRegion(new MapBounds(r.South, r.West, r.North, r.East)); });
    }

    /// <summary>Ações de triagem/uso agem sobre a seleção; sem seleção, sobre a foto aberta.</summary>
    private IReadOnlyList<PhotoCardViewModel> ActionTargets() =>
        _selectedCards.Count > 0 ? _selectedCards : _selectedPhoto is null ? [] : [_selectedPhoto];

    private void RaiseProCommands()
    {
        if (PickCommand is null) return;
        foreach (var command in new[] { PickCommand, RejectCommand, UnflagCommand, MarkUsedCommand, MarkPublishedCommand, ClearUsageCommand, RateSelectionCommand, CompareCommand, ExportCommand, ClearSelectionCommand, SaveSmartCollectionCommand, ExportCutsCommand, AddMarkerCommand, SemanticSearchCommand })
            command.RaiseCanExecuteChanged();
    }

    // ---------- busca com sintaxe, chips e coleções inteligentes ----------

    private LibraryQuery CurrentQuery() => LibraryQuery.Parse(SearchText);
    public string QueryErrorText => string.Join(" ", CurrentQuery().Errors);
    public bool HasQueryError => QueryErrorText.Length > 0;
    public IReadOnlyList<string> SearchSuggestions => string.IsNullOrWhiteSpace(SearchText) ? [] : LibraryQuery.Suggest(SearchText, DynamicValues);

    private IEnumerable<string> DynamicValues(string key) => key switch
    {
        "tag" => _all.SelectMany(p => p.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase),
        "categoria" => _all.Select(p => p.CategoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase),
        "colecao" => _all.SelectMany(p => p.Collections).Distinct(StringComparer.OrdinalIgnoreCase),
        "ext" => _all.Select(p => p.Extension.TrimStart('.').ToLowerInvariant()).Distinct(),
        "local" => _all.Select(p => p.PlaceName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase),
        "pessoa" => PeopleEntries.Select(e => e.Label),
        "etiqueta" => AutoTagVocabulary.Terms.Select(t => t.Tag),
        "ano" => _all.Select(p => p.DisplayDate.Year.ToString(CultureInfo.InvariantCulture)).Distinct().OrderDescending(),
        _ => []
    };

    public ObservableCollection<FilterChip> FilterChips { get; } = [];
    public bool HasFilterChips => FilterChips.Count > 0;
    public ObservableCollection<SidebarEntry> SmartCollectionEntries { get; } = [];
    public ObservableCollection<SidebarEntry> PeopleEntries { get; } = [];

    private ProQueryContext? _queryContext;
    private ILibraryQueryContext QueryContext => _queryContext ??= new ProQueryContext(this);

    private sealed class ProQueryContext(LibraryViewModel library) : ILibraryQueryContext
    {
        public bool IsDuplicate(long photoId) => library._duplicatePhotoIds.Contains(photoId);
        public bool HasMarkers(long photoId) => library._markerPhotoIds.Contains(photoId);
        public bool TranscriptContains(long photoId, string text) => library._transcripts.TryGetValue(photoId, out var transcript) && LibraryQuery.Normalize(transcript).Contains(LibraryQuery.Normalize(text), StringComparison.Ordinal);
        public bool HasPerson(long photoId, string name) => library._peopleByPhoto.TryGetValue(photoId, out var names) && names.Any(n => LibraryQuery.Normalize(n) == LibraryQuery.Normalize(name));
        public bool HasAiTag(long photoId, string tag) => library._aiTags.TryGetValue(photoId, out var tags) && tags.Any(t => LibraryQuery.Normalize(t) == LibraryQuery.Normalize(tag));
        public IReadOnlySet<long>? SemanticMatches(string text) => library._semanticCache.TryGetValue(text, out var ids) ? ids : null;
    }

    /// <summary>Listas inteligentes extras da barra lateral (regras fixas).</summary>
    private Dictionary<string, Func<Photo, bool>>? _proSmartLists;
    private Dictionary<string, Func<Photo, bool>> ProSmartLists => _proSmartLists ??= new()
    {
        ["shorts"] = p => ShortFormRules.IsShortForm(p),
        ["picked"] = p => p.Pick == PickFlag.Picked,
        ["rejected"] = p => p.Pick == PickFlag.Rejected,
        ["unused"] = p => p.IsVideo && p.Usage == UsageStatus.None,
        ["blurry"] = p => !p.IsVideo && p.Sharpness is { } s && s < LibraryQuery.BlurThreshold,
        ["markers"] = p => _markerPhotoIds.Contains(p.Id)
    };

    private void AddProSmartLists()
    {
        if (!HasPro) return;
        SmartLists.Add(new(SidebarKind.Smart, "shorts", "Shorts / Reels", "", 0, toolTip: "Vídeos verticais 9:16 com até 3 minutos"));
        SmartLists.Add(new(SidebarKind.Smart, "picked", "Escolhidas", "", 0, toolTip: "Marcadas com P na triagem"));
        SmartLists.Add(new(SidebarKind.Smart, "rejected", "Rejeitadas", "", 0, toolTip: "Marcadas com X na triagem"));
        SmartLists.Add(new(SidebarKind.Smart, "unused", "Vídeos não usados", "", 0, toolTip: "Clipes ainda sem uso em projeto/publicação"));
        SmartLists.Add(new(SidebarKind.Smart, "blurry", "Desfocadas", "", 0, toolTip: "Fotos com pouca nitidez (análise automática)"));
        SmartLists.Add(new(SidebarKind.Smart, "markers", "Com cortes marcados", "", 0, toolTip: "Vídeos com trechos de entrada/saída marcados"));
    }

    private bool MatchesProFilters(Photo photo) =>
        (_smartCollectionQuery is null || _smartCollectionQuery.Matches(photo, QueryContext))
        && (_personId is null || _personPhotoIds.Contains(photo.Id))
        && (_mapBounds is null || (photo.HasGps && _mapBounds.Contains(photo.Latitude!.Value, photo.Longitude!.Value)));

    private bool HasProFilters => _smartCollectionId.HasValue || _personId.HasValue || _mapBounds is not null;

    private void ClearProFilters()
    {
        _smartCollectionId = null; _smartCollectionQuery = null;
        _personId = null; _personPhotoIds = [];
        _mapBounds = null;
    }

    private void SelectProLocation(SidebarEntry entry)
    {
        if (entry.Kind == SidebarKind.SmartCollection && entry.Id is { } id)
        {
            _smartCollectionId = id;
            _smartCollectionQuery = LibraryQuery.Parse(entry.Key);
        }
        else if (entry.Kind == SidebarKind.Person && entry.Id is { } person)
        {
            _personId = person;
            _personPhotoIds = _peopleByPhoto.Where(p => p.Value.Contains(entry.Label)).Select(p => p.Key).ToHashSet();
        }
    }

    public async Task SaveSearchAsSmartCollectionAsync()
    {
        if (_pro is null || string.IsNullOrWhiteSpace(SearchText)) return;
        if (PromptText("Salvar como coleção inteligente", "Nome da coleção (ela se atualiza sozinha)", string.Empty) is not { Length: > 0 } name) return;
        try
        {
            await _pro.Repository.SaveSmartCollectionAsync(null, name, SearchText);
            await RebuildSmartCollectionsAsync();
            StatusText = $"Coleção inteligente “{name}” criada.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText = $"Não foi possível salvar (já existe uma coleção com esse nome?): {ex.Message}"; }
    }

    private async Task DeleteSmartCollectionAsync(long id)
    {
        if (_pro is null || !ConfirmAction("Excluir esta coleção inteligente? As fotos não são afetadas.")) return;
        await _pro.Repository.DeleteSmartCollectionAsync(id);
        if (_smartCollectionId == id) { ClearProFilters(); ApplyView(); }
        await RebuildSmartCollectionsAsync();
    }

    private async Task RebuildSmartCollectionsAsync()
    {
        if (_pro is null) return;
        var collections = await _pro.Repository.GetSmartCollectionsAsync();
        SmartCollectionEntries.Clear();
        foreach (var collection in collections)
        {
            var query = LibraryQuery.Parse(collection.Query);
            SmartCollectionEntries.Add(new(SidebarKind.SmartCollection, collection.Query, collection.Name, "", _all.Count(p => query.Matches(p, QueryContext)), toolTip: collection.Query, id: collection.Id)
                { IsSelected = _smartCollectionId == collection.Id });
        }
    }

    private void RebuildChips()
    {
        FilterChips.Clear();
        void Add(string label, Action remove, string kind = "filtro") => FilterChips.Add(new FilterChip(label, new RelayCommand(_ => { remove(); NotifyFilterChoices(); OnPropertyChanged(nameof(SearchText)); ApplyView(); }), kind));
        foreach (var term in CurrentQuery().Terms)
        {
            var captured = term;
            Add(term.Label, () => SearchText = LibraryQuery.Without(SearchText, captured), "busca");
        }
        if (_smartList != "all") Add(SmartLists.FirstOrDefault(s => s.Key == _smartList)?.Label ?? _smartList, () => _smartList = "all", "lista");
        if (!string.IsNullOrEmpty(_folderFilter)) Add($"Pasta: {Path.GetFileName(_folderFilter.TrimEnd('\\'))}", () => _folderFilter = string.Empty, "pasta");
        if (!string.IsNullOrEmpty(CategoryFilter)) Add($"Categoria: {CategoryFilter}", () => CategoryFilter = string.Empty);
        if (!string.IsNullOrEmpty(TagFilter)) Add($"Tag: {TagFilter}", () => TagFilter = string.Empty);
        if (!string.IsNullOrEmpty(CollectionFilter) || _subtreeRootId.HasValue) Add($"Coleção: {(string.IsNullOrEmpty(CollectionFilter) ? SelectedCollectionNode?.Name : CollectionFilter)}", () => { CollectionFilter = string.Empty; _collectionIdFilter = null; _subtreeRootId = null; _subtreeIds = []; });
        if (_allCollectionsOnly) Add("Em alguma coleção", () => _allCollectionsOnly = false);
        if (_noCollectionOnly) Add("Sem coleção", () => _noCollectionOnly = false);
        if (FavoritesOnly) Add("Favoritas", () => FavoritesOnly = false);
        if (MinimumRating > 0) Add($"★ {MinimumRating}+", () => MinimumRating = 0);
        if (_orientationFilter != AnyOrientation) Add(_orientationFilter, () => { _orientationFilter = AnyOrientation; OnPropertyChanged(nameof(OrientationChoice)); });
        if (_typeFilter != AllTypes) Add(_typeFilter, () => TypeChoice = AllTypes);
        if (_extensionFilter != AllExtensions) Add(_extensionFilter.ToUpperInvariant(), () => ExtensionChoice = AllExtensions);
        if (_colorFilter != "Qualquer") Add($"Cor: {_colorFilter}", () => ColorFilterChoice = "Qualquer");
        if (_dateFrom.HasValue || _dateTo.HasValue) Add($"Data: {_dateFrom:dd/MM/yy}–{_dateTo:dd/MM/yy}", () => { DateFrom = null; DateTo = null; });
        if (_smartCollectionId is { } smart) Add(SmartCollectionEntries.FirstOrDefault(e => e.Id == smart)?.Label ?? "Coleção inteligente", () => { _smartCollectionId = null; _smartCollectionQuery = null; }, "inteligente");
        if (_personId is { } person) Add(PeopleEntries.FirstOrDefault(e => e.Id == person)?.Label ?? "Pessoa", () => { _personId = null; _personPhotoIds = []; }, "pessoa");
        if (_mapBounds is not null) Add("Área do mapa", () => _mapBounds = null, "mapa");
        OnPropertyChanged(nameof(HasFilterChips));
        OnPropertyChanged(nameof(QueryErrorText)); OnPropertyChanged(nameof(HasQueryError));
        foreach (var entry in SmartCollectionEntries) entry.IsSelected = entry.Id == _smartCollectionId;
        foreach (var entry in PeopleEntries) entry.IsSelected = entry.Id == _personId;
    }

    // ---------- ordenação, pilhas e grupos por dia ----------

    private List<PhotoCardViewModel> ArrangeView(List<Photo> photos)
    {
        IEnumerable<Photo> ordered = _preferences.Sort switch
        {
            LibrarySort.DateNewest => photos.OrderByDescending(p => p.DisplayDate).ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase),
            LibrarySort.DateOldest => photos.OrderBy(p => p.DisplayDate).ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase),
            LibrarySort.Size => photos.OrderByDescending(p => p.FileSize),
            LibrarySort.Rating => photos.OrderByDescending(p => p.Rating).ThenByDescending(p => p.Pick).ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase),
            LibrarySort.Duration => photos.OrderByDescending(p => p.DurationSeconds ?? -1),
            _ => photos                                                                       // Nome: a ordem do catálogo
        };
        var list = ordered.ToList();

        var hidden = new HashSet<long>();
        var stackOf = new Dictionary<long, PhotoStack>();
        if (_preferences.StackPhotos)
            foreach (var stack in StackBuilder.Build(list))
            {
                foreach (var member in stack.Photos) stackOf[member.Id] = stack;
                if (!_expandedStacks.Contains(stack.Key)) hidden.UnionWith(stack.Photos.Where(m => m.Id != stack.Top.Id).Select(m => m.Id));
            }

        var cards = new List<PhotoCardViewModel>(list.Count);
        foreach (var photo in list)
        {
            if (hidden.Contains(photo.Id)) continue;
            var card = GetCard(photo);
            card.ShowFileName = _preferences.ShowFileNames;
            if (stackOf.TryGetValue(photo.Id, out var stack)) card.SetStack(stack.Key, stack.Kind, stack.Top.Id == photo.Id ? stack.Photos.Count : 0, _expandedStacks.Contains(stack.Key));
            else card.SetStack(null, StackKind.Burst, 0, false);
            card.HasMarkers = _markerPhotoIds.Contains(photo.Id);
            cards.Add(card);
        }

        var group = IsDateSort && _preferences.GroupByDay && IsJustifiedGrid;
        if (group)
        {
            var culture = CultureInfo.GetCultureInfo("pt-BR");
            foreach (var day in cards.GroupBy(c => c.Photo.DisplayDate.Date))
            {
                var text = day.Key.ToString("dddd, d 'de' MMMM 'de' yyyy", culture);
                var label = $"{char.ToUpper(text[0], culture)}{text[1..]}   ·   {day.Count()} {(day.Count() == 1 ? "item" : "itens")}";
                foreach (var card in day) card.SetGroup(day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), label);
            }
        }
        else foreach (var card in cards) card.SetGroup(null, null);
        return cards;
    }

    private void AfterViewApplied(IReadOnlyList<PhotoCardViewModel> visible)
    {
        RebuildChips();
        RebuildTimeline(visible);
        UpdateViewSummary(visible);
        if (_isMapMode) UpdateMapPoints(visible);
        OnPropertyChanged(nameof(SearchSuggestions));
        RaiseProCommands();
        if (CurrentQuery().Terms.FirstOrDefault(t => t.Key == "ia") is { } ai && !_semanticCache.ContainsKey(ai.Value)) _ = RunSemanticSearchAsync(ai.Value);
    }

    // ---------- linha do tempo ----------

    public IReadOnlyList<TimelineMark> Timeline { get => _timeline; private set { _timeline = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTimeline)); } }
    public bool HasTimeline => _timeline.Count > 1;

    private void RebuildTimeline(IReadOnlyList<PhotoCardViewModel> visible)
    {
        if (!IsDateSort || visible.Count < 30) { Timeline = []; return; }
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var marks = new List<TimelineMark>();
        var lastYear = -1;
        for (var i = 0; i < visible.Count; i++)
        {
            var date = visible[i].Photo.DisplayDate;
            var monthKey = date.Year * 100 + date.Month;
            if (i > 0)
            {
                var previous = visible[i - 1].Photo.DisplayDate;
                if (previous.Year * 100 + previous.Month == monthKey) continue;
            }
            var count = visible.Skip(i).TakeWhile(c => c.Photo.DisplayDate.Year * 100 + c.Photo.DisplayDate.Month == monthKey).Count();
            var isYear = date.Year != lastYear;
            lastYear = date.Year;
            marks.Add(new TimelineMark(isYear ? date.Year.ToString(CultureInfo.InvariantCulture) : culture.TextInfo.ToTitleCase(date.ToString("MMM", culture).TrimEnd('.')),
                $"{culture.TextInfo.ToTitleCase(date.ToString("MMMM 'de' yyyy", culture))} · {count} itens", i / (double)visible.Count, i, isYear));
        }
        Timeline = marks.Count > 40 ? marks.Where(m => m.IsYear).ToList() : marks;            // muitos meses: só os anos cabem na barra
    }

    // ---------- painel direito sem seleção: resumo da visão ----------

    public IReadOnlyList<SummaryLine> ViewSummary { get => _viewSummary; private set { _viewSummary = value; OnPropertyChanged(); } }
    public string ViewSummaryTitle => FilterChips.FirstOrDefault(c => c.Kind is "pasta" or "lista" or "inteligente" or "pessoa")?.Label ?? (HasActiveFilters ? "Resultado do filtro" : "Todas as fotos");

    private void UpdateViewSummary(IReadOnlyList<PhotoCardViewModel> visible)
    {
        var stats = LibraryStats.Compute(visible.Select(c => c.Photo).ToList(), topFolders: 3);
        var lines = new List<SummaryLine>
        {
            new("Itens", $"{stats.Total:N0}  ({stats.Photos:N0} fotos, {stats.Videos:N0} vídeos)"),
            new("Espaço", LibraryStats.FormatBytes(stats.TotalBytes))
        };
        if (stats.Videos > 0) lines.Add(new("Vídeo", LibraryStats.FormatHours(stats.VideoSeconds) + (stats.ShortForm > 0 ? $"  ·  {stats.ShortForm} Shorts/Reels" : string.Empty)));
        lines.Add(new("Sem avaliação", $"{stats.Unrated:N0}"));
        if (stats.Picked + stats.Rejected > 0) lines.Add(new("Triagem", $"{stats.Picked} escolhidas · {stats.Rejected} rejeitadas ({LibraryStats.FormatBytes(stats.RejectedBytes)})"));
        if (stats.DuplicateExtraCopies > 0) lines.Add(new("Duplicadas", $"{stats.DuplicateExtraCopies} cópias extras ({LibraryStats.FormatBytes(stats.DuplicateReclaimableBytes)})"));
        if (stats.Missing > 0) lines.Add(new("Ausentes", $"{stats.Missing}"));
        if (stats.ByYear.Count > 1) lines.Add(new("Período", $"{stats.ByYear[^1].Label} – {stats.ByYear[0].Label}"));
        if (!string.IsNullOrEmpty(_analysisStatus)) lines.Add(new("Análise", _analysisStatus));
        ViewSummary = lines;
        OnPropertyChanged(nameof(ViewSummaryTitle));
    }

    // ---------- status que some sozinho ----------

    private async void ScheduleStatusClear(string value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith("Carregando", StringComparison.Ordinal)) return;
        var version = ++_statusVersion;
        try { await Task.Delay(TimeSpan.FromSeconds(10)); } catch (TaskCanceledException) { return; }
        if (version == _statusVersion && _statusText == value && !IsBusy) { _statusText = string.Empty; OnPropertyChanged(nameof(StatusText)); }
    }

    // ---------- triagem, avaliação e uso ----------

    public async Task SetPickAsync(IReadOnlyList<PhotoCardViewModel> targets, PickFlag flag, bool? advance = null)
    {
        if (_pro is null || targets.Count == 0) return;
        try
        {
            await _pro.Repository.UpdatePickAsync(targets.Select(c => c.Photo.Id).ToList(), flag);
            foreach (var card in targets) { card.Photo.Pick = flag; card.NotifyProChanged(); }
            StatusText = flag switch
            {
                PickFlag.Picked => $"{targets.Count} escolhida(s).",
                PickFlag.Rejected => $"{targets.Count} rejeitada(s). Use “Rejeitadas” › “Enviar para a Lixeira” quando terminar a triagem.",
                _ => $"Bandeira removida de {targets.Count} item(ns)."
            };
            UpdateSidebarCounts();
            if ((advance ?? _preferences.AutoAdvance) && targets.Count == 1 && ReferenceEquals(targets[0], _selectedPhoto) && CanMoveSelection(1)) MoveSelection(1);
            if (_smartList is "picked" or "rejected") ApplyView();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText = $"Não foi possível marcar: {ex.Message}"; }
    }

    private async Task RecycleRejectedAsync()
    {
        var rejected = _all.Where(p => p.Pick == PickFlag.Rejected && !p.IsMissing).Select(GetCard).ToList();
        if (rejected.Count == 0) { StatusText = "Nenhum item rejeitado."; return; }
        var size = LibraryStats.FormatBytes(rejected.Sum(c => c.Photo.FileSize));
        if (!ConfirmAction($"Enviar {rejected.Count} item(ns) rejeitado(s) ({size}) para a Lixeira do Windows?\n\nDá para restaurar pela Lixeira.")) return;
        await RecycleSelectedAsync(rejected);
    }

    public async Task RateAsync(IReadOnlyList<PhotoCardViewModel> targets, int stars)
    {
        foreach (var card in targets)
        {
            card.Rating = Math.Clamp(stars, 0, 5);
            await SavePhotoAsync(card);
        }
        StatusText = stars == 0 ? $"Estrelas removidas de {targets.Count} item(ns)." : $"{targets.Count} item(ns) com {stars} estrela(s).";
    }

    public async Task SetUsageAsync(IReadOnlyList<PhotoCardViewModel> targets, UsageStatus status, string? note)
    {
        if (_pro is null || targets.Count == 0) return;
        await _pro.Repository.UpdateUsageAsync(targets.Select(c => c.Photo.Id).ToList(), status, note);
        foreach (var card in targets) { card.Photo.Usage = status; card.Photo.UsageNote = note; card.NotifyProChanged(); }
        UpdateSidebarCounts();
        if (_smartList == "unused") ApplyView();
        StatusText = status switch
        {
            UsageStatus.Published => $"{targets.Count} item(ns) marcado(s) como publicado(s).",
            UsageStatus.Used => $"{targets.Count} item(ns) marcado(s) como usado(s){(string.IsNullOrWhiteSpace(note) ? string.Empty : $" em “{note}”")}.",
            _ => $"Status de uso removido de {targets.Count} item(ns)."
        };
    }

    private async Task MarkUsageWithPromptAsync()
    {
        var targets = ActionTargets();
        if (targets.Count == 0) return;
        var note = PromptText("Marcar como usado", "Onde foi usado? (opcional — ex.: Vlog Valinhos #12)", targets[0].Photo.UsageNote ?? string.Empty);
        if (note is null) return;
        await SetUsageAsync(targets, UsageStatus.Used, note.Trim().Length == 0 ? null : note.Trim());
    }

    // ---------- marcadores de trecho (vídeos) ----------

    public ObservableCollection<ClipMarkerViewModel> Markers { get; } = [];
    public double? MarkerIn { get => _markerIn; set { _markerIn = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarkerRangeText)); AddMarkerCommand?.RaiseCanExecuteChanged(); } }
    public double? MarkerOut { get => _markerOut; set { _markerOut = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarkerRangeText)); AddMarkerCommand?.RaiseCanExecuteChanged(); } }
    public string MarkerName { get => _markerName; set { _markerName = value ?? string.Empty; OnPropertyChanged(); } }
    public string MarkerRangeText => $"Entrada {(MarkerIn is { } i ? PhotoCardViewModel.FormatDuration(i) : "—")}   ·   Saída {(MarkerOut is { } o ? PhotoCardViewModel.FormatDuration(o) : "—")}";
    public bool CanAddMarker => _pro is not null && _selectedPhoto is { IsVideo: true } && MarkerIn.HasValue && MarkerOut.HasValue && Math.Abs(MarkerOut.Value - MarkerIn.Value) >= 0.2;

    private async Task LoadMarkersAsync(PhotoCardViewModel? card)
    {
        MarkerIn = null; MarkerOut = null; MarkerName = string.Empty;
        Markers.Clear();
        if (_pro is null || card is not { IsVideo: true }) return;
        foreach (var marker in await _pro.Repository.GetMarkersAsync(card.Photo.Id))
            if (ReferenceEquals(card, _selectedPhoto)) Markers.Add(new ClipMarkerViewModel(marker));
    }

    public async Task AddMarkerAsync()
    {
        if (!CanAddMarker) return;
        var card = _selectedPhoto!;
        var name = string.IsNullOrWhiteSpace(MarkerName) ? $"{Path.GetFileNameWithoutExtension(card.FileName)} #{Markers.Count + 1}" : MarkerName.Trim();
        var marker = await _pro!.Repository.AddMarkerAsync(card.Photo.Id, MarkerIn!.Value, MarkerOut!.Value, name, 0);
        Markers.Add(new ClipMarkerViewModel(marker));
        _markerPhotoIds.Add(card.Photo.Id);
        card.HasMarkers = true;
        MarkerIn = null; MarkerOut = null; MarkerName = string.Empty;
        UpdateSidebarCounts();
        StatusText = $"Trecho “{name}” marcado ({marker.Duration:0.#} s).";
    }

    private async Task DeleteMarkerAsync(ClipMarkerViewModel marker)
    {
        if (_pro is null) return;
        await _pro.Repository.DeleteMarkerAsync(marker.Marker.Id);
        Markers.Remove(marker);
        if (Markers.Count == 0 && _selectedPhoto is { } card) { _markerPhotoIds.Remove(card.Photo.Id); card.HasMarkers = false; UpdateSidebarCounts(); }
    }

    /// <summary>EDL ou FCPXML com os trechos dos vídeos selecionados (ou de todos os da visão), em ordem de gravação.</summary>
    public async Task ExportCutsAsync()
    {
        if (_pro is null) return;
        var scope = (_selectedCards.Count > 0 ? _selectedCards : (IEnumerable<PhotoCardViewModel>)Photos).Select(c => c.Photo).Where(p => _markerPhotoIds.Contains(p.Id)).ToDictionary(p => p.Id);
        if (scope.Count == 0) { StatusText = "Nenhum vídeo com trechos marcados na seleção."; return; }
        var items = (await _pro.Repository.GetMarkersAsync()).Where(m => scope.ContainsKey(m.PhotoId))
            .OrderBy(m => scope[m.PhotoId].DisplayDate).ThenBy(m => m.InSeconds).Select(m => (m, scope[m.PhotoId])).ToList();
        if (PickSaveFile("Exportar cortes para o editor de vídeo", "DaVinci Resolve / Final Cut (*.fcpxml)|*.fcpxml|Premiere / qualquer editor (*.edl)|*.edl", $"cortes_{DateTime.Now:yyyy-MM-dd}") is not { } path) return;
        var title = Path.GetFileNameWithoutExtension(path);
        var content = path.EndsWith(".edl", StringComparison.OrdinalIgnoreCase) ? ClipExport.ToEdl(title, items) : ClipExport.ToFcpxml(title, items);
        await File.WriteAllTextAsync(path, content);
        StatusText = $"{items.Count} trecho(s) de {scope.Count} vídeo(s) exportado(s) para {Path.GetFileName(path)}.";
    }

    // ---------- pessoas (IA) ----------

    private async Task RenamePersonAsync(SidebarEntry entry)
    {
        if (_pro?.AiRepository is null || entry.Id is not { } id) return;
        if (PromptText("Nome da pessoa", "Como esta pessoa se chama?", entry.Label.StartsWith("Pessoa ", StringComparison.Ordinal) ? string.Empty : entry.Label) is not { } name) return;
        await _pro.AiRepository.RenamePersonAsync(id, name);
        await LoadAiCachesAsync();
    }

    public bool IsAiSearchReady => _pro?.Ai?.Engines.IsReady(AiCapability.SemanticSearch) == true;
    public string AiSearchToolTip => IsAiSearchReady ? "Buscar pelo significado (IA local): “praia ao pôr do sol”, “barraca de feira”…" : "Busca por descrição: ative a IA local em Configurações.";

    private async Task RunSemanticSearchAsync(string text)
    {
        if (_pro?.Ai is not { } ai || !ai.Engines.IsReady(AiCapability.SemanticSearch)) { _semanticCache[text] = new HashSet<long>(); return; }
        try
        {
            var hits = await ai.SearchAsync(text);
            _semanticCache[text] = hits.Select(h => h.PhotoId).ToHashSet();
            ApplyView();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText = $"Busca por descrição falhou: {ex.Message}"; }
    }

    private async Task LoadAiCachesAsync()
    {
        if (_pro?.AiRepository is not { } repository) return;
        _transcripts = await repository.GetTranscriptsAsync();
        _aiTags = await repository.GetTagsAsync();
        var people = await repository.GetPeopleAsync();
        var faces = await repository.GetFacesAsync();
        var names = people.ToDictionary(p => p.Id, p => p.DisplayName);
        _peopleByPhoto = faces.Where(f => f.PersonId.HasValue && names.ContainsKey(f.PersonId.Value)).GroupBy(f => f.PhotoId)
            .ToDictionary(g => g.Key, g => g.Select(f => names[f.PersonId!.Value]).ToHashSet(StringComparer.OrdinalIgnoreCase));
        PeopleEntries.Clear();
        foreach (var person in people)
            PeopleEntries.Add(new(SidebarKind.Person, person.Id.ToString(CultureInfo.InvariantCulture), person.DisplayName, "", person.FaceCount, toolTip: "Clique para ver as fotos; botão direito para dar nome", id: person.Id) { IsSelected = _personId == person.Id });
        OnPropertyChanged(nameof(HasPeople));
    }

    public bool HasPeople => PeopleEntries.Count > 0;

    // ---------- carga e trabalho em segundo plano ----------

    public string AnalysisStatus { get => _analysisStatus; private set { _analysisStatus = value; OnPropertyChanged(); } }

    private async Task OnCatalogReloadedAsync()
    {
        if (_pro is null) return;
        try
        {
            _markerPhotoIds = (await _pro.Repository.GetMarkersAsync()).Select(m => m.PhotoId).ToHashSet();
            await RebuildSmartCollectionsAsync();
            await LoadAiCachesAsync();
            UpdateSidebarCounts();
            foreach (var card in _cards.Values) card.HasMarkers = _markerPhotoIds.Contains(card.Photo.Id);
        }
        catch (Exception) { /* extras não podem impedir a Biblioteca de abrir */ }
        StartBackgroundWork();
    }

    /// <summary>Análise de imagens (parecidas/desfocadas) e indexação da IA, uma por vez, com prioridade baixa; recomeça a cada recarga.</summary>
    private void StartBackgroundWork()
    {
        _backgroundCts?.Cancel();
        var cts = _backgroundCts = new CancellationTokenSource();
        var snapshot = _all.ToList();
        // Os Progress<T> nascem aqui (na thread da tela) e o trabalho pesado vai para Task.Run; o que mexe em coleções da tela volta para cá.
        var analysisProgress = new Progress<AnalysisProgress>(p => AnalysisStatus = p.Done >= p.Total ? string.Empty : $"analisando imagens {p.Done:N0} de {p.Total:N0}");
        var aiProgress = new Progress<AiIndexProgress>(p => AnalysisStatus = p.Done >= p.Total ? string.Empty : $"IA: {AiModelCatalog.Describe(p.Capability).ToLowerInvariant()} {p.Done:N0} de {p.Total:N0}");
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                if (_pro?.Analysis is { } analysis && snapshot.Any(p => p.PerceptualHash is null && !p.IsMissing))
                {
                    var analyzed = await Task.Run(() => analysis.AnalyzeAsync(snapshot, analysisProgress, cts.Token), cts.Token);
                    if (analyzed > 0)
                    {
                        UpdateSidebarCounts();
                        foreach (var card in Photos) card.NotifyProChanged();
                        if (_smartList == "blurry") ApplyView();
                    }
                }
                if (_pro?.Ai is { } ai && _preferences.AiEnabled is { Count: > 0 } enabled && enabled.Any(ai.Engines.IsReady))
                {
                    var thumbnails = _thumbnails;
                    await Task.Run(() => ai.IndexAsync(snapshot, enabled, async p => p.IsVideo ? await thumbnails.GetOrCreateAsync(p.Id, p.CurrentPath, cts.Token) : p.CurrentPath, aiProgress, cts.Token), cts.Token);
                    await LoadAiCachesAsync();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { AnalysisStatus = string.Empty; }
        }
    }

    /// <summary>Para o trabalho em segundo plano (testes e encerramento).</summary>
    public void StopBackgroundWork() => _backgroundCts?.Cancel();

    // ---------- comparar 2–4 ----------

    private void OpenCompare()
    {
        if (SelectionCount is < 2 or > 4) return;
        ShowCompare(new CompareViewModel(this, _selectedCards.ToList()));
    }

    // ---------- exportar ----------

    public bool IsExporting { get => _isExporting; private set { _isExporting = value; OnPropertyChanged(); ExportCommand?.RaiseCanExecuteChanged(); CancelExportCommand?.RaiseCanExecuteChanged(); } }
    public string ExportText { get => _exportText; private set { _exportText = value; OnPropertyChanged(); } }
    public double ExportPercent { get => _exportPercent; private set { _exportPercent = value; OnPropertyChanged(); } }

    private async Task ExportWithDialogAsync()
    {
        var targets = ActionTargets().Select(c => c.Photo).ToList();
        var dialog = new ExportDialogViewModel(_preferences.Presets, targets.Count, targets.Count(p => p.IsVideo));
        if (!ShowExportDialog(dialog) || dialog.Destination is not { Length: > 0 } destination) return;
        if (dialog.SaveAsPreset) UpdatePreferences(p => p with { ExportPresets = [.. p.Presets.Where(x => x.Name != dialog.Preset.Name), dialog.Preset] });
        await ExportAsync(targets, dialog.Preset, destination);
    }

    public async Task ExportAsync(IReadOnlyList<Photo> photos, ExportPreset preset, string destination)
    {
        if (_pro?.Export is not { } export || IsExporting) return;
        _exportCts = new CancellationTokenSource();
        IsExporting = true;
        try
        {
            var progress = new Progress<ExportProgress>(p => { ExportPercent = p.Total == 0 ? 100 : p.Done * 100.0 / p.Total; ExportText = $"Exportando {Math.Min(p.Done + 1, p.Total)} de {p.Total} · {p.Current}"; });
            var result = await export.ExportAsync(photos, preset, destination, progress, _exportCts.Token);
            StatusText = result.Cancelled ? $"Exportação cancelada ({result.Created.Count} já exportado(s))."
                : $"{result.Created.Count} item(ns) exportado(s) para {destination}" + (result.Failed.Count > 0 ? $"; {result.Failed.Count} com erro ({result.Failed[0].Error})." : ".");
        }
        finally { IsExporting = false; ExportText = string.Empty; }
    }

    // ---------- mapa ----------

    public bool IsMapMode { get => _isMapMode; set { if (_isMapMode == value) return; _isMapMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsGridMode)); if (value) UpdateMapPoints(Photos); } }
    public bool IsGridMode => !_isMapMode;
    public bool MapAllowed => _preferences.MapAllowed;
    public IReadOnlyList<MapPoint> MapPoints { get => _mapPoints; private set { _mapPoints = value; OnPropertyChanged(); OnPropertyChanged(nameof(MapSummary)); } }
    public string MapSummary => $"{_mapPoints.Count:N0} de {Photos.Count:N0} itens têm GPS" + (_all.Any(p => !p.GpsChecked && !p.IsMissing) ? " · use “Obter localização” para ler o GPS dos arquivos ainda não verificados" : string.Empty);
    public IMapTileProvider? Tiles => _pro?.Tiles;

    private void UpdateMapPoints(IEnumerable<PhotoCardViewModel> cards) =>
        MapPoints = cards.Where(c => c.Photo.HasGps).Select(c => new MapPoint(c.Photo.Latitude!.Value, c.Photo.Longitude!.Value, c)).ToList();

    /// <summary>Área desenhada no mapa (Shift+arrastar) ou um grupo clicado: vira filtro (ficha "Área do mapa").</summary>
    public void SetMapRegion(MapBounds? bounds)
    {
        _mapBounds = bounds;
        ApplyView();
    }

    // ---------- paleta de comandos (Ctrl+K) ----------

    public bool IsPaletteOpen { get => _isPaletteOpen; set { if (_isPaletteOpen == value) return; _isPaletteOpen = value; OnPropertyChanged(); if (value) FilterPalette(); } }
    public string PaletteText { get => _paletteText; set { _paletteText = value ?? string.Empty; OnPropertyChanged(); FilterPalette(); } }
    public IReadOnlyList<PaletteCommand> PaletteResults { get => _paletteResults; private set { _paletteResults = value; OnPropertyChanged(); } }

    private void FilterPalette()
    {
        var all = BuildPalette().ToList();
        var words = LibraryQuery.Normalize(_paletteText).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        PaletteResults = all.Where(c => words.All(w => LibraryQuery.Normalize(c.Title + " " + c.Group).Contains(w, StringComparison.Ordinal)))
            .OrderBy(c => words.Length > 0 && LibraryQuery.Normalize(c.Title).StartsWith(words[0], StringComparison.Ordinal) ? 0 : 1)
            .Take(40).ToList();
    }

    private IEnumerable<PaletteCommand> BuildPalette()
    {
        yield return new("Selecionar tudo", "Ação", () => SelectAllRequested?.Invoke(), "Ctrl+A", "");
        yield return new("Limpar filtros", "Ação", () => ClearFiltersCommand.Execute(null), "", "");
        yield return new("Revisar a foto selecionada", "Ação", () => EnterReview(), "Enter", "");
        if (HasPro)
        {
            yield return new("Escolher (P)", "Triagem", () => PickCommand.Execute(null), "P", "");
            yield return new("Rejeitar (X)", "Triagem", () => RejectCommand.Execute(null), "X", "");
            yield return new("Tirar bandeira (U)", "Triagem", () => UnflagCommand.Execute(null), "U", "");
            yield return new("Comparar selecionadas (2 a 4)", "Triagem", OpenCompare, "", "");
            yield return new("Enviar rejeitadas para a Lixeira", "Triagem", () => RecycleRejectedCommand.Execute(null), "", "");
            yield return new("Marcar como usado…", "Uso", () => MarkUsedCommand.Execute(null), "", "");
            yield return new("Marcar como publicado", "Uso", () => MarkPublishedCommand.Execute(null), "", "");
            yield return new("Exportar seleção…", "Arquivos", () => ExportCommand.Execute(null), "Ctrl+E", "");
            yield return new("Exportar cortes para o editor (EDL/FCPXML)…", "Vídeo", () => ExportCutsCommand.Execute(null), "", "");
            yield return new("Salvar busca como coleção inteligente…", "Busca", () => SaveSmartCollectionCommand.Execute(null), "", "");
            yield return new(IsMapMode ? "Voltar para a grade" : "Ver no mapa", "Exibição", () => IsMapMode = !IsMapMode, "", "");
            yield return new(IsJustifiedGrid ? "Grade uniforme" : "Grade justificada", "Exibição", () => ToggleGridCommand.Execute(null), "", "");
            yield return new(ShowRightPanel ? "Esconder painel de detalhes" : "Mostrar painel de detalhes", "Exibição", () => ShowRightPanel = !ShowRightPanel, "", "");
            foreach (var sort in SortChoices) yield return new($"Ordenar por {sort.Label.ToLowerInvariant()}", "Exibição", () => SortOption = sort, "", "");
        }
        foreach (var entry in SmartLists) yield return new(entry.Label, "Biblioteca", () => SelectSidebar(entry), "", "");
        foreach (var entry in SmartCollectionEntries) yield return new(entry.Label, "Coleção inteligente", () => SelectSidebar(entry), "", "");
        foreach (var entry in PeopleEntries) yield return new(entry.Label, "Pessoa", () => SelectSidebar(entry), "", "");
        foreach (var node in CollectionNodes.SelectMany(n => n.SelfAndDescendants())) yield return new(node.Path, "Coleção", () => SelectCollectionNode(node), "", "");
        foreach (var folder in Folders.SelectMany(f => f.SelfAndDescendants())) yield return new(folder.Label, "Pasta · " + folder.Path, () => SelectFolder(folder), "", "");
        if (ExternalPaletteCommands?.Invoke() is { } external) foreach (var command in external) yield return command;
    }
}

public sealed partial class PhotoCardViewModel : PhotoManager.Wpf.Controls.IJustifiedItem
{
    private bool _showFileName = true, _hasMarkers, _isStackExpanded;
    private string? _groupKey, _groupLabel, _stackKey;
    private int _stackCount;
    private StackKind _stackKind;

    /// <summary>Proporção como exibida (giro manual incluído); 1,5 enquanto as dimensões não são conhecidas.</summary>
    public double AspectRatio
    {
        get
        {
            if (Photo.Width is not > 0 || Photo.Height is not > 0) return 1.5;
            double w = Photo.Width.Value, h = Photo.Height.Value;
            if (Photo.UserRotation is 90 or 270) (w, h) = (h, w);
            return Math.Clamp(w / h, 0.4, 3.0);
        }
    }
    public string? GroupKey => _groupKey;
    public string? GroupLabel => _groupLabel;
    public void SetGroup(string? key, string? label) { _groupKey = key; _groupLabel = label; }

    /// <summary>Título humano ("27 set · 05:59"); o nome do arquivo fica na dica (ou no lugar, se o usuário preferir).</summary>
    public string Title => _showFileName ? FileName : Photo.DisplayDate.ToString("d MMM · HH:mm", CultureInfo.GetCultureInfo("pt-BR")).Replace(".", string.Empty);
    public bool ShowFileName { get => _showFileName; set { if (_showFileName == value) return; _showFileName = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); } }

    public PickFlag Pick => Photo.Pick;
    public bool IsPicked => Photo.Pick == PickFlag.Picked;
    public bool IsRejected => Photo.Pick == PickFlag.Rejected;
    public UsageStatus Usage => Photo.Usage;
    public bool HasUsage => Photo.Usage != UsageStatus.None;
    public string UsageText => Photo.Usage switch
    {
        UsageStatus.Published => "Publicado" + (string.IsNullOrEmpty(Photo.UsageNote) ? string.Empty : $" · {Photo.UsageNote}"),
        UsageStatus.Used => "Usado" + (string.IsNullOrEmpty(Photo.UsageNote) ? string.Empty : $" em {Photo.UsageNote}"),
        _ => "Sem uso"
    };
    public bool IsShortForm => ShortFormRules.IsShortForm(Photo);
    public bool IsBlurry => !Photo.IsVideo && Photo.Sharpness is { } s && s < LibraryQuery.BlurThreshold;
    public bool HasMarkers { get => _hasMarkers; set { if (_hasMarkers == value) return; _hasMarkers = value; OnPropertyChanged(); } }
    public bool HasRating => Photo.Rating > 0;
    /// <summary>Vídeo presente: a prévia ao passar o mouse usa este caminho.</summary>
    public string? VideoPreviewPath => IsVideo && !IsMissing ? Photo.CurrentPath : null;
    public double VideoRotation => Photo.VideoDisplayRotation;

    public string? StackKey => _stackKey;
    public int StackCount => _stackCount;
    public bool IsStackTop => _stackCount > 1;
    public bool IsStackExpanded => _isStackExpanded;
    public string StackText => _stackKind == StackKind.RawJpeg ? $"RAW+JPG" : $"{_stackCount} ▸";
    public string StackToolTip => _stackKind == StackKind.RawJpeg ? "RAW e JPEG do mesmo clique — clique para ver os dois" : $"Rajada com {_stackCount} fotos — clique para expandir/recolher";
    public void SetStack(string? key, StackKind kind, int count, bool expanded)
    {
        if (_stackKey == key && _stackCount == count && _isStackExpanded == expanded) return;
        _stackKey = key; _stackKind = kind; _stackCount = count; _isStackExpanded = expanded;
        OnPropertyChanged(nameof(StackCount)); OnPropertyChanged(nameof(IsStackTop)); OnPropertyChanged(nameof(StackText)); OnPropertyChanged(nameof(StackToolTip)); OnPropertyChanged(nameof(IsStackExpanded));
    }

    public void NotifyProChanged()
    {
        foreach (var name in new[] { nameof(Pick), nameof(IsPicked), nameof(IsRejected), nameof(Usage), nameof(HasUsage), nameof(UsageText), nameof(IsBlurry), nameof(HasRating), nameof(Rating) })
            OnPropertyChanged(name);
    }
}
