using System.ComponentModel;
using System.Windows;

namespace PhotoManager.Wpf;

public partial class MainWindow : System.Windows.Window
{
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>Tela cheia do modo de revisão: janela sem moldura e maximizada; ao sair, volta ao estado anterior.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsFullScreen) || sender is not MainViewModel viewModel) return;
        if (viewModel.IsFullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.Normal; // necessário para cobrir também a barra de tarefas
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
        }
    }
}
