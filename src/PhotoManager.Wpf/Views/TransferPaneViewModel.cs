using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Transfer;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Commands;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

public enum TransferViewMode { Grid, List }

public sealed record TransferSortOption(TransferSortField Field, string Label)
{
    public override string ToString() => Label;                                   // a caixa de seleção mostra o rótulo, não o nome do tipo
}

/// <summary>Algo mudou dentro de <see cref="Parent"/>. <see cref="OldPath"/> = item renomeado (para <see cref="NewPath"/>) ou excluído (NewPath nulo).</summary>
public sealed record TransferFolderChange(string Parent, string? OldPath = null, string? NewPath = null);

public sealed record DetailRow(string Label, string Value);

/// <summary>Um item exibido num painel de Transferência (pasta ou arquivo). Miniatura, cor, comparação e duplicata chegam em segundo plano.</summary>
public sealed class TransferItemViewModel(TransferEntry entry) : ViewModelBase
{
    private Uri? _thumbnailUri;
    private PhotoColor _color;
    private bool _isCataloged, _isEditing;
    private string _editText = string.Empty;
    private CompareState _compare;
    private IReadOnlyList<string> _duplicates = [];

    public TransferEntry Entry { get; } = entry;
    public string Path => Entry.Path;
    public string Name => Entry.Name;
    public bool IsFolder => Entry.IsFolder;
    public bool IsVideo => Entry.Kind == TransferEntryKind.Video;
    public bool IsMedia => Entry.IsMedia;
    public bool ShowGlyph => ThumbnailUri is null;
    public Uri? ThumbnailUri { get => _thumbnailUri; set { _thumbnailUri = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowGlyph)); } }
    /// <summary>Ícone da fonte do Windows para pasta/vídeo/foto/outro enquanto não há miniatura.</summary>
    public string Glyph => Entry.Kind switch { TransferEntryKind.Folder => "\uE8B7", TransferEntryKind.Video => "\uE714", TransferEntryKind.Photo => "\uE91B", _ => "\uE7C3" };
    public string SizeText => IsFolder ? string.Empty : FormatSize(Entry.Size);
    public string DateText => Entry.ModifiedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
    public string TypeText => IsFolder ? "Pasta" : Entry.Extension.Length > 1 ? Entry.Extension[1..].ToUpperInvariant() : "Arquivo";

    // ---------- cor (arquivo: etiqueta do catálogo; pasta: cor de pasta) ----------
    public PhotoColor ColorLabel => _color;
    public bool HasColor => _color != PhotoColor.None;
    public bool IsCataloged => _isCataloged;
    public Brush ColorBrush => ColorLookup.BrushFor(_color);
    public string ColorToolTip => HasColor ? $"Cor: {PhotoColors.Name(_color)}" : IsMedia && !IsCataloged ? "Fora do catálogo (marcar uma cor o adiciona)" : string.Empty;

    /// <summary>Arquivo: <paramref name="color"/> nulo = fora do catálogo. Pasta: nulo = sem cor.</summary>
    public void SetCatalogState(PhotoColor? color)
    {
        var cataloged = !IsFolder && color.HasValue;
        if (_isCataloged == cataloged && _color == (color ?? PhotoColor.None)) return;
        _isCataloged = cataloged;
        _color = color ?? PhotoColor.None;
        OnPropertyChanged(nameof(ColorLabel)); OnPropertyChanged(nameof(HasColor)); OnPropertyChanged(nameof(IsCataloged)); OnPropertyChanged(nameof(ColorBrush)); OnPropertyChanged(nameof(ColorToolTip));
    }

    // ---------- renomear no lugar ----------
    public bool IsEditing { get => _isEditing; set { if (_isEditing == value) return; _isEditing = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotEditing)); } }
    public bool IsNotEditing => !IsEditing;
    public string EditText { get => _editText; set { _editText = value ?? string.Empty; OnPropertyChanged(); } }
    public string Extension => IsFolder ? string.Empty : System.IO.Path.GetExtension(Name);

    // ---------- comparação com o outro painel ----------
    public CompareState CompareState { get => _compare; set { if (_compare == value) return; _compare = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCompareBadge)); OnPropertyChanged(nameof(CompareText)); OnPropertyChanged(nameof(CompareToolTip)); } }
    public bool HasCompareBadge => _compare is CompareState.OnlyHere or CompareState.Different or CompareState.Same;
    public string CompareText => _compare switch { CompareState.OnlyHere => "Só aqui", CompareState.Different => "Diferente", CompareState.Same => "Igual", _ => string.Empty };
    public string CompareToolTip => _compare switch
    {
        CompareState.OnlyHere => "Não existe no outro painel",
        CompareState.Different => "Existe no outro painel com tamanho ou data diferentes",
        CompareState.Same => "Igual no outro painel (mesmo nome, tamanho e data)",
        _ => string.Empty
    };

    // ---------- duplicata no catálogo ----------
    public IReadOnlyList<string> Duplicates { get => _duplicates; set { _duplicates = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsDuplicate)); OnPropertyChanged(nameof(DuplicateToolTip)); } }
    public bool IsDuplicate => _duplicates.Count > 0;
    public string DuplicateToolTip => IsDuplicate ? "Conteúdo idêntico já está no catálogo em:\n" + string.Join("\n", _duplicates.Take(5)) + (_duplicates.Count > 5 ? $"\n… e mais {_duplicates.Count - 5}" : string.Empty) : string.Empty;

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };
}

/// <summary>
/// Um lado do módulo Transferência: pasta atual, histórico, busca, filtros, ordenação, modo de exibição e seleção — tudo independente do outro lado —
/// e a organização física (pastas, renomear, Lixeira, cores, área de transferência, organizar por data). Copiar/mover entre os lados e desfazer ficam
/// no <see cref="ITransferPaneHost"/> (o <see cref="TransferViewModel"/>).
/// </summary>
public sealed class TransferPaneViewModel : ViewModelBase
{
    public static IReadOnlyList<TransferSortOption> SortOptions { get; } =
    [
        new(TransferSortField.Name, "Nome"), new(TransferSortField.Date, "Data"), new(TransferSortField.Size, "Tamanho"), new(TransferSortField.Type, "Tipo")
    ];
    public const string AllTypes = "Todos os tipos", PhotosOnly = "Fotos", VideosOnly = "Vídeos", MediaOnly = "Fotos e vídeos", FoldersOnly = "Pastas", OthersOnly = "Outros arquivos", DuplicatesOnly = "Duplicados no catálogo";
    public static IReadOnlyList<string> TypeFilterChoices { get; } = [AllTypes, PhotosOnly, VideosOnly, MediaOnly, FoldersOnly, OthersOnly, DuplicatesOnly];
    public static IReadOnlyList<string> ColorFilterChoices => PhotoColors.FilterChoices;

    private readonly IFileThumbnailService? _thumbnails;
    private readonly Func<string?> _chooseFolder;
    private readonly Func<string, CancellationToken, FolderListResult> _lister;
    private readonly ITransferOrganizer? _organizer;
    private readonly ITransferDialogs _dialogs;
    private readonly NavigationHistory _history = new();
    private readonly Dictionary<string, TransferItemViewModel> _known = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<TransferEntry> _entries = [];
    private IReadOnlyList<TransferItemViewModel> _items = [];
    private IReadOnlyList<TransferItemViewModel> _selected = [];
    private IReadOnlyDictionary<string, PhotoColor> _colors = new Dictionary<string, PhotoColor>();
    private IReadOnlyDictionary<string, PhotoColor> _folderColors = new Dictionary<string, PhotoColor>();
    private IReadOnlyDictionary<string, CompareState>? _compare;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _duplicates = new Dictionary<string, IReadOnlyList<string>>();
    private CancellationTokenSource? _loadCts, _detailsCts;
    private string _currentPath = string.Empty, _pathText = string.Empty, _searchText = string.Empty, _errorText = string.Empty, _messageText = string.Empty;
    private string _typeFilter = AllTypes, _colorFilter = "Qualquer";
    private bool _isLoading, _isEditingPath, _descending, _foldersFirst = true, _isBusy, _showDetails, _onlyDifferences, _isActive;
    private TransferSortOption _sort = SortOptions[0];
    private TransferViewMode _viewMode = TransferViewMode.Grid;
    private double _thumbnailSize = 132;
    private int _thumbnailVersion;
    private string? _pendingNewFolder;
    private IReadOnlyList<DetailRow> _details = [];

    public TransferPaneViewModel(IFileThumbnailService? thumbnails = null, Func<string?>? chooseFolder = null, Func<string, CancellationToken, FolderListResult>? lister = null,
        ITransferOrganizer? organizer = null, ITransferDialogs? dialogs = null)
    {
        _thumbnails = thumbnails;
        _chooseFolder = chooseFolder ?? PickFolder;
        _lister = lister ?? ((path, token) => FolderLister.List(path, token));
        _organizer = organizer;
        _dialogs = dialogs ?? new WpfTransferDialogs();

        BackCommand = new RelayCommand(_ => GoBack(), _ => _history.CanGoBack);
        ForwardCommand = new RelayCommand(_ => GoForward(), _ => _history.CanGoForward);
        UpCommand = new RelayCommand(_ => GoUp(), _ => ParentPath is not null);
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => HasFolder);
        NavigateCommand = new RelayCommand(parameter => { if (PathOf(parameter) is { } path) _ = NavigateAsync(path); });
        OpenItemCommand = new RelayCommand(parameter => { if (parameter is TransferItemViewModel { IsFolder: true } folder) _ = NavigateAsync(folder.Path); });
        ChooseFolderCommand = new RelayCommand(_ => { if (_chooseFolder() is { Length: > 0 } chosen) _ = NavigateAsync(chosen); });
        BeginEditPathCommand = new RelayCommand(_ => { PathText = CurrentPath; IsEditingPath = true; });
        CommitPathCommand = new RelayCommand(_ => { IsEditingPath = false; if (!string.IsNullOrWhiteSpace(PathText)) _ = NavigateAsync(PathText.Trim().Trim('"')); });
        CancelEditPathCommand = new RelayCommand(_ => IsEditingPath = false);
        ToggleSortDirectionCommand = new RelayCommand(_ => SortDescending = !SortDescending);
        SetViewModeCommand = new RelayCommand(parameter => ViewMode = parameter is TransferViewMode mode ? mode : parameter is string text && Enum.TryParse<TransferViewMode>(text, out var parsed) ? parsed : ViewMode);
        SortByCommand = new RelayCommand(parameter =>
        {
            if (parameter is not TransferSortField field) return;
            if (_sort.Field == field) SortDescending = !SortDescending;
            else { SortOption = SortOptions.First(o => o.Field == field); SortDescending = false; }
        });
        ClearFiltersCommand = new RelayCommand(_ => { SearchText = string.Empty; TypeFilter = AllTypes; ColorFilter = "Qualquer"; }, _ => HasActiveFilter);

        NewFolderCommand = new RelayCommand(_ => _ = CreateFolderAsync(), _ => CanOrganize);
        RenameCommand = new RelayCommand(_ => _ = RenameSelectionAsync(), _ => CanOrganize && _selected.Count > 0 && (_selected.Count == 1 || _selected.Any(i => !i.IsFolder)));
        DeleteCommand = new RelayCommand(_ => _ = RecycleSelectedAsync(), _ => CanOrganize && _selected.Count > 0);
        AddToCatalogCommand = new RelayCommand(_ => _ = AddSelectedToCatalogAsync(), _ => CanOrganize && _selected.Any(i => i.IsMedia && !i.IsCataloged));
        ShowInExplorerCommand = new RelayCommand(_ => ShowInExplorer(), _ => HasFolder && !HasError);
        OrganizeByDateCommand = new RelayCommand(_ => _ = OrganizeByDateAsync(), _ => CanOrganize && Host is not null && _entries.Any(e => e.IsMedia));
        CopyCommand = new RelayCommand(_ => CopyToClipboard(cut: false), _ => Host is not null && _selected.Count > 0);
        CutCommand = new RelayCommand(_ => CopyToClipboard(cut: true), _ => Host is not null && CanOrganize && _selected.Count > 0);
        PasteCommand = new RelayCommand(_ => _ = PasteAsync(), _ => Host is not null && CanOrganize);
        UndoCommand = new RelayCommand(_ => _ = Host?.UndoAsync(), _ => Host is not null);
        PinCommand = new RelayCommand(_ => Host?.TogglePin(this), _ => Host is not null && HasFolder);
        ColorChoices = PhotoColors.All.Prepend(PhotoColor.None).Select(color => new ColorChoice(color, new RelayCommand(_ => _ = SetColorForSelectionAsync(color), _ => CanColor))).ToList();
    }

    public RelayCommand BackCommand { get; }
    public RelayCommand ForwardCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand OpenItemCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand BeginEditPathCommand { get; }
    public RelayCommand CommitPathCommand { get; }
    public RelayCommand CancelEditPathCommand { get; }
    public RelayCommand ToggleSortDirectionCommand { get; }
    public RelayCommand SetViewModeCommand { get; }
    public RelayCommand SortByCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand NewFolderCommand { get; }
    /// <summary>F2: um item → renomear no lugar; vários arquivos → renomear em lote com modelo.</summary>
    public RelayCommand RenameCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand AddToCatalogCommand { get; }
    public RelayCommand ShowInExplorerCommand { get; }
    public RelayCommand OrganizeByDateCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand CutCommand { get; }
    public RelayCommand PasteCommand { get; }
    public RelayCommand UndoCommand { get; }
    /// <summary>Fixa/desafixa a pasta atual na barra de Locais.</summary>
    public RelayCommand PinCommand { get; }
    /// <summary>Submenu/botão "Cor": as mesmas seis etiquetas da Biblioteca (arquivos) e cor de pasta (pastas). Atalho 1-6; 0 remove.</summary>
    public IReadOnlyList<ColorChoice> ColorChoices { get; }

    /// <summary>Outro painel precisa saber: pasta criada/renomeada/excluída aqui (ele atualiza ou segue a pasta renomeada).</summary>
    public event EventHandler<TransferFolderChange>? FolderChanged;
    /// <summary>A tela deve selecionar e mostrar este caminho (pasta recém-criada ou renomeada).</summary>
    public event EventHandler<string>? RevealRequested;
    /// <summary>Uma pasta foi aberta com sucesso (recentes, comparação, layout).</summary>
    public event EventHandler<string>? Navigated;
    /// <summary>Modo, ordenação, tamanho ou detalhes mudaram (o layout é salvo).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Coordenador dos dois painéis; sem ele (painel isolado) não há copiar/mover/colar/desfazer.</summary>
    public ITransferPaneHost? Host { get; set; }
    public IReadOnlyList<TransferEntry> Entries => _entries;

    public bool CanOrganize => _organizer is not null && HasFolder && !HasError && !IsBusy;
    public bool CanColor => _organizer is not null && !IsBusy && _selected.Any(i => i.IsMedia || i.IsFolder);
    public bool IsBusy { get => _isBusy; private set { if (_isBusy == value) return; _isBusy = value; OnPropertyChanged(); RaiseOrganizeState(); } }
    /// <summary>Resultado da última ação (criar, renomear, excluir, cor…) ou o motivo da falha.</summary>
    public string MessageText { get => _messageText; set { _messageText = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => MessageText.Length > 0;
    private bool _isPinned;
    public bool IsPinned { get => _isPinned; set { if (_isPinned == value) return; _isPinned = value; OnPropertyChanged(); OnPropertyChanged(nameof(PinGlyph)); OnPropertyChanged(nameof(PinToolTip)); } }
    public string PinGlyph => IsPinned ? "" : "";
    public string PinToolTip => IsPinned ? "Desafixar esta pasta dos Locais" : "Fixar esta pasta nos Locais";
    /// <summary>Painel com o foco: os "Locais" da barra lateral abrem aqui.</summary>
    public bool IsActive { get => _isActive; set { if (_isActive == value) return; _isActive = value; OnPropertyChanged(); } }

    public ObservableCollection<BreadcrumbPart> Breadcrumb { get; } = [];
    public string CurrentPath { get => _currentPath; private set { _currentPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFolder)); OnPropertyChanged(nameof(ParentPath)); } }
    public bool HasFolder => CurrentPath.Length > 0;
    public string? ParentPath => HasFolder ? NavigationHistory.Parent(CurrentPath) : null;
    public string PathText { get => _pathText; set { _pathText = value; OnPropertyChanged(); } }
    public bool IsEditingPath { get => _isEditingPath; set { if (_isEditingPath == value) return; _isEditingPath = value; OnPropertyChanged(); } }
    public IReadOnlyList<TransferItemViewModel> Items { get => _items; private set { _items = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EmptyText)); OnPropertyChanged(nameof(StatusText)); } }
    public bool IsLoading { get => _isLoading; private set { if (_isLoading == value) return; _isLoading = value; OnPropertyChanged(); } }
    public string ErrorText { get => _errorText; private set { _errorText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EmptyText)); } }
    public bool HasError => ErrorText.Length > 0;
    public bool IsEmpty => !IsLoading && Items.Count == 0;
    public string EmptyText => HasError ? ErrorText : !HasFolder ? "Escolha uma pasta para começar." : HasActiveFilter ? "Nada encontrado com esta busca/filtro." : "Esta pasta está vazia.";

    public string SearchText { get => _searchText; set { if (_searchText == value) return; _searchText = value; OnPropertyChanged(); ApplyView(); } }
    public string TypeFilter { get => _typeFilter; set { if (value is null || _typeFilter == value) return; _typeFilter = value; OnPropertyChanged(); ApplyView(); } }
    public string ColorFilter { get => _colorFilter; set { if (value is null || _colorFilter == value) return; _colorFilter = value; OnPropertyChanged(); ApplyView(); } }
    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(SearchText) || _typeFilter != AllTypes || _colorFilter != "Qualquer" || (_onlyDifferences && _compare is not null);
    /// <summary>Com a comparação ligada, esconde o que é igual nos dois lados.</summary>
    public bool ShowOnlyDifferences { get => _onlyDifferences; set { if (_onlyDifferences == value) return; _onlyDifferences = value; OnPropertyChanged(); ApplyView(); } }
    public TransferSortOption SortOption { get => _sort; set { if (_sort == value || value is null) return; _sort = value; OnPropertyChanged(); ApplyView(); LayoutChanged?.Invoke(this, EventArgs.Empty); } }
    public bool SortDescending { get => _descending; set { if (_descending == value) return; _descending = value; OnPropertyChanged(); OnPropertyChanged(nameof(SortArrow)); ApplyView(); LayoutChanged?.Invoke(this, EventArgs.Empty); } }
    public string SortArrow => SortDescending ? "\uE74B" : "\uE74A";
    public bool FoldersFirst { get => _foldersFirst; set { if (_foldersFirst == value) return; _foldersFirst = value; OnPropertyChanged(); ApplyView(); LayoutChanged?.Invoke(this, EventArgs.Empty); } }

    public TransferViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (_viewMode == value) return;
            _viewMode = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(IsGridMode)); OnPropertyChanged(nameof(IsListMode));
            SetSelection([]);
            if (value == TransferViewMode.Grid) _ = LoadThumbnailsAsync(++_thumbnailVersion);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public bool IsGridMode => ViewMode == TransferViewMode.Grid;
    public bool IsListMode => ViewMode == TransferViewMode.List;

    /// <summary>Lado da miniatura em pixels (pequeno/médio/grande por controle deslizante).</summary>
    public double ThumbnailSize
    {
        get => _thumbnailSize;
        set
        {
            value = Math.Clamp(Math.Round(value), 80, 260);
            if (Math.Abs(_thumbnailSize - value) < 0.5) return;
            _thumbnailSize = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(CardWidth)); OnPropertyChanged(nameof(CardHeight)); OnPropertyChanged(nameof(ImageHeight));
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    /// <summary>Largura da célula: miniatura + moldura do cartão (borda, respiro e margem do PhotoCardContainer).</summary>
    public double CardWidth => ThumbnailSize + 36;
    public double CardHeight => ThumbnailSize * 0.75 + 78;
    public double ImageHeight => ThumbnailSize * 0.75;

    // ---------- detalhes ----------
    public bool ShowDetails { get => _showDetails; set { if (_showDetails == value) return; _showDetails = value; OnPropertyChanged(); _ = LoadDetailsAsync(); LayoutChanged?.Invoke(this, EventArgs.Empty); } }
    public IReadOnlyList<DetailRow> Details { get => _details; private set { _details = value; OnPropertyChanged(); } }

    public IReadOnlyList<TransferItemViewModel> SelectedItems => _selected;
    public string SelectionText => _selected.Count == 0 ? string.Empty : $"{_selected.Count} selecionado(s) · {TransferItemViewModel.FormatSize(_selected.Where(i => !i.IsFolder).Sum(i => i.Entry.Size))}";
    public string StatusText
    {
        get
        {
            if (!HasFolder) return string.Empty;
            var folders = Items.Count(i => i.IsFolder);
            var files = Items.Count - folders;
            var text = $"{folders} pasta(s), {files} arquivo(s)";
            if (_entries.Count != Items.Count) text += $" (de {_entries.Count}; filtro ativo)";
            var duplicates = _duplicates.Count;
            if (duplicates > 0) text += $" · {duplicates} já no catálogo em outro lugar";
            return text;
        }
    }

    public void SetSelection(IEnumerable<TransferItemViewModel> items)
    {
        _selected = items.ToList();
        OnPropertyChanged(nameof(SelectedItems)); OnPropertyChanged(nameof(SelectionText));
        RaiseOrganizeState();
        if (_showDetails) _ = LoadDetailsAsync();
    }

    private void RaiseOrganizeState()
    {
        OnPropertyChanged(nameof(CanOrganize)); OnPropertyChanged(nameof(CanColor));
        foreach (var command in new[] { NewFolderCommand, RenameCommand, DeleteCommand, AddToCatalogCommand, ShowInExplorerCommand, OrganizeByDateCommand, CopyCommand, CutCommand, PasteCommand, UndoCommand, ClearFiltersCommand, PinCommand })
            command?.RaiseCanExecuteChanged();
        if (ColorChoices is not null) foreach (var choice in ColorChoices) choice.Command.RaiseCanExecuteChanged();
    }

    // ---------- layout ----------

    public TransferPaneLayout GetLayout() => new(HasFolder ? CurrentPath : null, ViewMode.ToString(), ThumbnailSize, _sort.Field.ToString(), SortDescending, FoldersFirst, ShowDetails);

    public void ApplyLayout(TransferPaneLayout layout)
    {
        if (Enum.TryParse<TransferViewMode>(layout.ViewMode, out var mode)) ViewMode = mode;
        ThumbnailSize = layout.ThumbnailSize;
        if (Enum.TryParse<TransferSortField>(layout.SortField, out var field)) SortOption = SortOptions.First(o => o.Field == field);
        SortDescending = layout.SortDescending;
        FoldersFirst = layout.FoldersFirst;
        ShowDetails = layout.ShowDetails;
    }

    // ---------- navegação ----------

    /// <summary>Abre uma pasta (registrando no histórico). Devolve quando a listagem e as cores terminaram (miniaturas e duplicatas continuam em segundo plano).</summary>
    public async Task NavigateAsync(string path, bool record = true)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var token = cts.Token;
        path = NormalizePath(path);
        IsLoading = true;
        FolderListResult result;
        try { result = await Task.Run(() => _lister(path, token), token); }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested) return;

        // Cores do catálogo junto com a listagem (uma consulta leve por pasta), para os itens já nascerem coloridos.
        var (colors, folderColors) = result.IsOk ? await ReadColorsAsync(path) : (null, null);
        if (token.IsCancellationRequested) return;
        _colors = colors ?? new Dictionary<string, PhotoColor>();
        _folderColors = folderColors ?? new Dictionary<string, PhotoColor>();

        IsLoading = false;
        if (!string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase)) MessageText = string.Empty;
        SetSelection([]);
        _known.Clear();
        _duplicates = new Dictionary<string, IReadOnlyList<string>>();
        _pendingNewFolder = null;
        CurrentPath = path;
        if (result.IsOk)
        {
            if (record) _history.Visit(path);
            ErrorText = string.Empty;
            _entries = result.Entries;
        }
        else
        {
            // Pasta offline/sem permissão: o painel mostra o motivo e mantém o caminho para o usuário tentar de novo (Atualizar).
            ErrorText = result.Error!;
            _entries = [];
        }
        RebuildBreadcrumb();
        ApplyView();
        RaiseNavigationState();
        RaiseOrganizeState();
        if (result.IsOk)
        {
            Navigated?.Invoke(this, path);
            _ = FindDuplicatesAsync(token);
        }
    }

    public Task RefreshAsync() => HasFolder ? NavigateAsync(CurrentPath, record: false) : Task.CompletedTask;

    private void GoBack() { if (_history.Back() is { } path) _ = NavigateAsync(path, record: false); }
    private void GoForward() { if (_history.Forward() is { } path) _ = NavigateAsync(path, record: false); }
    private void GoUp() { if (ParentPath is { } parent) _ = NavigateAsync(parent); }

    private void RaiseNavigationState()
    {
        BackCommand.RaiseCanExecuteChanged(); ForwardCommand.RaiseCanExecuteChanged(); UpCommand.RaiseCanExecuteChanged(); RefreshCommand.RaiseCanExecuteChanged();
    }

    private void RebuildBreadcrumb()
    {
        Breadcrumb.Clear();
        foreach (var part in Breadcrumbs.Split(CurrentPath)) Breadcrumb.Add(part);
    }

    private void ApplyView()
    {
        var selectedPaths = _selected.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wantedColor = PhotoColors.FromFilterChoice(_colorFilter);
        var listing = TransferListing.Apply(_entries, SearchText, _sort.Field, SortDescending, FoldersFirst)
            .Where(MatchesType)
            .Where(e => wantedColor is null || ColorOf(e) == wantedColor)
            .Where(e => !_onlyDifferences || _compare is null || !_compare.TryGetValue(e.Path, out var state) || state != CompareState.Same);
        Items = listing.Select(entry => _known.TryGetValue(entry.Path, out var item) ? item : _known[entry.Path] = NewItem(entry)).ToList();
        if (selectedPaths.Count > 0) SetSelection(Items.Where(i => selectedPaths.Contains(i.Path)));
        OnPropertyChanged(nameof(EmptyText)); OnPropertyChanged(nameof(HasActiveFilter));
        ClearFiltersCommand?.RaiseCanExecuteChanged();
        if (IsGridMode && _entries.Count > 0) _ = LoadThumbnailsAsync(++_thumbnailVersion);
    }

    private bool MatchesType(TransferEntry entry) => _typeFilter switch
    {
        PhotosOnly => entry.Kind == TransferEntryKind.Photo,
        VideosOnly => entry.Kind == TransferEntryKind.Video,
        MediaOnly => entry.IsMedia,
        FoldersOnly => entry.IsFolder,
        OthersOnly => entry.Kind == TransferEntryKind.Other,
        DuplicatesOnly => _duplicates.ContainsKey(entry.Path),
        _ => true
    };

    private PhotoColor ColorOf(TransferEntry entry) =>
        (entry.IsFolder ? _folderColors : _colors).TryGetValue(entry.Path, out var color) ? color : PhotoColor.None;

    private TransferItemViewModel NewItem(TransferEntry entry)
    {
        var item = new TransferItemViewModel(entry);
        if (entry.IsFolder) item.SetCatalogState(_folderColors.TryGetValue(entry.Path, out var folderColor) ? folderColor : null);
        else item.SetCatalogState(_colors.TryGetValue(entry.Path, out var color) ? color : null);
        if (_compare is not null && _compare.TryGetValue(entry.Path, out var state)) item.CompareState = state;
        if (_duplicates.TryGetValue(entry.Path, out var others)) item.Duplicates = others;
        return item;
    }

    /// <summary>Miniaturas em segundo plano, uma por vez, só no modo grade; recomeça (e cancela a anterior) a cada navegação/busca.</summary>
    private async Task LoadThumbnailsAsync(int version)
    {
        if (_thumbnails is null || !IsGridMode) return;
        var token = _loadCts?.Token ?? CancellationToken.None;
        foreach (var item in Items.Where(i => i.IsMedia && i.ThumbnailUri is null).ToList())
        {
            if (version != _thumbnailVersion || token.IsCancellationRequested) return;
            try
            {
                var file = await _thumbnails.GetOrCreateForFileAsync(item.Path, token);
                if (file is not null && version == _thumbnailVersion) item.ThumbnailUri = new Uri(file);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* uma miniatura com falha não interrompe as demais */ }
        }
    }

    // ---------- cores, comparação, duplicatas, detalhes ----------

    private async Task<(IReadOnlyDictionary<string, PhotoColor>?, IReadOnlyDictionary<string, PhotoColor>?)> ReadColorsAsync(string folder)
    {
        if (_organizer is null) return (null, null);
        try { return (await _organizer.GetColorsAsync(folder), await _organizer.GetFolderColorsAsync(folder)); }
        catch (Exception) { return (null, null); }                          // sem catálogo disponível o painel continua navegando, só sem cores
    }

    /// <summary>Relê as cores da pasta atual (ex.: ao voltar da Biblioteca, onde cores podem ter sido marcadas).</summary>
    public async Task RefreshColorsAsync()
    {
        if (!HasFolder || HasError) return;
        var (colors, folderColors) = await ReadColorsAsync(CurrentPath);
        if (colors is null || folderColors is null) return;
        _colors = colors;
        _folderColors = folderColors;
        foreach (var item in _known.Values)
            item.SetCatalogState(item.IsFolder ? (folderColors.TryGetValue(item.Path, out var f) ? f : null) : (colors.TryGetValue(item.Path, out var c) ? c : null));
        if (_colorFilter != "Qualquer") ApplyView();
        AddToCatalogCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Estado de comparação vindo do coordenador (nulo = comparação desligada).</summary>
    public void ApplyCompare(IReadOnlyDictionary<string, CompareState>? states)
    {
        _compare = states;
        foreach (var item in _known.Values) item.CompareState = states is not null && states.TryGetValue(item.Path, out var state) ? state : CompareState.None;
        if (_onlyDifferences) ApplyView();
        OnPropertyChanged(nameof(HasActiveFilter));
    }

    private async Task FindDuplicatesAsync(CancellationToken token)
    {
        if (_organizer is null) return;
        var files = _entries.Where(e => e.IsMedia).Select(e => e.Path).ToList();
        if (files.Count == 0) return;
        try
        {
            var found = await Task.Run(() => _organizer.FindDuplicatesAsync(files, token), token);
            if (token.IsCancellationRequested) return;
            _duplicates = found;
            foreach (var item in _known.Values) item.Duplicates = found.TryGetValue(item.Path, out var others) ? others : [];
            if (_typeFilter == DuplicatesOnly) ApplyView();
            OnPropertyChanged(nameof(StatusText));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* duplicata é informação extra; falhar aqui não atrapalha a navegação */ }
    }

    private async Task LoadDetailsAsync()
    {
        _detailsCts?.Cancel();
        if (!_showDetails) { Details = []; return; }
        var cts = _detailsCts = new CancellationTokenSource();
        if (_selected.Count > 1)
        {
            var files = _selected.Where(i => !i.IsFolder).ToList();
            Details =
            [
                new("Seleção", $"{_selected.Count} itens ({_selected.Count - files.Count} pasta(s), {files.Count} arquivo(s))"),
                new("Tamanho", TransferItemViewModel.FormatSize(files.Sum(f => f.Entry.Size))),
                new("Fotos / vídeos", $"{files.Count(f => f.Entry.Kind == TransferEntryKind.Photo)} / {files.Count(f => f.IsVideo)}"),
                new("No catálogo", $"{files.Count(f => f.IsCataloged)} de {files.Count(f => f.IsMedia)}")
            ];
            return;
        }
        var target = _selected.Count == 1 ? _selected[0].Path : HasFolder && !HasError ? CurrentPath : null;
        if (target is null || _organizer is null) { Details = []; return; }
        try
        {
            await Task.Delay(120, cts.Token);                              // setas do teclado: só lê o item em que o usuário parou
            var details = await _organizer.GetDetailsAsync(target, cts.Token);
            if (cts.IsCancellationRequested) return;
            var rows = details.Rows.Select(r => new DetailRow(r.Label, r.Value)).ToList();
            if (_selected.Count == 1 && _selected[0].IsDuplicate) rows.Add(new("Duplicata", string.Join("\n", _selected[0].Duplicates.Take(3))));
            if (_selected.Count == 1 && _selected[0].HasCompareBadge) rows.Add(new("Comparação", _selected[0].CompareToolTip));
            Details = rows;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Details = [new("Detalhes", $"Não foi possível ler: {ex.Message}")]; }
    }

    // ---------- organização: pastas, renomear, Lixeira, cores ----------

    /// <summary>Como no Explorer: cria "Nova pasta" (ou "Nova pasta (2)"…) na hora e já abre o nome para edição.</summary>
    public async Task CreateFolderAsync()
    {
        if (!CanOrganize) return;
        var parent = CurrentPath;
        var name = TransferNames.Unique(_entries.Select(e => e.Name));
        await RunAsync(async () =>
        {
            var created = await _organizer!.CreateFolderAsync(parent, name);
            await RefreshAsync();
            FolderChanged?.Invoke(this, new TransferFolderChange(parent));
            _pendingNewFolder = created;
            if (_known.TryGetValue(created, out var item))
            {
                RevealRequested?.Invoke(this, created);
                BeginInlineRename(item);
            }
            return string.Empty;
        }, "criar a pasta");
    }

    private async Task RenameSelectionAsync()
    {
        if (_selected.Count == 1) BeginInlineRename(_selected[0]);
        else await BatchRenameAsync();
    }

    /// <summary>Abre o nome do item para edição no próprio cartão/linha (arquivo: só o nome, a extensão é mantida).</summary>
    public void BeginInlineRename(TransferItemViewModel item)
    {
        if (_organizer is null || !HasFolder || HasError) return;
        foreach (var other in _known.Values.Where(i => i.IsEditing && i != item)) other.IsEditing = false;
        item.EditText = item.IsFolder ? item.Name : System.IO.Path.GetFileNameWithoutExtension(item.Name);
        item.IsEditing = true;
    }

    /// <summary>Esc: desiste do nome novo. Uma pasta recém-criada continua existindo (com o nome sugerido).</summary>
    public void CancelInlineRename(TransferItemViewModel item)
    {
        if (!item.IsEditing) return;
        item.IsEditing = false;
        if (string.Equals(_pendingNewFolder, item.Path, StringComparison.OrdinalIgnoreCase)) { Host?.PushUndo(new($"criar a pasta “{item.Name}”", [], [], [item.Path])); _pendingNewFolder = null; }
    }

    /// <summary>Enter/clique fora: confirma o nome digitado. O catálogo acompanha; Ctrl+Z desfaz.</summary>
    public async Task CommitInlineRenameAsync(TransferItemViewModel item)
    {
        if (!item.IsEditing) return;
        item.IsEditing = false;
        var isNewFolder = string.Equals(_pendingNewFolder, item.Path, StringComparison.OrdinalIgnoreCase);
        _pendingNewFolder = null;
        var finalName = item.IsFolder ? item.EditText.Trim() : TransferNames.WithOriginalExtension(item.EditText, item.Name);
        if (string.Equals(finalName, item.Name, StringComparison.Ordinal))
        {
            if (isNewFolder) { Host?.PushUndo(new($"criar a pasta “{item.Name}”", [], [], [item.Path])); MessageText = $"Pasta “{item.Name}” criada."; }
            return;
        }
        var siblings = _entries.Select(e => e.Name).Where(n => !string.Equals(n, item.Name, StringComparison.OrdinalIgnoreCase));
        if (TransferNames.Validate(finalName, siblings, item.Name) is { } error)
        {
            MessageText = $"Nome não aceito: {error}";
            if (isNewFolder) Host?.PushUndo(new($"criar a pasta “{item.Name}”", [], [], [item.Path]));
            return;
        }
        var parent = CurrentPath;
        await RunAsync(async () =>
        {
            var result = await _organizer!.RenameAsync(item.Path, finalName);
            Host?.PushUndo(isNewFolder ? new($"criar a pasta “{finalName}”", [], [], [result.NewPath]) : TransferUndoEntry.Rename(item.Path, result.NewPath));
            await RefreshAsync();
            RevealRequested?.Invoke(this, result.NewPath);
            FolderChanged?.Invoke(this, new TransferFolderChange(parent, item.Path, result.NewPath));
            return isNewFolder ? $"Pasta “{finalName}” criada."
                : $"Renomeado para “{finalName}”." + (result.CatalogItemsUpdated > 0 ? $" {result.CatalogItemsUpdated} item(ns) do catálogo atualizado(s)." : string.Empty);
        }, "renomear");
    }

    /// <summary>Vários arquivos: modelo com {data}, {seq}… e pré-visualização. Pastas da seleção ficam de fora.</summary>
    public async Task BatchRenameAsync()
    {
        if (!CanOrganize) return;
        var files = _selected.Where(i => !i.IsFolder).ToList();
        if (files.Count == 0) return;
        var dated = await ReadDatesAsync(files.Select(f => f.Path).ToList());
        if (dated is null) return;
        var targets = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var viewModel = new BatchRenameViewModel(dated.Select(d => (d.Path, d.Date.Value)).ToList(), _entries.Where(e => !targets.Contains(e.Path)).Select(e => e.Name));
        if (!_dialogs.BatchRename(viewModel)) return;
        var renames = viewModel.Renames.Where(r => !string.Equals(r.From, r.To, StringComparison.Ordinal)).ToList();
        await RunAsync(async () =>
        {
            var failed = await _organizer!.MovePathsAsync(renames);
            var done = renames.Where(r => !failed.Any(f => string.Equals(f.Path, r.From, StringComparison.OrdinalIgnoreCase))).ToList();
            if (done.Count > 0) Host?.PushUndo(new($"renomear {done.Count} arquivo(s)", done, [], []));
            await RefreshAsync();
            FolderChanged?.Invoke(this, new TransferFolderChange(CurrentPath));
            return $"{done.Count} arquivo(s) renomeado(s)." + (failed.Count > 0 ? $" {failed.Count} falharam: {failed[0].Error}" : string.Empty);
        }, "renomear em lote");
    }

    /// <summary>Envia a seleção para a Lixeira (pastas com todo o conteúdo), depois de confirmar.</summary>
    public async Task RecycleSelectedAsync()
    {
        if (!CanOrganize || _selected.Count == 0) return;
        var targets = _selected.ToList();
        var folders = targets.Count(i => i.IsFolder);
        var question = targets.Count == 1
            ? $"Enviar “{targets[0].Name}” para a Lixeira?" + (folders == 1 ? "\n\nA pasta vai com todo o conteúdo (subpastas incluídas)." : string.Empty)
            : $"Enviar {targets.Count} itens para a Lixeira?" + (folders > 0 ? $"\n\n{folders} pasta(s) vão com todo o conteúdo (subpastas incluídas)." : string.Empty);
        if (!_dialogs.Confirm(question + "\n\nItens catalogados ficam marcados como ausentes na Biblioteca. Para voltar atrás, restaure pela Lixeira do Windows (Ctrl+Z não desfaz exclusões).")) return;
        var parent = CurrentPath;
        await RunAsync(async () =>
        {
            var result = await _organizer!.RecycleAsync(targets.Select(i => i.Path).ToList());
            await RefreshAsync();
            foreach (var path in result.Recycled) FolderChanged?.Invoke(this, new TransferFolderChange(parent, path));
            var text = $"{result.Recycled.Count} item(ns) enviado(s) para a Lixeira.";
            return result.Failed.Count == 0 ? text : $"{text} {result.Failed.Count} não puderam ser excluído(s): {result.Failed[0].Error}";
        }, "excluir");
    }

    /// <summary>Cor da seleção: fotos/vídeos recebem a etiqueta do catálogo (os de fora são adicionados, se o usuário aceitar); pastas recebem cor de pasta.</summary>
    public async Task SetColorForSelectionAsync(PhotoColor color)
    {
        if (_organizer is null || IsBusy) return;
        var folders = _selected.Where(i => i.IsFolder).ToList();
        var media = _selected.Where(i => i.IsMedia).ToList();
        if (folders.Count == 0 && media.Count == 0)
        {
            MessageText = _selected.Count > 0 ? "A cor vale para fotos, vídeos e pastas; outros arquivos não recebem cor." : "Selecione fotos, vídeos ou pastas para marcar a cor.";
            return;
        }
        var outside = media.Count(i => !i.IsCataloged);
        var addMissing = false;
        if (color != PhotoColor.None && outside > 0)
        {
            addMissing = _dialogs.Confirm($"{outside} arquivo(s) ainda não estão no catálogo.\n\nPara guardar a cor, eles serão adicionados ao catálogo no lugar onde estão (nada é copiado nem movido). Adicionar?");
            if (!addMissing && outside == media.Count && folders.Count == 0) return;
        }
        await RunAsync(async () =>
        {
            var parts = new List<string>();
            if (folders.Count > 0)
            {
                await _organizer.SetFolderColorAsync(folders.Select(f => f.Path).ToList(), color);
                parts.Add($"{folders.Count} pasta(s)");
            }
            var added = 0;
            if (media.Count > 0)
            {
                var result = await _organizer.SetColorAsync(media.Select(i => i.Path).ToList(), color, addMissing);
                added = result.Added;
                if (result.Colored > 0) parts.Add($"{result.Colored} arquivo(s)");
            }
            await RefreshColorsAsync();
            var what = parts.Count == 0 ? "nenhum item" : string.Join(" e ", parts);
            var text = color == PhotoColor.None ? $"Cor removida de {what}." : $"{char.ToUpper(what[0])}{what[1..]} marcado(s) como {PhotoColors.Name(color).ToLowerInvariant()}.";
            if (added > 0) text += $" {added} adicionado(s) ao catálogo.";
            return text;
        }, "marcar a cor");
    }

    /// <summary>Cataloga as fotos/vídeos selecionados no lugar (sem cor), para usar marcações e aparecer na Biblioteca.</summary>
    public async Task AddSelectedToCatalogAsync()
    {
        if (!CanOrganize) return;
        var targets = _selected.Where(i => i.IsMedia && !i.IsCataloged).Select(i => i.Path).ToList();
        if (targets.Count == 0) return;
        await RunAsync(async () =>
        {
            // Marcar "sem cor" com addMissing só cataloga: é o mesmo caminho da marcação, sem um segundo fluxo de importação.
            var result = await _organizer!.SetColorAsync(targets, PhotoColor.None, addMissing: true);
            await RefreshColorsAsync();
            return $"{result.Added} item(ns) adicionado(s) ao catálogo.";
        }, "adicionar ao catálogo");
    }

    // ---------- organizar por data ----------

    /// <summary>Fotos/vídeos selecionados (ou todos os da pasta) vão para pastas Ano\Mês… nesta pasta ou na do outro painel.</summary>
    public async Task OrganizeByDateAsync()
    {
        if (!CanOrganize || Host is null) return;
        var chosen = _selected.Where(i => i.IsMedia).Select(i => i.Path).ToList();
        var files = chosen.Count > 0 ? chosen : _entries.Where(e => e.IsMedia).Select(e => e.Path).ToList();
        if (files.Count == 0) { MessageText = "Não há fotos nem vídeos nesta pasta."; return; }
        var dated = await ReadDatesAsync(files);
        if (dated is null) return;
        var viewModel = new OrganizeByDateViewModel(dated, CurrentPath, Host.OtherPanePath(this));
        if (!_dialogs.OrganizeByDate(viewModel)) return;
        var jobs = viewModel.Plan.Select(p => new TransferJob(p.Files, p.Folder)).ToList();
        var result = await Host.RunTransferAsync(jobs, viewModel.Move, $"organizar {files.Count} arquivo(s) por data", createMissingDestinations: true);
        if (result is not null) MessageText = $"{result.Files} arquivo(s) {(viewModel.Move ? "movido(s)" : "copiado(s)")} para {jobs.Count} pasta(s) de data.";
    }

    private async Task<IReadOnlyList<(string Path, CaptureDate Date)>?> ReadDatesAsync(IReadOnlyList<string> files)
    {
        if (_organizer is null) return null;
        var result = new List<(string, CaptureDate)>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (i % 25 == 0) MessageText = $"Lendo datas… {i} de {files.Count}";
                result.Add((files[i], await _organizer.GetCaptureDateAsync(files[i])));
            }
            MessageText = string.Empty;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageText = $"Não foi possível ler as datas: {ex.Message}"; return null; }
        finally { IsBusy = false; }
    }

    // ---------- área de transferência e arrastar ----------

    /// <summary>Caminhos que saem deste painel ao copiar/arrastar (com o .xmp de cada mídia, como o Explorer espera).</summary>
    public IReadOnlyList<string> SelectedPathsWithSidecars()
    {
        var paths = new List<string>();
        foreach (var item in _selected)
        {
            paths.Add(item.Path);
            if (!item.IsFolder && XmpSidecar.UsesSidecar(item.Path) && File.Exists(XmpSidecar.PathFor(item.Path))) paths.Add(XmpSidecar.PathFor(item.Path));
        }
        return paths;
    }

    private void CopyToClipboard(bool cut)
    {
        if (Host is null || _selected.Count == 0) return;
        Host.Clipboard.SetFiles(SelectedPathsWithSidecars(), cut);
        MessageText = $"{_selected.Count} item(ns) {(cut ? "recortado(s)" : "copiado(s)")}. Ctrl+V cola no outro painel ou no Explorer.";
    }

    /// <summary>Ctrl+V: cola aqui o que foi copiado/recortado neste app ou no Explorer. Colar na mesma pasta cria cópias numeradas.</summary>
    public async Task PasteAsync()
    {
        if (Host is null || !CanOrganize) return;
        if (Host.Clipboard.GetFiles() is not { } clip) { MessageText = "Não há arquivos copiados para colar."; return; }
        var result = await Host.RunTransferAsync([new TransferJob(clip.Paths, CurrentPath)], clip.Cut, $"colar {clip.Paths.Count} item(ns)");
        if (result is not null && clip.Cut && !result.Cancelled) Host.Clipboard.Clear();
    }

    /// <summary>Itens soltos aqui (de outro painel, da Biblioteca ou do Explorer): vão para a pasta sob o cursor ou para a pasta atual.</summary>
    public Task DropAsync(IReadOnlyList<string> paths, string? targetFolder, bool move) =>
        Host is null || !CanOrganize ? Task.CompletedTask
            : Host.RunTransferAsync([new TransferJob(paths, targetFolder ?? CurrentPath)], move, $"{(move ? "mover" : "copiar")} {paths.Count} item(ns)");

    // ---------- apoio ----------

    private async Task RunAsync(Func<Task<string>> action, string what)
    {
        IsBusy = true;
        try { var message = await action(); if (message.Length > 0) MessageText = message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { MessageText = $"Não foi possível {what}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void ShowInExplorer()
    {
        var target = _selected.Count == 1 ? _selected[0].Path : CurrentPath;
        try
        {
            var arguments = _selected.Count == 1 ? $"/select,\"{target}\"" : $"\"{target}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageText = $"Não foi possível abrir o Explorer: {ex.Message}"; }
    }

    private static string? PathOf(object? parameter) => parameter switch
    {
        BreadcrumbPart part => part.Path,
        TransferItemViewModel item => item.Path,
        string text when !string.IsNullOrWhiteSpace(text) => text,
        _ => null
    };

    private static string NormalizePath(string path)
    {
        path = path.Trim();
        if (path.Length == 2 && path[1] == ':') path += "\\";                       // "D:" → "D:\"
        return path.Length > 3 ? path.TrimEnd('\\', '/') : path;
    }

    private static string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha a pasta" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
