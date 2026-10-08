using System.Windows;
using System.Windows.Controls;

namespace PhotoManager.Wpf.Views;

/// <summary>Pergunta curta de texto (nome de coleção inteligente, pessoa, onde o clipe foi usado). Montada em código: não precisa de XAML próprio.</summary>
public static class TextPromptDialog
{
    /// <summary>Devolve o texto digitado, ou nulo se o usuário cancelou.</summary>
    public static string? Ask(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 16), MinWidth = 360 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Width = 90 };
        if (System.Windows.Application.Current?.TryFindResource("AccentButton") is Style accent) ok.Style = accent;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var caption = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var window = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new StackPanel { Margin = new Thickness(20), Children = { caption, box, buttons } }
        };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner) window.Owner = owner;
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text : null;
    }
}
