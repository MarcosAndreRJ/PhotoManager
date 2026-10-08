using System.Windows;
using System.Windows.Input;

namespace PhotoManager.Wpf.Views;

public partial class CompareWindow : Window
{
    private CompareItemViewModel? _focused;

    public CompareWindow() { InitializeComponent(); }

    public static void Open(CompareViewModel viewModel)
    {
        var window = new CompareWindow { DataContext = viewModel };
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner) window.Owner = owner;
        window._focused = viewModel.Items.FirstOrDefault();
        window.Show();
    }

    private void Item_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CompareItemViewModel item }) _focused = item;
    }

    /// <summary>P/X agem sobre o último item clicado; 1–4 escolhem o item pela posição; Esc fecha.</summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CompareViewModel viewModel) return;
        if (e.Key == Key.Escape) { Close(); return; }
        if (e.Key is >= Key.D1 and <= Key.D4 && (int)(e.Key - Key.D1) < viewModel.Items.Count) _focused = viewModel.Items[e.Key - Key.D1];
        else if (e.Key == Key.P && _focused is not null) viewModel.PickCommand.Execute(_focused);
        else if (e.Key == Key.X && _focused is not null) viewModel.RejectCommand.Execute(_focused);
        else return;
        e.Handled = true;
    }
}
