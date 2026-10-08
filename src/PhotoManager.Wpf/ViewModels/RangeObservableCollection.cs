using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PhotoManager.Wpf.ViewModels;

/// <summary>ObservableCollection que troca todo o conteúdo com uma única notificação (evita N eventos ao refiltrar milhares de fotos).</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
