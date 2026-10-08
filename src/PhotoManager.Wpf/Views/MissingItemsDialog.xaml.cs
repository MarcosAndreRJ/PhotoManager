using System.Windows;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

public partial class MissingItemsDialog : Window
{
    public MissingItemsDialog(MissingItemsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
