using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoManager.Wpf.Controls;

/// <summary>
/// Visualizador de imagem com "ajustar à janela", 100 % (pixels reais), zoom pela roda do mouse ancorado no cursor,
/// arrastar para mover e duplo clique alternando ajustar/100 %. Troca de imagem volta a "ajustar".
/// </summary>
public sealed class ZoomViewer : ScrollViewer
{
    private const double MinZoom = 0.02, MaxZoom = 16, WheelFactor = 1.18;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(ZoomViewer),
        new PropertyMetadata(null, (d, e) => ((ZoomViewer)d).OnSourceChanged((ImageSource?)e.OldValue, (ImageSource?)e.NewValue)));
    private static readonly DependencyPropertyKey ZoomPercentKey = DependencyProperty.RegisterReadOnly(nameof(ZoomPercent), typeof(double), typeof(ZoomViewer), new PropertyMetadata(0d));
    public static readonly DependencyProperty ZoomPercentProperty = ZoomPercentKey.DependencyProperty;
    private static readonly DependencyPropertyKey IsFitKey = DependencyProperty.RegisterReadOnly(nameof(IsFit), typeof(bool), typeof(ZoomViewer), new PropertyMetadata(true));
    public static readonly DependencyProperty IsFitProperty = IsFitKey.DependencyProperty;

    private readonly Grid _host = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
    private double _zoom = 1;
    private Point? _dragStart;
    private Point _dragOffset;

    public ZoomViewer()
    {
        _host.Children.Add(_image);
        Content = _host;
        Focusable = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        SizeChanged += (_, _) => Apply();
        ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) UpdateHostSize(); };
    }

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    /// <summary>Zoom atual em % do tamanho real da imagem (100 = pixels reais).</summary>
    public double ZoomPercent => (double)GetValue(ZoomPercentProperty);
    public bool IsFit => (bool)GetValue(IsFitProperty);

    public void Fit() { SetValue(IsFitKey, true); Apply(); }
    public void Actual() { SetValue(IsFitKey, false); _zoom = 1; Apply(); CenterContent(); }
    public void ZoomIn() => ZoomBy(WheelFactor, null);
    public void ZoomOut() => ZoomBy(1 / WheelFactor, null);

    private void OnSourceChanged(ImageSource? oldValue, ImageSource? newValue)
    {
        // Mesma foto em resolução maior (preview → completa): mantém o enquadramento atual; foto diferente: volta a "ajustar".
        var sameShape = oldValue is not null && newValue is not null && NaturalSize(oldValue) is var a && NaturalSize(newValue) is var b
            && Math.Abs(a.Width / Math.Max(a.Height, 1) - b.Width / Math.Max(b.Height, 1)) < 0.01;
        _image.Source = newValue;
        if (!sameShape || IsFit) { SetValue(IsFitKey, true); }
        else if (!IsFit && oldValue is not null && newValue is not null) _zoom *= NaturalSize(oldValue).Width / NaturalSize(newValue).Width; // preserva o tamanho na tela
        Apply();
    }

    private Size NaturalSize(ImageSource source)
    {
        var scale = VisualTreeHelper.GetDpi(this);
        return source is System.Windows.Media.Imaging.BitmapSource bitmap
            ? new Size(bitmap.PixelWidth / scale.DpiScaleX, bitmap.PixelHeight / scale.DpiScaleY)
            : new Size(source.Width, source.Height);
    }

    private void Apply()
    {
        if (Source is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _image.Width = _image.Height = double.NaN;
            SetValue(ZoomPercentKey, 0d);
            return;
        }
        var natural = NaturalSize(Source);
        if (natural.Width <= 0 || natural.Height <= 0) return;
        if (IsFit) _zoom = Math.Min(1, Math.Min(ActualWidth / natural.Width, ActualHeight / natural.Height)); // ajustar nunca amplia além do tamanho real
        _zoom = Math.Clamp(_zoom, MinZoom, MaxZoom);
        _image.Width = natural.Width * _zoom;
        _image.Height = natural.Height * _zoom;
        HorizontalScrollBarVisibility = VerticalScrollBarVisibility = IsFit ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        UpdateHostSize();
        if (IsFit) { ScrollToHorizontalOffset(0); ScrollToVerticalOffset(0); }
        SetValue(ZoomPercentKey, Math.Round(_zoom * 100, 0));
    }

    /// <summary>O contêiner nunca é menor que a área visível: assim a imagem pequena fica centralizada.</summary>
    private void UpdateHostSize()
    {
        _host.MinWidth = Math.Max(0, ViewportWidth > 0 ? ViewportWidth : ActualWidth);
        _host.MinHeight = Math.Max(0, ViewportHeight > 0 ? ViewportHeight : ActualHeight);
    }

    private void CenterContent()
    {
        UpdateLayout();
        ScrollToHorizontalOffset(Math.Max(0, (ExtentWidth - ViewportWidth) / 2));
        ScrollToVerticalOffset(Math.Max(0, (ExtentHeight - ViewportHeight) / 2));
    }

    private void ZoomBy(double factor, Point? anchor)
    {
        if (Source is null) return;
        var point = anchor ?? new Point(ViewportWidth / 2, ViewportHeight / 2);
        var ratioX = ExtentWidth > 0 ? (HorizontalOffset + point.X) / ExtentWidth : 0.5;
        var ratioY = ExtentHeight > 0 ? (VerticalOffset + point.Y) / ExtentHeight : 0.5;
        SetValue(IsFitKey, false);
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        Apply();
        UpdateLayout();
        ScrollToHorizontalOffset(Math.Max(0, ratioX * ExtentWidth - point.X));
        ScrollToVerticalOffset(Math.Max(0, ratioY * ExtentHeight - point.Y));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomBy(e.Delta > 0 ? WheelFactor : 1 / WheelFactor, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2) { if (IsFit) Actual(); else Fit(); e.Handled = true; return; }
        if (IsFit) return;
        _dragStart = e.GetPosition(this);
        _dragOffset = new Point(HorizontalOffset, VerticalOffset);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is not { } start) return;
        var now = e.GetPosition(this);
        ScrollToHorizontalOffset(_dragOffset.X - (now.X - start.X));
        ScrollToVerticalOffset(_dragOffset.Y - (now.Y - start.Y));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragStart is null) return;
        _dragStart = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }
}
