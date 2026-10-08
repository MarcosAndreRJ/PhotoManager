using System.Windows;

namespace PhotoManager.Wpf.Views;

public partial class BatchRenameDialog : Window
{
    public BatchRenameDialog() { InitializeComponent(); }

    private void Window_Loaded(object sender, RoutedEventArgs e) { TemplateBox.Focus(); TemplateBox.SelectAll(); }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BatchRenameViewModel { IsValid: true }) DialogResult = true;
    }
}
