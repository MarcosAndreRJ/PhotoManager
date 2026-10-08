using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoManager.Application.Library;

namespace PhotoManager.Wpf.Controls;

/// <summary>Um ponto no mapa (latitude/longitude) com o item que ele representa.</summary>
public interface IMapItem
{
    double Latitude { get; }
    double Longitude { get; }
}

/// <summary>
/// Mapa leve (sem componentes externos): tiles do OpenStreetMap via <see cref="IMapTileProvider"/> (com cache), pontos agrupados em bolhas com contagem,
/// arrastar move, roda do mouse dá zoom no cursor, clique numa bolha aproxima, <b>Shift+arrastar</b> desenha uma área que vira filtro.
/// </summary>
public sealed class MapView : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IEnumerable<IMapItem>), typeof(MapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MapView)d).FitToItems()));
    public static readonly DependencyProperty TileProviderProperty = DependencyProperty.Register(nameof(TileProvider), typeof(IMapTileProvider), typeof(MapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RegionCommandProperty = DependencyProperty.Register(nameof(RegionCommand), typeof(ICommand), typeof(MapView));

    private const int ClusterCell = 56;
    private readonly Dictionary<string, ImageSource?> _tiles = [];
    private readonly HashSet<string> _loading = [];
    private double _centerX, _centerY;          // em pixels do mundo no zoom atual
    private int _zoom = 4;
    private Point? _dragStart, _selectStart;
    private Point _dragCenter;
    private Rect? _selection;
    private List<(Point Position, int Count, IMapItem First)> _clusters = [];

    public IEnumerable<IMapItem>? Items { get => (IEnumerable<IMapItem>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public IMapTileProvider? TileProvider { get => (IMapTileProvider?)GetValue(TileProviderProperty); set => SetValue(TileProviderProperty, value); }
    /// <summary>Recebe um <see cref="MapRegion"/> quando o usuário seleciona uma área (Shift+arrastar).</summary>
    public ICommand? RegionCommand { get => (ICommand?)GetValue(RegionCommandProperty); set => SetValue(RegionCommandProperty, value); }

    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, _) => { if (_centerX == 0 && _centerY == 0) FitToItems(); InvalidateVisual(); };
    }

    public int Zoom => _zoom;

    public void FitToItems()
    {
        var points = (Items ?? []).Select(i => (i.Latitude, i.Longitude)).ToList();
        var (lat, lon, zoom) = MapMath.Fit(points, Math.Max(200, ActualWidth), Math.Max(200, ActualHeight));
        _zoom = zoom;
        (_centerX, _centerY) = MapMath.ToPixel(lat, lon, _zoom);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth; var height = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xAA, 0xD3, 0xDF)), null, new Rect(0, 0, width, height));
        var originX = _centerX - width / 2; var originY = _centerY - height / 2;

        // Tiles visíveis
        var size = MapMath.TileSize;
        int firstX = (int)Math.Floor(originX / size), lastX = (int)Math.Floor((originX + width) / size);
        int firstY = Math.Max(0, (int)Math.Floor(originY / size)), lastY = Math.Min((1 << _zoom) - 1, (int)Math.Floor((originY + height) / size));
        for (var ty = firstY; ty <= lastY; ty++)
            for (var tx = firstX; tx <= lastX; tx++)
            {
                var rect = new Rect(tx * size - originX, ty * size - originY, size, size);
                if (TileImage(_zoom, tx, ty) is { } image) dc.DrawImage(image, rect);
                else dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xDC)), new Pen(new SolidColorBrush(Color.FromRgb(0xD8, 0xD3, 0xC9)), 0.5), rect);
            }

        // Pontos agrupados por célula de tela
        _clusters = (Items ?? []).Select(i => (Item: i, Pixel: MapMath.ToPixel(i.Latitude, i.Longitude, _zoom)))
            .Select(x => (x.Item, Screen: new Point(x.Pixel.X - originX, x.Pixel.Y - originY)))
            .Where(x => x.Screen.X > -ClusterCell && x.Screen.Y > -ClusterCell && x.Screen.X < width + ClusterCell && x.Screen.Y < height + ClusterCell)
            .GroupBy(x => ((int)Math.Floor(x.Screen.X / ClusterCell), (int)Math.Floor(x.Screen.Y / ClusterCell)))
            .Select(g => (new Point(g.Average(x => x.Screen.X), g.Average(x => x.Screen.Y)), g.Count(), g.First().Item))
            .ToList();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fill = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        var stroke = new Pen(Brushes.White, 2);
        foreach (var (position, count, _) in _clusters)
        {
            var radius = count == 1 ? 7 : Math.Min(26, 11 + Math.Log2(count) * 3);
            dc.DrawEllipse(fill, stroke, position, radius, radius);
            if (count > 1)
            {
                var text = new FormattedText(count.ToString("N0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 11, Brushes.White, dpi);
                dc.DrawText(text, new Point(position.X - text.Width / 2, position.Y - text.Height / 2));
            }
        }

        if (_selection is { } selection)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x33, 0x25, 0x63, 0xEB)), new Pen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), 1.5), selection);

        // Atribuição exigida pelo OpenStreetMap
        var credit = new FormattedText("© colaboradores do OpenStreetMap", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.Black, dpi);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), null, new Rect(width - credit.Width - 10, height - credit.Height - 4, credit.Width + 10, credit.Height + 4));
        dc.DrawText(credit, new Point(width - credit.Width - 5, height - credit.Height - 2));
    }

    private ImageSource? TileImage(int zoom, int x, int y)
    {
        var key = $"{zoom}/{x}/{y}";
        if (_tiles.TryGetValue(key, out var cached)) return cached;
        if (TileProvider is { } provider && _loading.Add(key)) _ = LoadTileAsync(provider, zoom, x, y, key);
        // enquanto carrega, mostra o tile "pai" ampliado se já estiver em memória
        return zoom > 0 && _tiles.TryGetValue($"{zoom - 1}/{x >> 1}/{y >> 1}", out var parent) && parent is BitmapSource source
            ? new CroppedBitmap(source, new Int32Rect((x & 1) * 128, (y & 1) * 128, 128, 128)) : null;
    }

    private async Task LoadTileAsync(IMapTileProvider provider, int zoom, int x, int y, string key)
    {
        try
        {
            var path = await provider.GetTileAsync(zoom, x, y);
            ImageSource? image = null;
            if (path is not null)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            _tiles[key] = image;
            if (_tiles.Count > 600) foreach (var old in _tiles.Keys.Where(k => !k.StartsWith(_zoom + "/", StringComparison.Ordinal)).Take(200).ToList()) _tiles.Remove(old);
            InvalidateVisual();
        }
        catch (Exception) { _tiles[key] = null; }
        finally { _loading.Remove(key); }
    }

    // ---------- interação ----------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var mouse = e.GetPosition(this);
        ZoomAt(mouse, e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void ZoomAt(Point screen, int delta)
    {
        var newZoom = Math.Clamp(_zoom + delta, 2, 18);
        if (newZoom == _zoom) return;
        var worldX = _centerX - ActualWidth / 2 + screen.X; var worldY = _centerY - ActualHeight / 2 + screen.Y;
        var factor = Math.Pow(2, newZoom - _zoom);
        _centerX = worldX * factor - (screen.X - ActualWidth / 2);
        _centerY = worldY * factor - (screen.Y - ActualHeight / 2);
        _zoom = newZoom;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var position = e.GetPosition(this);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _selectStart = position;
        else { _dragStart = position; _dragCenter = new Point(_centerX, _centerY); }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (_dragStart is { } start)
        {
            _centerX = _dragCenter.X - (position.X - start.X);
            _centerY = _dragCenter.Y - (position.Y - start.Y);
            InvalidateVisual();
        }
        else if (_selectStart is { } origin)
        {
            _selection = new Rect(origin, position);
            InvalidateVisual();
        }
        Cursor = _dragStart is not null ? Cursors.SizeAll : _clusters.Any(c => (c.Position - position).Length < 14) ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var position = e.GetPosition(this);
        ReleaseMouseCapture();
        if (_selectStart is not null && _selection is { Width: > 8, Height: > 8 } selection)
        {
            var origin = new Point(_centerX - ActualWidth / 2, _centerY - ActualHeight / 2);
            var (north, west) = MapMath.ToLatLon(origin.X + selection.Left, origin.Y + selection.Top, _zoom);
            var (south, east) = MapMath.ToLatLon(origin.X + selection.Right, origin.Y + selection.Bottom, _zoom);
            RegionCommand?.Execute(new MapRegion(south, west, north, east));
        }
        else if (_dragStart is { } start && (position - start).Length < 4 && _clusters.FirstOrDefault(c => (c.Position - position).Length < 22) is { Count: > 0 } cluster)
        {
            if (cluster.Count > 1 && _zoom < 17) { ZoomAt(cluster.Position, 2); }
            else
            {
                // um ponto (ou zoom máximo): filtra pelos itens desta bolha
                var origin = new Point(_centerX - ActualWidth / 2, _centerY - ActualHeight / 2);
                var (north, west) = MapMath.ToLatLon(origin.X + cluster.Position.X - ClusterCell, origin.Y + cluster.Position.Y - ClusterCell, _zoom);
                var (south, east) = MapMath.ToLatLon(origin.X + cluster.Position.X + ClusterCell, origin.Y + cluster.Position.Y + ClusterCell, _zoom);
                RegionCommand?.Execute(new MapRegion(south, west, north, east));
            }
        }
        _dragStart = null; _selectStart = null; _selection = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus: ZoomAt(center, 1); break;
            case Key.Subtract or Key.OemMinus: ZoomAt(center, -1); break;
            case Key.Home: FitToItems(); break;
            default: return;
        }
        e.Handled = true;
    }
}

/// <summary>Área selecionada no mapa (graus).</summary>
public sealed record MapRegion(double South, double West, double North, double East);
