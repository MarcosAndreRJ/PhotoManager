using System.Windows;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

public partial class CollectionNameDialog : Window
{
    public CollectionNameDialog(CollectionNameViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        NameInput.Focus();
        NameInput.SelectAll();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CollectionNameViewModel { IsValid: true })
        {
            DialogResult = true;
            Close();
        }
    }
}
