using System.IO;
using System.Text;
using System.Windows;
using PhotoManager.Application.Transfer;
using PhotoManager.Wpf.Commands;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

public enum FullCompareFilter
{
    All,
    OnlyDifferences,
    OnlySame,
    OnlyLeft,
    OnlyRight
}

public sealed class FullCompareItemViewModel
{
    public FullCompareItemViewModel(FullCompareItem item)
    {
        Item = item;
    }

    public FullCompareItem Item { get; }
    public string RelativePath => Item.RelativePath;
    public string Name => Item.Name;
    public bool IsFolder => Item.IsFolder;
    public FullCompareStatus Status => Item.Status;
    public string StatusText => Status switch
    {
        FullCompareStatus.Same => "Igual",
        FullCompareStatus.Different => "Diferente",
        FullCompareStatus.OnlyLeft => "Só à esquerda",
        FullCompareStatus.OnlyRight => "Só à direita",
        _ => string.Empty
    };

    public string Icon => IsFolder ? "" : "";

    public string LeftDetails => Item.ExistsLeft
        ? (IsFolder ? "Pasta presente" : $"{FormatBytes(Item.SizeLeft ?? 0)} · {Item.ModifiedUtcLeft?.ToLocalTime():yyyy-MM-dd HH:mm:ss}")
        : "—";

    public string RightDetails => Item.ExistsRight
        ? (IsFolder ? "Pasta presente" : $"{FormatBytes(Item.SizeRight ?? 0)} · {Item.ModifiedUtcRight?.ToLocalTime():yyyy-MM-dd HH:mm:ss}")
        : "—";

    public string Details => Item.Details;

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public sealed class FullCompareViewModel : ViewModelBase
{
    private readonly string _folderLeft;
    private readonly string _folderRight;
    private FullCompareResult? _result;
    private FullCompareFilter _filter = FullCompareFilter.All;
    private string _searchText = string.Empty;
    private bool _isLoading;
    private IReadOnlyList<FullCompareItemViewModel> _allItems = [];
    private IReadOnlyList<FullCompareItemViewModel> _filteredItems = [];

    public FullCompareViewModel(string folderLeft, string folderRight)
    {
        _folderLeft = folderLeft;
        _folderRight = folderRight;

        RefreshCommand = new RelayCommand(_ => _ = LoadAsync(), _ => !IsLoading);
        CopyListCommand = new RelayCommand(_ => CopyToClipboard(), _ => _filteredItems.Count > 0);
        SetFilterCommand = new RelayCommand(p =>
        {
            if (p is FullCompareFilter filter) Filter = filter;
            else if (p is string s && Enum.TryParse<FullCompareFilter>(s, out var parsed)) Filter = parsed;
        });

        _ = LoadAsync();
    }

    public string Title => $"Comparação completa — {Path.GetFileName(_folderLeft)} vs {Path.GetFileName(_folderRight)}";
    public string FolderLeft => _folderLeft;
    public string FolderRight => _folderRight;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            _isLoading = value;
            OnPropertyChanged();
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public FullCompareFilter Filter
    {
        get => _filter;
        set
        {
            if (_filter == value) return;
            _filter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFilterAll));
            OnPropertyChanged(nameof(IsFilterDiff));
            OnPropertyChanged(nameof(IsFilterSame));
            OnPropertyChanged(nameof(IsFilterOnlyLeft));
            OnPropertyChanged(nameof(IsFilterOnlyRight));
            ApplyFilter();
        }
    }

    public bool IsFilterAll { get => _filter == FullCompareFilter.All; set { if (value) Filter = FullCompareFilter.All; } }
    public bool IsFilterDiff { get => _filter == FullCompareFilter.OnlyDifferences; set { if (value) Filter = FullCompareFilter.OnlyDifferences; } }
    public bool IsFilterSame { get => _filter == FullCompareFilter.OnlySame; set { if (value) Filter = FullCompareFilter.OnlySame; } }
    public bool IsFilterOnlyLeft { get => _filter == FullCompareFilter.OnlyLeft; set { if (value) Filter = FullCompareFilter.OnlyLeft; } }
    public bool IsFilterOnlyRight { get => _filter == FullCompareFilter.OnlyRight; set { if (value) Filter = FullCompareFilter.OnlyRight; } }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public int TotalCount => _allItems.Count;
    public int SameCount => _result?.Summary.SameCount ?? 0;
    public int DifferentCount => _result?.Summary.DifferentCount ?? 0;
    public int OnlyLeftCount => _result?.Summary.OnlyLeftCount ?? 0;
    public int OnlyRightCount => _result?.Summary.OnlyRightCount ?? 0;

    public string SummaryText => _result is null
        ? "Comparando itens…"
        : $"Total: {TotalCount} itens · Iguais: {SameCount} · Diferentes: {DifferentCount} · Só à esquerda: {OnlyLeftCount} · Só à direita: {OnlyRightCount}";

    public IReadOnlyList<FullCompareItemViewModel> FilteredItems
    {
        get => _filteredItems;
        private set
        {
            _filteredItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilteredCountText));
            CopyListCommand.RaiseCanExecuteChanged();
        }
    }

    public string FilteredCountText => $"Exibindo {_filteredItems.Count} de {TotalCount} itens";

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CopyListCommand { get; }
    public RelayCommand SetFilterCommand { get; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var res = await Task.Run(() => FullFolderComparer.Compare(_folderLeft, _folderRight));
            _result = res;
            _allItems = res.Items.Select(i => new FullCompareItemViewModel(i)).ToList();

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(SameCount));
            OnPropertyChanged(nameof(DifferentCount));
            OnPropertyChanged(nameof(OnlyLeftCount));
            OnPropertyChanged(nameof(OnlyRightCount));
            OnPropertyChanged(nameof(SummaryText));

            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var query = _allItems.AsEnumerable();

        query = _filter switch
        {
            FullCompareFilter.OnlyDifferences => query.Where(i => i.Status != FullCompareStatus.Same),
            FullCompareFilter.OnlySame => query.Where(i => i.Status == FullCompareStatus.Same),
            FullCompareFilter.OnlyLeft => query.Where(i => i.Status == FullCompareStatus.OnlyLeft),
            FullCompareFilter.OnlyRight => query.Where(i => i.Status == FullCompareStatus.OnlyRight),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var terms = _searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            query = query.Where(i => terms.All(t =>
                i.RelativePath.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                i.Details.Contains(t, StringComparison.OrdinalIgnoreCase)));
        }

        FilteredItems = query.ToList();
    }

    private void CopyToClipboard()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Comparação completa: {_folderLeft} vs {_folderRight}");
        sb.AppendLine(SummaryText);
        sb.AppendLine(new string('-', 80));
        sb.AppendLine("Status\tTipo\tCaminho Relativo\tDetalhes\tEsquerda\tDireita");

        foreach (var item in _filteredItems)
        {
            var tipo = item.IsFolder ? "Pasta" : "Arquivo";
            sb.AppendLine($"{item.StatusText}\t{tipo}\t{item.RelativePath}\t{item.Details}\t{item.LeftDetails}\t{item.RightDetails}");
        }

        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch
        {
            // Área de transferência pode estar travada por outro processo
        }
    }
}
