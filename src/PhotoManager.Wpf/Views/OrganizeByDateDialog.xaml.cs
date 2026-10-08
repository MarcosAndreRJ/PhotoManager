using System.Windows;

namespace PhotoManager.Wpf.Views;

public partial class OrganizeByDateDialog : Window
{
    public OrganizeByDateDialog() { InitializeComponent(); }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
