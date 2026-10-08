using System.Windows;

namespace PhotoManager.Wpf.Views;

public partial class ExportDialog : Window
{
    public ExportDialog() { InitializeComponent(); }

    public static bool Ask(ExportDialogViewModel viewModel)
    {
        var dialog = new ExportDialog { DataContext = viewModel };
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner) dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExportDialogViewModel { CanExport: true }) DialogResult = true;
    }
}
