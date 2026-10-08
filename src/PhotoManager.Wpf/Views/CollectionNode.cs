using System.Collections.ObjectModel;
using PhotoManager.Domain.Collections;

namespace PhotoManager.Wpf.Views;

/// <summary>
/// Nó da árvore de coleções na barra lateral.
/// Conforme as regras do projeto, <see cref="DirectCount"/> contém SOMENTE as fotos
/// diretamente associadas a esta coleção (não inclui fotos das subcoleções).
/// </summary>
public sealed class CollectionNode : ViewModels.ViewModelBase
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isDragOver;
    private int _directCount;
    private int _subtreeCount;
    private string _name;

    public CollectionNode(long id, string name, long? parentId, string path)
    {
        Id = id;
        _name = name;
        ParentId = parentId;
        Path = path;
    }

    public long Id { get; }
    public bool IsDragOver
    {
        get => _isDragOver;
        set
        {
            if (_isDragOver == value) return;
            _isDragOver = value;
            OnPropertyChanged();
        }
    }
    public string Name
    {
        get => _name;
        internal set { if (_name == value) return; _name = value; OnPropertyChanged(); }
    }
    public long? ParentId { get; }
    public string Path { get; internal set; }

    public int DirectCount
    {
        get => _directCount;
        internal set
        {
            if (_directCount == value) return;
            _directCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    public int SubtreeCount
    {
        get => _subtreeCount;
        internal set
        {
            if (_subtreeCount == value) return;
            _subtreeCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    public string ToolTipText => IsAggregate
        ? "Fotos desta coleção e de todas as suas subcoleções"
        : SubtreeCount > 0
        ? $"{DirectCount} fotos diretas · {SubtreeCount} em subcoleções"
        : $"{DirectCount} fotos diretas";

    public ObservableCollection<CollectionNode> Children { get; } = [];

    /// <summary>Item virtual "Todas" que abre a coleção e todas as suas subcoleções. Só existe em coleções com filhos; usa o Id da coleção a que pertence.</summary>
    public bool IsAggregate { get; private init; }
    public CollectionNode? AggregateNode { get; private set; }

    /// <summary>O que a árvore mostra como filhos: o item virtual "Todas" primeiro (se houver subcoleções), depois as subcoleções.</summary>
    public ObservableCollection<CollectionNode> DisplayChildren { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            ExpansionChanged?.Invoke(this);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    internal Action<CollectionNode>? ExpansionChanged { get; set; }

    public IEnumerable<CollectionNode> SelfAndDescendants() => SelfAndDescendants(includeAggregates: false);

    public IEnumerable<CollectionNode> SelfAndDescendants(bool includeAggregates)
    {
        yield return this;
        if (includeAggregates && AggregateNode is not null) yield return AggregateNode;
        foreach (var child in Children)
            foreach (var node in child.SelfAndDescendants(includeAggregates))
                yield return node;
    }

    public static IReadOnlyList<CollectionNode> Build(
        IEnumerable<Collection> collections,
        IReadOnlyDictionary<long, int> directCounts,
        IEnumerable<long>? initiallyExpandedIds = null)
    {
        var set = initiallyExpandedIds != null ? new HashSet<long>(initiallyExpandedIds) : null;
        return Build(collections, directCounts, id => set != null && set.Contains(id), null);
    }

    public static IReadOnlyList<CollectionNode> Build(
        IEnumerable<Collection> collections,
        IReadOnlyDictionary<long, int> directCounts,
        Func<long, bool> initiallyExpanded,
        Action<CollectionNode>? expansionChanged)
    {
        var colList = collections.ToList();
        var byParent = colList.ToLookup(c => c.ParentCollectionId);

        List<CollectionNode> BuildLevel(long? parentId, string parentPath)
        {
            var items = byParent[parentId]
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)   // D5: alfabética; SortOrder fica reservado para reordenação manual futura
                .ToList();

            var result = new List<CollectionNode>();
            foreach (var col in items)
            {
                var currentPath = string.IsNullOrEmpty(parentPath) ? col.Name : $"{parentPath} / {col.Name}";
                var node = new CollectionNode(col.Id, col.Name, col.ParentCollectionId, currentPath)
                {
                    DirectCount = directCounts.TryGetValue(col.Id, out var count) ? count : 0
                };

                var children = BuildLevel(col.Id, currentPath);
                foreach (var child in children)
                {
                    node.Children.Add(child);
                }

                if (node.Children.Count > 0)
                {
                    node.AggregateNode = new CollectionNode(col.Id, "Todas", col.ParentCollectionId, currentPath) { IsAggregate = true };
                    node.DisplayChildren.Add(node.AggregateNode);
                }
                foreach (var child in children) node.DisplayChildren.Add(child);

                result.Add(node);
            }
            return result;
        }

        var roots = BuildLevel(null, string.Empty);

        void CalculateSubtree(CollectionNode node)
        {
            var sum = 0;
            foreach (var child in node.Children)
            {
                CalculateSubtree(child);
                sum += child.DirectCount + child.SubtreeCount;
            }
            node.SubtreeCount = sum;
        }

        void Wire(CollectionNode node)
        {
            node._isExpanded = initiallyExpanded(node.Id);
            node.ExpansionChanged = expansionChanged;
            foreach (var child in node.Children)
                Wire(child);
        }

        foreach (var root in roots)
        {
            CalculateSubtree(root);
            Wire(root);
        }

        return roots;
    }
}
