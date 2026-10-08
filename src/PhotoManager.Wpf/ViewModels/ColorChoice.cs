using System.Windows.Media;
using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Commands;

namespace PhotoManager.Wpf.ViewModels;

/// <summary>Uma opção do submenu "Cor": nome, amostra de cor e o comando que aplica aos itens alvo do menu.</summary>
public sealed class ColorChoice(PhotoColor color, RelayCommand command)
{
    public PhotoColor Color { get; } = color;
    public string Name { get; } = PhotoColors.Name(color);
    public string Shortcut { get; } = color == PhotoColor.None ? "0" : ((int)color).ToString();
    public bool IsNone => Color == PhotoColor.None;
    public Brush Brush { get; } = Freeze(color);
    public RelayCommand Command { get; } = command;

    private static Brush Freeze(PhotoColor color)
    {
        var brush = color == PhotoColor.None ? Brushes.Transparent : (Brush)new BrushConverter().ConvertFromString(PhotoColors.Hex(color))!;
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }
}

/// <summary>Pincéis congelados das etiquetas de cor (um por cor, criados uma vez).</summary>
public static class ColorLookup
{
    private static readonly Dictionary<PhotoColor, Brush> Cache = Enum.GetValues<PhotoColor>().ToDictionary(c => c, c =>
    {
        var brush = c == PhotoColor.None ? Brushes.Transparent : (Brush)new BrushConverter().ConvertFromString(PhotoColors.Hex(c))!;
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    });

    public static Brush BrushFor(PhotoColor color) => Cache[color];
}
