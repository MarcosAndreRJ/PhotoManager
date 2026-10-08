using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PhotoManager.Wpf.Controls;

/// <summary>Item que sabe a própria proporção (largura/altura como exibida) e, opcionalmente, o grupo (dia) a que pertence.</summary>
public interface IJustifiedItem
{
    double AspectRatio { get; }
    string? GroupKey { get; }
    string? GroupLabel { get; }
}

/// <summary>
/// Grade "justificada" (como Google Fotos / Lightroom): cada linha tem altura próxima de <see cref="RowHeight"/> e é esticada até a largura toda,
/// e cada item ocupa a largura da sua proporção — retratos não desperdiçam espaço com faixas brancas.
/// Virtualiza por linha (só cria os itens visíveis) e desenha os cabeçalhos de grupo (dia) acima de cada grupo.
/// </summary>
public sealed class VirtualizingJustifiedPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(nameof(RowHeight), typeof(double), typeof(VirtualizingJustifiedPanel),
        new FrameworkPropertyMetadata(200d, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((VirtualizingJustifiedPanel)d).InvalidateLayoutCache()));
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(VirtualizingJustifiedPanel),
        new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((VirtualizingJustifiedPanel)d).InvalidateLayoutCache()));
    public static readonly DependencyProperty HeaderBrushProperty = DependencyProperty.Register(nameof(HeaderBrush), typeof(Brush), typeof(VirtualizingJustifiedPanel),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public const double HeaderHeight = 40;

    private readonly List<Rect> _rects = [];
    private readonly List<(double Y, string Label)> _headers = [];
    private double _layoutWidth = -1;
    private int _layoutCount = -1;
    private Size _extent, _viewport;
    private Point _offset;

    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public Brush HeaderBrush { get => (Brush)GetValue(HeaderBrushProperty); set => SetValue(HeaderBrushProperty, value); }

    private ItemCollection? Items => ItemsControl.GetItemsOwner(this)?.Items;

    private void InvalidateLayoutCache() { _layoutWidth = -1; InvalidateMeasure(); }

    /// <summary>
    /// Distribuição em linhas (pura, testável): acumula itens até a soma das larguras na altura-alvo passar da largura disponível e então
    /// escala a linha para caber exatamente. A última linha de cada grupo não é esticada. Grupos novos começam numa linha nova com cabeçalho.
    /// </summary>
    public static (List<Rect> Rects, List<(double Y, string Label)> Headers, double Height) Compute(IReadOnlyList<(double Aspect, string? Group, string? Label)> items, double width, double rowHeight, double spacing, double headerHeight = HeaderHeight)
    {
        var rects = new List<Rect>(items.Count);
        var headers = new List<(double, string)>();
        var y = 0.0;
        var start = 0;
        string? currentGroup = null;
        while (start < items.Count)
        {
            if (items[start].Group is { } group && group != currentGroup)
            {
                currentGroup = group;
                headers.Add((y, items[start].Label ?? group));
                y += headerHeight;
            }
            var end = start;
            var sum = 0.0;
            while (end < items.Count && (end == start || items[end].Group == items[start].Group))
            {
                sum += items[end].Aspect * rowHeight;
                end++;
                if (sum + spacing * (end - start - 1) >= width) break;
            }
            var gaps = spacing * (end - start - 1);
            var full = sum + gaps >= width && end - start > 0;
            var scale = full ? (width - gaps) / sum : 1.0;
            var height = Math.Min(rowHeight * scale, rowHeight * 2.2);
            var x = 0.0;
            for (var i = start; i < end; i++)
            {
                var w = items[i].Aspect * height;
                if (full && i == end - 1) w = Math.Max(1, width - x);                // fecha o arredondamento na borda direita
                rects.Add(new Rect(x, y, Math.Max(1, w), height));
                x += w + spacing;
            }
            y += height + spacing;
            start = end;
        }
        return (rects, headers, y);
    }

    private void EnsureLayout(double width)
    {
        var items = Items;
        var count = items?.Count ?? 0;
        if (Math.Abs(width - _layoutWidth) < 0.5 && count == _layoutCount) return;
        _layoutWidth = width; _layoutCount = count;
        var data = new List<(double, string?, string?)>(count);
        for (var i = 0; i < count; i++)
            data.Add(items![i] is IJustifiedItem item ? (item.AspectRatio, item.GroupKey, item.GroupLabel) : (1.5, null, null));
        var (rects, headers, height) = Compute(data, Math.Max(100, width - 2), RowHeight, Spacing);
        _rects.Clear(); _rects.AddRange(rects);
        _headers.Clear(); _headers.AddRange(headers);
        _extent = new Size(width, height);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _ = InternalChildren;                                                          // o WPF só cria o ItemContainerGenerator depois deste acesso
        var width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height;
        EnsureLayout(width);
        UpdateScrollInfo(new Size(width, height));
        var count = _rects.Count;
        if (count == 0) { CleanUp(0, -1); return new Size(width, height); }

        // Primeiro e último itens que tocam a janela visível (com uma folga de meia tela para rolar sem buracos).
        var top = _offset.Y - height / 2; var bottom = _offset.Y + height * 1.5;
        var first = LowerBound(top);
        var last = first;
        while (last + 1 < count && _rects[last + 1].Top <= bottom) last++;

        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (var index = first; index <= last; index++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var isNew);
                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(_rects[index].Size);
            }
        }
        CleanUp(first, last);
        return new Size(width, height);
    }

    /// <summary>Primeiro item cuja base passa de <paramref name="y"/> (busca binária: as linhas estão em ordem).</summary>
    private int LowerBound(double y)
    {
        int lo = 0, hi = _rects.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_rects[mid].Bottom < y) lo = mid + 1; else hi = mid;
        }
        // volta ao início da linha
        while (lo > 0 && Math.Abs(_rects[lo - 1].Top - _rects[lo].Top) < 0.5) lo--;
        return lo;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0 || index >= _rects.Count) continue;
            var rect = _rects[index];
            InternalChildren[i].Arrange(new Rect(rect.X - _offset.X, rect.Y - _offset.Y, rect.Width, rect.Height));
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_headers.Count == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        foreach (var (y, label) in _headers)
        {
            var top = y - _offset.Y;
            if (top < -HeaderHeight || top > _viewport.Height) continue;
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 14, HeaderBrush, dpi);
            drawingContext.DrawText(text, new Point(2, top + (HeaderHeight - text.Height) / 2 + 4));
        }
    }

    private void CleanUp(int firstVisible, int lastVisible)
    {
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= firstVisible && index <= lastVisible) continue;
            ItemContainerGenerator.Remove(new GeneratorPosition(i, 0), 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
        }
        _layoutCount = -1;                                                              // itens novos: recalcula as linhas
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= _rects.Count) return;
        var rect = _rects[index];
        var headerRoom = _headers.Any(h => Math.Abs(h.Y + HeaderHeight - rect.Top) < 1) ? HeaderHeight : 0;
        if (rect.Top - headerRoom < _offset.Y) SetVerticalOffset(rect.Top - headerRoom);
        else if (rect.Bottom > _offset.Y + _viewport.Height) SetVerticalOffset(rect.Bottom - _viewport.Height);
    }

    /// <summary>Rola para deixar o item no topo (linha do tempo).</summary>
    public void ScrollToIndex(int index)
    {
        if (index < 0 || index >= _rects.Count) return;
        var rect = _rects[index];
        var header = _headers.LastOrDefault(h => h.Y <= rect.Top);
        SetVerticalOffset(header.Label is not null && rect.Top - header.Y <= HeaderHeight + 1 ? header.Y : rect.Top);
    }

    private void UpdateScrollInfo(Size viewport)
    {
        var changed = viewport != _viewport;
        _viewport = viewport;
        var maxOffset = Math.Max(0, _extent.Height - viewport.Height);
        if (_offset.Y > maxOffset) _offset.Y = maxOffset;
        if (changed) ScrollOwner?.InvalidateScrollInfo();
        ScrollOwner?.InvalidateScrollInfo();
    }

    // IScrollInfo
    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(offset - _offset.Y) < 0.1) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset(_offset.Y - 48);
    public void LineDown() => SetVerticalOffset(_offset.Y + 48);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - RowHeight * 0.6);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + RowHeight * 0.6);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is UIElement element)
        {
            var i = InternalChildren.IndexOf(element);
            var index = i < 0 ? -1 : ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= 0) BringIndexIntoView(index);
        }
        return rectangle;
    }
}
