namespace PhotoManager.Wpf.Views;

public partial class ToolsView : System.Windows.Controls.UserControl
{
    public ToolsView() { InitializeComponent(); }

    private void ChooseFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not ToolsViewModel viewModel) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha a pasta de destino" };
        if (dialog.ShowDialog() == true) viewModel.DestinationFolder = dialog.FolderName;
    }

    private void OpenFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: DuplicatePhotoViewModel photo }) return;
        var target = File.Exists(photo.Path) ? photo.Path : Path.GetDirectoryName(photo.Path);
        if (string.IsNullOrWhiteSpace(target)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
    }
}
