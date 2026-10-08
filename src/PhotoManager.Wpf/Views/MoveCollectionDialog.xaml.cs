using System.Windows;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

public partial class MoveCollectionDialog : Window
{
    public MoveCollectionDialog(MoveCollectionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MoveCollectionViewModel { CanMove: true })
        {
            DialogResult = true;
            Close();
        }
    }
}
