using System.Windows;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

public partial class DeleteCollectionDialog : Window
{
    public DeleteCollectionDialog(DeleteCollectionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
