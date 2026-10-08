using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;

namespace PhotoManager.Wpf.Views;

/// <summary>Somente integração visual do editor: leitura preguiçosa das linhas visíveis, foco por teclado, confirmação e link do mapa.</summary>
public partial class MetadataView : System.Windows.Controls.UserControl
{
    public MetadataView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MetadataEditorViewModel viewModel)
                viewModel.ConfirmRevert = count => System.Windows.MessageBox.Show($"Descartar as alterações não salvas de {count} foto(s)? Os arquivos não serão alterados.", "Reverter tudo", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        };
    }

    /// <summary>A linha só lê os metadados do arquivo quando aparece na lista (virtualizada).</summary>
    private async void Row_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBoxItem { DataContext: MetadataRowViewModel row } && DataContext is MetadataEditorViewModel viewModel)
            await viewModel.EnsureLoadedAsync(row);
    }

    /// <summary>Digitar numa linha a coloca em foco (o painel da direita mostra EXIF/histórico dela).</summary>
    private void Rows_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        for (var node = e.NewFocus as System.Windows.DependencyObject; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.ListBoxItem item) { item.IsSelected = true; return; }
    }

    private void MaximizeThumb_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        OpenFocusedViewer();
    }

    private void FocusedThumb_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { e.Handled = true; OpenFocusedViewer(); }
    }

    private void RowThumb_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not System.Windows.FrameworkElement { DataContext: MetadataRowViewModel row } || row.Card.IsMissing) return;
        e.Handled = true;
        MediaViewerWindow.Open(row.Photo);
    }

    private void OpenFocusedViewer()
    {
        if (DataContext is MetadataEditorViewModel { Focused: { } row } && !row.Card.IsMissing) MediaViewerWindow.Open(row.Photo);
    }

    private void OpenMap_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is MetadataEditorViewModel { FocusedMapUrl: { } url })
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

/// <summary>Visível apenas quando a contagem é zero (para o traço "—" de listas vazias).</summary>
public sealed class CountToCollapsedConverter : IValueConverter
{
    public static readonly CountToCollapsedConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visível apenas quando o texto não é vazio.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public static readonly TextToVisibilityConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
