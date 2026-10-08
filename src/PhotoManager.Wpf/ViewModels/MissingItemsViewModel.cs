using System.Collections.ObjectModel;
using PhotoManager.Application.Catalog;
using PhotoManager.Wpf.Commands;

namespace PhotoManager.Wpf.ViewModels;

public sealed class MissingEntryViewModel(MissingEntry entry, RelayCommand removeCommand) : ViewModelBase
{
    public MissingEntry Entry { get; } = entry;
    public string Title => Entry.Title;
    public string Detail => Entry.Detail;
    public bool FolderMissing => !Entry.FolderExists;
    public RelayCommand RemoveCommand { get; } = removeCommand;
}

/// <summary>
/// Lista o que o "Atualizar" não encontrou (pastas e arquivos) e deixa remover do catálogo item a item ou tudo.
/// Remover apaga só as linhas do catálogo (e tags/coleções/notas/histórico delas); nenhum arquivo é tocado, pois já não existem.
/// </summary>
public sealed class MissingItemsViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<long>, Task<int>> _remove;
    private readonly Func<int, bool> _confirmAll;
    private bool _isBusy;
    private string _status = string.Empty;

    public MissingItemsViewModel(IReadOnlyList<MissingEntry> entries, Func<IReadOnlyList<long>, Task<int>> remove, Func<int, bool> confirmAll)
    {
        _remove = remove;
        _confirmAll = confirmAll;
        foreach (var entry in entries)
        {
            MissingEntryViewModel? row = null;
            row = new MissingEntryViewModel(entry, new RelayCommand(_ => _ = RemoveAsync(row!), _ => !IsBusy));
            Entries.Add(row);
        }
        RemoveAllCommand = new RelayCommand(_ => _ = RemoveAllAsync(), _ => !IsBusy && Entries.Count > 0);
    }

    public ObservableCollection<MissingEntryViewModel> Entries { get; } = [];
    public RelayCommand RemoveAllCommand { get; }
    public bool HasEntries => Entries.Count > 0;
    public int TotalFiles => Entries.Sum(e => e.Entry.FileCount);
    public string SummaryText => Entries.Count == 0 ? "Nada mais a remover." : $"{Entries.Count} item(ns) não encontrado(s), {TotalFiles} arquivo(s) no catálogo.";
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; OnPropertyChanged(); Refresh(); } }
    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }

    public async Task RemoveAsync(MissingEntryViewModel row)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var removed = await _remove(row.Entry.PhotoIds);
            Entries.Remove(row);
            Status = $"{removed} arquivo(s) removido(s) do catálogo.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Status = $"Não foi possível remover: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    public async Task RemoveAllAsync()
    {
        if (IsBusy || Entries.Count == 0) return;
        var total = TotalFiles;
        if (!_confirmAll(total)) return;
        IsBusy = true;
        try
        {
            var removed = await _remove(Entries.SelectMany(e => e.Entry.PhotoIds).Distinct().ToList());
            Entries.Clear();
            Status = $"{removed} arquivo(s) removido(s) do catálogo.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Status = $"Não foi possível remover: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasEntries)); OnPropertyChanged(nameof(TotalFiles)); OnPropertyChanged(nameof(SummaryText));
        RemoveAllCommand.RaiseCanExecuteChanged();
        foreach (var row in Entries) row.RemoveCommand.RaiseCanExecuteChanged();
    }
}
