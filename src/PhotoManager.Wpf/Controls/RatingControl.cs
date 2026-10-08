using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoManager.Wpf.Controls;

/// <summary>Cinco estrelas vetoriais desenhadas diretamente (sem um elemento visual por estrela, para manter o grid leve).</summary>
public sealed class RatingControl : FrameworkElement
{
    private static readonly Geometry StarGeometry = Geometry.Parse("M 0,-1 L 0.2245,-0.309 L 0.9511,-0.309 L 0.3633,0.118 L 0.5878,0.809 L 0,0.382 L -0.5878,0.809 L -0.3633,0.118 L -0.9511,-0.309 L -0.2245,-0.309 Z");

    static RatingControl() => StarGeometry.Freeze();

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int), typeof(RatingControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(RatingControl), new PropertyMetadata(false));
    public static readonly DependencyProperty StarSizeProperty = DependencyProperty.Register(nameof(StarSize), typeof(double), typeof(RatingControl),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public double StarSize { get => (double)GetValue(StarSizeProperty); set => SetValue(StarSizeProperty, value); }

    private double Step => StarSize + 3;

    protected override Size MeasureOverride(Size availableSize) => new(Step * 5 - 3, StarSize);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var on = TryFindResource("StarBrush") as Brush ?? Brushes.Gold;
        var off = TryFindResource("StarOffBrush") as Brush ?? Brushes.LightGray;
        var radius = StarSize / 2;
        for (var i = 0; i < 5; i++)
        {
            var transform = new TransformGroup();
            transform.Children.Add(new ScaleTransform(radius, radius));
            transform.Children.Add(new TranslateTransform(i * Step + radius, radius));
            drawingContext.PushTransform(transform);
            drawingContext.DrawGeometry(i < Value ? on : off, null, StarGeometry);
            drawingContext.Pop();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsReadOnly) return;
        var star = (int)Math.Clamp(e.GetPosition(this).X / Step, 0, 4) + 1;
        Value = star == Value ? 0 : star;
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); if (!IsReadOnly) Cursor = Cursors.Hand; }
}
