using System.Windows;
using System.Windows.Controls;

namespace PhotoManager.Wpf.Views;

/// <summary>Integração visual da Transferência: proporção do divisor (guardada no layout) e arquivos soltos sobre os Locais.</summary>
public partial class TransferView : UserControl
{
    public TransferView() { InitializeComponent(); }

    private TransferViewModel? ViewModel => DataContext as TransferViewModel;

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        LeftColumn.Width = new GridLength(viewModel.LeftRatio, GridUnitType.Star);
        RightColumn.Width = new GridLength(1 - viewModel.LeftRatio, GridUnitType.Star);
    }

    /// <summary>Sair da tela (ou fechar o app) grava o layout na hora, sem esperar o atraso de meio segundo.</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e) => ViewModel?.SaveLayoutNow();

    private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (ViewModel is null) return;
        var total = LeftColumn.ActualWidth + RightColumn.ActualWidth;
        if (total > 0) ViewModel.LeftRatio = LeftColumn.ActualWidth / total;
    }

    private void Place_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = ViewModel is not null && e.Data.GetDataPresent(DataFormats.FileDrop) && (sender as FrameworkElement)?.DataContext is TransferPlace
            ? (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? DragDropEffects.Move : DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <summary>Soltar sobre um Local (pendrive, pasta fixada…) copia para lá sem precisar abri-lo; Shift move.</summary>
    private void Place_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } viewModel || (sender as FrameworkElement)?.DataContext is not TransferPlace place || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        var move = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        _ = viewModel.RunTransferAsync([new PhotoManager.Application.Transfer.TransferJob(files, place.Path)], move, $"{(move ? "mover" : "copiar")} {files.Length} item(ns) para “{place.Label}”");
    }
}
