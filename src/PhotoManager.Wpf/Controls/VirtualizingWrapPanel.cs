using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PhotoManager.Wpf.Controls;

/// <summary>
/// Painel de grade com virtualização para itens de tamanho fixo: só materializa os containers visíveis (mais uma linha de folga).
/// Necessário porque o WrapPanel padrão cria um container (e carrega uma miniatura) por foto do catálogo.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(200d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(200d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _firstIndex;
    private int _itemsPerRow = 1;

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? ItemWidth : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? ItemHeight * 4 : availableSize.Height;
        var count = ItemCount;
        _itemsPerRow = Math.Max(1, (int)(width / ItemWidth));
        var rows = (int)Math.Ceiling(count / (double)_itemsPerRow);
        UpdateScrollInfo(new Size(width, height), new Size(_itemsPerRow * ItemWidth, rows * ItemHeight));

        if (count == 0) { CleanUp(0, -1); return new Size(width, height); }

        var firstRow = Math.Max(0, (int)(_offset.Y / ItemHeight) - 1);
        var lastRow = Math.Min(rows - 1, (int)Math.Ceiling((_offset.Y + height) / ItemHeight));
        _firstIndex = firstRow * _itemsPerRow;
        var lastIndex = Math.Min(count - 1, (lastRow + 1) * _itemsPerRow - 1);

        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(_firstIndex);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (var index = _firstIndex; index <= lastIndex; index++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var isNew);
                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }

        CleanUp(_firstIndex, lastIndex);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            var row = index / _itemsPerRow;
            var column = index % _itemsPerRow;
            InternalChildren[i].Arrange(new Rect(column * ItemWidth - _offset.X, row * ItemHeight - _offset.Y, ItemWidth, ItemHeight));
        }
        return finalSize;
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
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= ItemCount) return;
        var top = index / _itemsPerRow * ItemHeight;
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (top + ItemHeight > _offset.Y + _viewport.Height) SetVerticalOffset(top + ItemHeight - _viewport.Height);
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        var changed = viewport != _viewport || extent != _extent;
        _viewport = viewport;
        _extent = extent;
        var maxOffset = Math.Max(0, extent.Height - viewport.Height);
        if (_offset.Y > maxOffset) _offset.Y = maxOffset;
        if (changed) ScrollOwner?.InvalidateScrollInfo();
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
        if (offset == _offset.Y) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset(_offset.Y - ItemHeight / 4);
    public void LineDown() => SetVerticalOffset(_offset.Y + ItemHeight / 4);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - ItemHeight / 2);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + ItemHeight / 2);
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
