using System.Collections.ObjectModel;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Wpf.Views;

/// <summary>Nó da árvore de pastas da barra lateral. <see cref="Count"/> inclui as subpastas.</summary>
public sealed class FolderNode(string path, string label) : ViewModels.ViewModelBase
{
    private bool _isExpanded, _isSelected;

    public string Path { get; } = path;
    public string Label { get; } = label;
    public int OwnCount { get; internal set; }
    private bool _isDragOver;
    /// <summary>Realce enquanto fotos são arrastadas sobre a pasta.</summary>
    public bool IsDragOver { get => _isDragOver; set { if (_isDragOver == value) return; _isDragOver = value; OnPropertyChanged(); } }
    public int Count { get; internal set; }
    public ObservableCollection<FolderNode> Children { get; } = [];

    /// <summary>O que a árvore mostra como filhos: igual a <see cref="Children"/>, exceto numa pasta inacessível, cujo conteúdo não é listado.</summary>
    public ObservableCollection<FolderNode> DisplayChildren { get; } = [];

    private bool _isAccessible = true;
    /// <summary>Falso quando a pasta não existe mais ou não pode ser aberta (disco desconectado, rede fora, pasta excluída).</summary>
    public bool IsAccessible
    {
        get => _isAccessible;
        set
        {
            if (_isAccessible == value) return;
            _isAccessible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateToolTip));
            SyncDisplayChildren();
        }
    }

    public string StateToolTip => _isAccessible ? Path : $"Pasta inacessível (não existe ou está desconectada):{Environment.NewLine}{Path}";

    internal void SyncDisplayChildren()
    {
        DisplayChildren.Clear();
        if (_isAccessible) foreach (var child in Children) DisplayChildren.Add(child);
    }
    public bool IsExpanded { get => _isExpanded; set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); ExpansionChanged?.Invoke(this); } }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); } }
    internal Action<FolderNode>? ExpansionChanged { get; set; }

    public IEnumerable<FolderNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.SelfAndDescendants()) yield return node;
    }

    /// <summary>
    /// Monta a ramificação a partir das pastas das fotos. A cadeia inicial de pastas sem fotos próprias e com um único filho é recolhida
    /// (ex.: <c>D:\Fotos\2026</c> aparece como uma raiz só), e cada pasta mostra o total com subpastas.
    /// </summary>
    public static IReadOnlyList<FolderNode> Build(IEnumerable<Photo> photos, Func<string, int, bool> initiallyExpanded, Action<FolderNode> expansionChanged)
    {
        var own = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var photo in photos)
        {
            var directory = System.IO.Path.GetDirectoryName(photo.CurrentPath);
            if (!string.IsNullOrEmpty(directory)) own[directory] = own.GetValueOrDefault(directory) + 1;
        }

        var nodes = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<FolderNode>();
        FolderNode Ensure(string path)
        {
            if (nodes.TryGetValue(path, out var existing)) return existing;
            var parentPath = System.IO.Path.GetDirectoryName(path);
            var label = System.IO.Path.GetFileName(path);
            var node = new FolderNode(path, label.Length > 0 ? label : path);
            nodes[path] = node;
            if (string.IsNullOrEmpty(parentPath)) roots.Add(node); else Ensure(parentPath).Children.Add(node);
            return node;
        }
        foreach (var (directory, count) in own) Ensure(directory).OwnCount = count;

        void Total(FolderNode node) { foreach (var child in node.Children) Total(child); node.Count = node.OwnCount + node.Children.Sum(c => c.Count); }
        foreach (var root in roots) Total(root);

        var result = new List<FolderNode>();
        foreach (var root in roots.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            var top = root;
            while (top.OwnCount == 0 && top.Children.Count == 1) top = top.Children[0];
            result.Add(top.WithPathLabel());
        }

        foreach (var root in result)
        {
            Sort(root);
            Wire(root, 0);
        }
        foreach (var root in result) foreach (var node in root.SelfAndDescendants()) node.SyncDisplayChildren();
        return result;

        static void Sort(FolderNode node)
        {
            var ordered = node.Children.OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            node.Children.Clear();
            foreach (var child in ordered) { node.Children.Add(child); Sort(child); }
        }
        void Wire(FolderNode node, int depth)
        {
            node._isExpanded = initiallyExpanded(node.Path, depth);
            node.ExpansionChanged = expansionChanged;
            foreach (var child in node.Children) Wire(child, depth + 1);
        }
    }

    private static string ShortPath(string path)
    {
        var parts = path.TrimEnd(System.IO.Path.DirectorySeparatorChar).Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? path : "…" + System.IO.Path.DirectorySeparatorChar + string.Join(System.IO.Path.DirectorySeparatorChar, parts[^2..]);
    }

    /// <summary>Raízes mostram o caminho completo (é o que identifica a pasta importada); filhos mostram só o nome.</summary>
    private FolderNode WithPathLabel()
    {
        var root = new FolderNode(Path, ShortPath(Path)) { OwnCount = OwnCount, Count = Count };
        foreach (var child in Children) root.Children.Add(child);
        return root;
    }
}
