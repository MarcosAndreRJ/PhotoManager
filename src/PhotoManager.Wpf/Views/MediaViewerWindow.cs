using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoManager.Domain.Photos;
using PhotoManager.Infrastructure.Images;
using PhotoManager.Wpf.Controls;

namespace PhotoManager.Wpf.Views;

/// <summary>
/// Visualizador ampliado de um item: foto em resolução cheia (com zoom) ou vídeo com o reprodutor embutido, já com a rotação aplicada.
/// Esc fecha. Ao fechar, o arquivo de vídeo é liberado.
/// </summary>
public sealed class MediaViewerWindow : Window
{
    private readonly VideoPlayer? _player;
    private readonly ZoomViewer? _zoom;
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0xD0, 0xE0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };

    public MediaViewerWindow(Photo photo)
    {
        Photo = photo;
        Title = photo.FileName;
        Width = 1100; Height = 760; MinWidth = 480; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x20));
        ShowInTaskbar = false;

        var grid = new Grid();
        if (photo.IsVideo)
        {
            _player = new VideoPlayer { RotationDegrees = photo.VideoDisplayRotation, AutoPlay = true };
            grid.Children.Add(_player);
            Loaded += (_, _) => _player.SourcePath = photo.CurrentPath;
        }
        else
        {
            _zoom = new ZoomViewer { Background = Background };
            grid.Children.Add(_zoom);
            grid.Children.Add(_status);
            _status.Text = "Carregando…";
            Loaded += async (_, _) => await LoadPhotoAsync();
        }
        Content = grid;
        Closed += (_, _) => { if (_player is not null) _player.SourcePath = null; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Space && _player is not null) { e.Handled = true; _player.TogglePlay(); }
        };
    }

    public Photo Photo { get; }
    public bool IsVideoViewer => _player is not null;
    public ZoomViewer? Zoom => _zoom;
    public VideoPlayer? Player => _player;

    private async Task LoadPhotoAsync()
    {
        var path = Photo.CurrentPath;
        var rotation = Photo.UserRotation;
        try
        {
            var image = await Task.Run(() => ImageLoader.LoadFull(path) is { } full ? ExifOrientation.Rotate(full, rotation) : null);
            if (image is null) { _status.Text = "Não foi possível abrir a imagem."; return; }
            _zoom!.Source = image;
            _status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            _status.Text = $"Não foi possível abrir a imagem: {ex.Message}";
        }
    }

    /// <summary>Abre o visualizador sobre a janela ativa.</summary>
    public static void Open(Photo photo)
    {
        var window = new MediaViewerWindow(photo) { Owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? System.Windows.Application.Current?.MainWindow };
        window.Show();
        window.Activate();
    }
}
