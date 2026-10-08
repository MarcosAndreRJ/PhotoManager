using System.IO;
using PhotoManager.Application.Transfer;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

public sealed record PreviewRow(string Before, string After);

/// <summary>Renomear vários arquivos com um modelo ({data}_{seq}…), com pré-visualização e conferência de nomes repetidos antes de mexer no disco.</summary>
public sealed class BatchRenameViewModel : ViewModelBase
{
    private readonly IReadOnlyList<(string Path, DateTime Date)> _files;
    private readonly IReadOnlyList<string> _otherNames;
    private string _template = "{data}_{seq}";
    private string _startText = "1";
    private string _error = string.Empty;

    public BatchRenameViewModel(IReadOnlyList<(string Path, DateTime Date)> files, IEnumerable<string> otherNamesInFolder)
    {
        _files = files;
        _otherNames = otherNamesInFolder.ToList();
        Recalculate();
    }

    public string Title => $"Renomear {_files.Count} arquivos";
    public string Help => BatchRenamePlanner.Help;
    public string Template { get => _template; set { _template = value ?? string.Empty; OnPropertyChanged(); Recalculate(); } }
    public string StartText { get => _startText; set { _startText = value ?? string.Empty; OnPropertyChanged(); Recalculate(); } }
    public IReadOnlyList<PreviewRow> Preview { get; private set; } = [];
    public IReadOnlyList<(string From, string To)> Renames { get; private set; } = [];
    public string Error { get => _error; private set { _error = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsValid)); } }
    public bool IsValid => Error.Length == 0 && Renames.Count > 0;

    private void Recalculate()
    {
        if (!int.TryParse(_startText, out var start) || start < 0) { Renames = []; Preview = []; OnPropertyChanged(nameof(Preview)); Error = "O número inicial deve ser 0 ou mais."; return; }
        var (renames, error) = BatchRenamePlanner.Plan(_files, _template, start, _otherNames);
        Renames = renames;
        Preview = renames.Take(300).Select(r => new PreviewRow(Path.GetFileName(r.From), Path.GetFileName(r.To))).ToList();
        OnPropertyChanged(nameof(Preview));
        Error = error ?? string.Empty;
    }
}

public sealed record DatePreviewRow(string Folder, int Count);

/// <summary>Organizar por data: escolhe o modelo de pastas, o destino (esta pasta ou a do outro painel) e mover/copiar; mostra quantos arquivos vão para cada pasta.</summary>
public sealed class OrganizeByDateViewModel : ViewModelBase
{
    private readonly IReadOnlyList<(string Path, CaptureDate Date)> _files;
    private DateFolderPattern _pattern = DateFolderPlanner.Patterns[0];
    private bool _useOtherPane, _move = true;

    public OrganizeByDateViewModel(IReadOnlyList<(string Path, CaptureDate Date)> files, string currentFolder, string? otherPaneFolder)
    {
        _files = files;
        CurrentFolder = currentFolder;
        OtherPaneFolder = otherPaneFolder;
        Recalculate();
    }

    public string CurrentFolder { get; }
    public string? OtherPaneFolder { get; }
    public bool CanUseOtherPane => OtherPaneFolder is not null;
    public IReadOnlyList<DateFolderPattern> Patterns => DateFolderPlanner.Patterns;
    public DateFolderPattern Pattern { get => _pattern; set { if (value is null) return; _pattern = value; OnPropertyChanged(); Recalculate(); } }
    public bool UseOtherPane { get => _useOtherPane; set { _useOtherPane = value && CanUseOtherPane; OnPropertyChanged(); OnPropertyChanged(nameof(UseThisFolder)); Recalculate(); } }
    public bool UseThisFolder { get => !_useOtherPane; set { if (value) UseOtherPane = false; } }
    public bool Move { get => _move; set { _move = value; OnPropertyChanged(); OnPropertyChanged(nameof(Copy)); OnPropertyChanged(nameof(ConfirmText)); } }
    public bool Copy { get => !_move; set { if (value) Move = false; } }
    public string ConfirmText => Move ? "Mover" : "Copiar";
    public string Root => UseOtherPane ? OtherPaneFolder! : CurrentFolder;
    public IReadOnlyList<(string Folder, IReadOnlyList<string> Files)> Plan { get; private set; } = [];
    public IReadOnlyList<DatePreviewRow> Preview { get; private set; } = [];
    public string Summary { get; private set; } = string.Empty;

    private void Recalculate()
    {
        Plan = DateFolderPlanner.Plan(_files.Select(f => (f.Path, f.Date.Value)), Root, _pattern);
        Preview = Plan.Select(p => new DatePreviewRow(Path.GetRelativePath(Root, p.Folder), p.Files.Count)).ToList();
        var guessed = _files.Count(f => !f.Date.FromMetadata);
        Summary = $"{_files.Count} arquivo(s) em {Plan.Count} pasta(s) dentro de {Root}."
            + (guessed > 0 ? $" {guessed} sem data da foto: usam a data de modificação do arquivo." : string.Empty);
        OnPropertyChanged(nameof(Plan)); OnPropertyChanged(nameof(Preview)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(Root));
    }
}
