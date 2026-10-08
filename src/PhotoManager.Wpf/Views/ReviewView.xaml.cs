using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace PhotoManager.Wpf.Views;

/// <summary>Somente comportamento visual do modo de revisão: atalhos de teclado, comandos do zoom e foco. Dados e regras ficam na ReviewViewModel/LibraryViewModel.</summary>
public partial class ReviewView : System.Windows.Controls.UserControl
{
    public ReviewView() { InitializeComponent(); }

    private LibraryViewModel? Library => (DataContext as ReviewViewModel)?.Library;

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Viewer.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Viewer.ZoomOut();
    private void Fit_Click(object sender, RoutedEventArgs e) => Viewer.Fit();
    private void Actual_Click(object sender, RoutedEventArgs e) => Viewer.Actual();

    private void Filmstrip_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Filmstrip.SelectedItem is not null) Filmstrip.ScrollIntoView(Filmstrip.SelectedItem);
    }

    private void Review_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Foco na própria tela para que as setas/Esc funcionem sem precisar clicar.
        if (e.NewValue is true) Dispatcher.BeginInvoke(() => Keyboard.Focus(this), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Review_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Library is not { } library) return;
        if (e.OriginalSource is not System.Windows.Controls.TextBox && Keyboard.Modifiers == ModifierKeys.None && HandleTriageKey(library, e.Key)) { e.Handled = true; return; }
        var handled = true;
        switch (e.Key)
        {
            case Key.Left or Key.PageUp: if (library.PreviousCommand.CanExecute(null)) library.PreviousCommand.Execute(null); break;
            case Key.Right or Key.PageDown or Key.Space: if (library.NextCommand.CanExecute(null)) library.NextCommand.Execute(null); break;
            case Key.Home: if (library.Photos.Count > 0) library.SelectedPhoto = library.Photos[0]; break;
            case Key.End: if (library.Photos.Count > 0) library.SelectedPhoto = library.Photos[^1]; break;
            case Key.Escape: if (library.IsFullScreen) library.ToggleFullScreenCommand.Execute(null); else library.ExitReview(); break;
            case Key.Enter or Key.F11: library.ToggleFullScreenCommand.Execute(null); break;
            case Key.F: Viewer.Fit(); break;
            case Key.D1 or Key.NumPad1: Viewer.Actual(); break;
            case Key.Add or Key.OemPlus: Viewer.ZoomIn(); break;
            case Key.Subtract or Key.OemMinus: Viewer.ZoomOut(); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    /// <summary>
    /// Triagem na revisão: P escolhe, X rejeita, U tira a bandeira (e avança, se ligado em Configurações).
    /// Vídeo: I marca a entrada e O a saída na posição do player; M guarda o trecho.
    /// </summary>
    private bool HandleTriageKey(LibraryViewModel library, Key key)
    {
        if (!library.HasPro || library.SelectedPhoto is not { } card) return false;
        switch (key)
        {
            case Key.P: _ = library.SetPickAsync([card], PhotoManager.Domain.Photos.PickFlag.Picked); return true;
            case Key.X: _ = library.SetPickAsync([card], PhotoManager.Domain.Photos.PickFlag.Rejected); return true;
            case Key.U: _ = library.SetPickAsync([card], PhotoManager.Domain.Photos.PickFlag.None); return true;
            case Key.I when card.IsVideo: library.MarkerIn = Player.PositionSeconds; return true;
            case Key.O when card.IsVideo: library.MarkerOut = Player.PositionSeconds; return true;
            case Key.M when card.IsVideo && library.CanAddMarker: _ = library.AddMarkerAsync(); return true;
            default: return false;
        }
    }
}

/// <summary>Visível apenas quando o valor não é nulo.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public static readonly NullToCollapsedConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Texto vazio vira "—" (campos opcionais em telas somente leitura).</summary>
public sealed class EmptyToDashConverter : IValueConverter
{
    public static readonly EmptyToDashConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.IsNullOrWhiteSpace(value as string) ? "—" : value!;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
