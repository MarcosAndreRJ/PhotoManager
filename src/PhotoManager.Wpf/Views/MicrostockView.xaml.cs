using System.Windows;
using System.Windows.Controls;

namespace PhotoManager.Wpf.Views;

public partial class MicrostockView : UserControl
{
    public MicrostockView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is MicrostockViewModel viewModel)
                viewModel.ConfirmAction = message => MessageBox.Show(message, "PhotoManager", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        };
    }

    private void ProductionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MicrostockViewModel viewModel && sender is DataGrid grid)
            viewModel.SetSelectedRows(grid.SelectedItems.OfType<MicrostockPhotoRowViewModel>());
    }
}
