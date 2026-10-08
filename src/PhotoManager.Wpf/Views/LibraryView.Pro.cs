using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Controls;

namespace PhotoManager.Wpf.Views;

/// <summary>Visível quando o valor é falso (o par do BooleanToVisibilityConverter).</summary>
public sealed class InverseBooleanToVisibility : IValueConverter
{
    public static InverseBooleanToVisibility Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Collapsed;
}

/// <summary>Integração visual dos recursos "pró": atalhos de triagem, paleta, sugestões da busca, linha do tempo, menus dos botões e marcação de trechos.</summary>
public partial class LibraryView
{
    private LibraryViewModel? _proSubscribed;

    private void InitPro()
    {
        PreviewKeyDown += Library_PreviewKeyDown;
        DataContextChanged += (_, _) =>
        {
            if (_proSubscribed is not null)
            {
                _proSubscribed.SelectAllRequested -= SelectAllFromViewModel;
                _proSubscribed.ClearSelectionRequested -= ClearSelectionFromViewModel;
                _proSubscribed.ScrollToIndexRequested -= ScrollToIndex;
            }
            _proSubscribed = DataContext as LibraryViewModel;
            if (_proSubscribed is not null)
            {
                _proSubscribed.SelectAllRequested += SelectAllFromViewModel;
                _proSubscribed.ClearSelectionRequested += ClearSelectionFromViewModel;
                _proSubscribed.ScrollToIndexRequested += ScrollToIndex;
            }
        };
    }

    private void SelectAllFromViewModel() => PhotoList.SelectAll();
    private void ClearSelectionFromViewModel() => PhotoList.UnselectAll();

    /// <summary>Linha do tempo: rola até o primeiro item do mês (no painel justificado o item vai para o topo).</summary>
    private void ScrollToIndex(int index)
    {
        if (DataContext is not LibraryViewModel viewModel || index < 0 || index >= viewModel.Photos.Count) return;
        if (FindChild<VirtualizingJustifiedPanel>(PhotoList) is { } panel) panel.ScrollToIndex(index);
        else PhotoList.ScrollIntoView(viewModel.Photos[index]);
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>Atalhos globais da Biblioteca: Ctrl+K paleta, Ctrl+E exportar, Esc limpa seleção/fecha a paleta.</summary>
    private void Library_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel || viewModel.IsReviewMode) return;
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.K && viewModel.HasPro) { viewModel.OpenPaletteCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.E && viewModel.ExportCommand?.CanExecute(null) == true) { viewModel.ExportCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape && viewModel.IsPaletteOpen) { viewModel.IsPaletteOpen = false; e.Handled = true; }
        else if (e.Key == Key.Escape && viewModel.HasSelection && e.OriginalSource is not TextBox) { PhotoList.UnselectAll(); e.Handled = true; }
    }

    /// <summary>Na grade: P escolhe, X rejeita, U tira a bandeira; Ctrl+0…5 dá estrelas.</summary>
    private bool HandleProGridKey(KeyEventArgs e, LibraryViewModel viewModel)
    {
        if (!viewModel.HasPro) return false;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            var targets = viewModel.SelectedCards.Count > 0 ? viewModel.SelectedCards : viewModel.SelectedPhoto is { } one ? [one] : [];
            if (targets.Count == 0) return false;
            var flag = e.Key switch { Key.P => PickFlag.Picked, Key.X => PickFlag.Rejected, Key.U => (PickFlag?)PickFlag.None, _ => null };
            if (flag is null) return false;
            _ = viewModel.SetPickAsync(targets, flag.Value);
            return true;
        }
        if (modifiers == ModifierKeys.Control && e.Key is >= Key.D0 and <= Key.D5)
        {
            viewModel.RateSelectionCommand.Execute((e.Key - Key.D0).ToString(CultureInfo.InvariantCulture));
            return true;
        }
        return false;
    }

    /// <summary>Botões com menu (estrelas, cor, uso): o clique abre o menu logo abaixo, com o ViewModel como contexto.</summary>
    private void OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------- sugestões da busca ----------

    private void ShowSuggestions()
    {
        if (DataContext is LibraryViewModel { SearchSuggestions.Count: > 0 } && SearchBox.IsKeyboardFocused) SuggestionPopup.IsOpen = true;
        else SuggestionPopup.IsOpen = false;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && SuggestionPopup.IsOpen && SuggestionList.Items.Count > 0)
        {
            SuggestionList.SelectedIndex = 0;
            (SuggestionList.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SuggestionPopup.IsOpen) { SuggestionPopup.IsOpen = false; e.Handled = true; }
        else if (e.Key == Key.Enter && DataContext is LibraryViewModel viewModel) { SuggestionPopup.IsOpen = false; viewModel.ApplyFilters(); e.Handled = true; }
    }

    private void SearchBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject target && IsInside(target, SuggestionList)) return;
        SuggestionPopup.IsOpen = false;
    }

    private void SuggestionList_Click(object sender, MouseButtonEventArgs e) => ApplySuggestion();

    private void SuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplySuggestion(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SuggestionPopup.IsOpen = false; SearchBox.Focus(); e.Handled = true; }
    }

    private void ApplySuggestion()
    {
        if (SuggestionList.SelectedItem is not string text || DataContext is not LibraryViewModel viewModel) return;
        viewModel.ApplySuggestionCommand.Execute(text);
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
        ShowSuggestions();
    }

    private static bool IsInside(DependencyObject node, DependencyObject container)
    {
        for (var current = node; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, container)) return true;
        return false;
    }

    // ---------- paleta ----------

    private void PaletteBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PaletteBox.IsVisible) Dispatcher.BeginInvoke(() => { PaletteBox.Focus(); PaletteList.SelectedIndex = 0; }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PaletteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { PaletteList.SelectedIndex = Math.Min(PaletteList.Items.Count - 1, PaletteList.SelectedIndex + 1); PaletteList.ScrollIntoView(PaletteList.SelectedItem); e.Handled = true; }
        else if (e.Key == Key.Up) { PaletteList.SelectedIndex = Math.Max(0, PaletteList.SelectedIndex - 1); PaletteList.ScrollIntoView(PaletteList.SelectedItem); e.Handled = true; }
        else if (e.Key == Key.Enter) { RunSelectedPalette(); e.Handled = true; }
        else if (e.Key == Key.Escape && DataContext is LibraryViewModel viewModel) { viewModel.IsPaletteOpen = false; e.Handled = true; }
        if (PaletteList.SelectedIndex < 0 && PaletteList.Items.Count > 0) PaletteList.SelectedIndex = 0;
    }

    private void PaletteList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { RunSelectedPalette(); e.Handled = true; }
    }

    private void PaletteList_MouseUp(object sender, MouseButtonEventArgs e) => RunSelectedPalette();

    private void RunSelectedPalette()
    {
        if (DataContext is LibraryViewModel viewModel && (PaletteList.SelectedItem ?? (PaletteList.Items.Count > 0 ? PaletteList.Items[0] : null)) is PaletteCommand command)
            viewModel.RunPaletteCommand.Execute(command);
    }

    private void PaletteBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel) viewModel.IsPaletteOpen = false;
    }

    private void PalettePanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;   // clicar dentro não fecha

    // ---------- trechos de vídeo ----------

    private void MarkIn_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel) viewModel.MarkerIn = PreviewPlayer.PositionSeconds;
    }

    private void MarkOut_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel) viewModel.MarkerOut = PreviewPlayer.PositionSeconds;
    }

    private void PlayMarker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipMarkerViewModel marker }) PreviewPlayer.Seek(marker.Marker.InSeconds);
    }
}
