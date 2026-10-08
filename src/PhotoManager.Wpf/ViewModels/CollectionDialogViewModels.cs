using System.Collections.ObjectModel;
using PhotoManager.Application.Collections;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Wpf.ViewModels;

/// <summary>
/// Item para exibição de coleção em chips, listas de seleção e comboboxes.
/// </summary>
public sealed class CollectionItemViewModel : ViewModelBase
{
    private bool _isEnabled = true;
    private string? _disabledReason;

    public CollectionItemViewModel(long id, string name, string path, long? parentId = null)
    {
        Id = id;
        Name = name;
        Path = path;
        ParentId = parentId;
    }

    public long Id { get; }
    public string Name { get; }
    public string Path { get; }
    public long? ParentId { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (_isEnabled == value) return; _isEnabled = value; OnPropertyChanged(); }
    }

    public string? DisabledReason
    {
        get => _disabledReason;
        set { if (_disabledReason == value) return; _disabledReason = value; OnPropertyChanged(); }
    }

    public override string ToString() => Path;
}

/// <summary>
/// ViewModel para diálogos de criação (raiz ou subcoleção) e renomeação.
/// Realiza validação em tempo real de tamanho, preenchimento e conflito com irmãos.
/// </summary>
public sealed class CollectionNameViewModel : ViewModelBase
{
    private string _name = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _isValid;

    public CollectionNameViewModel(
        string title,
        string initialName,
        IReadOnlyList<string> existingSiblingNames,
        string? currentName = null,
        string confirmButtonText = "Salvar")
    {
        Title = title;
        ExistingSiblingNames = existingSiblingNames;
        CurrentName = currentName;
        ConfirmButtonText = confirmButtonText;
        _name = initialName ?? string.Empty;
        Validate();
    }

    public string Title { get; }
    public IReadOnlyList<string> ExistingSiblingNames { get; }
    public string? CurrentName { get; }
    public string ConfirmButtonText { get; }

    public string Name
    {
        get => _name;
        set
        {
            _name = value ?? string.Empty;
            OnPropertyChanged();
            Validate();
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    public bool IsValid
    {
        get => _isValid;
        private set { _isValid = value; OnPropertyChanged(); }
    }

    private void Validate()
    {
        var trimmed = _name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            ErrorMessage = "O nome não pode ser vazio.";
            IsValid = false;
            return;
        }

        if (trimmed.Length > 100)
        {
            ErrorMessage = "O nome deve ter no máximo 100 caracteres.";
            IsValid = false;
            return;
        }

        var isSameAsCurrent = CurrentName is not null && string.Equals(trimmed, CurrentName.Trim(), StringComparison.OrdinalIgnoreCase);

        if (!isSameAsCurrent && ExistingSiblingNames.Any(s => string.Equals(s.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = "Já existe uma coleção com este nome neste nível.";
            IsValid = false;
            return;
        }

        if (isSameAsCurrent && string.Equals(trimmed, CurrentName, StringComparison.Ordinal))
        {
            ErrorMessage = string.Empty;
            IsValid = false; // nada mudou
            return;
        }

        ErrorMessage = string.Empty;
        IsValid = true;
    }
}

/// <summary>
/// ViewModel para o diálogo de exclusão de coleção.
/// Se não tiver subcoleções, exibe confirmação direta.
/// Se tiver subcoleções, permite escolher entre promover filhos ou excluir subárvore,
/// sempre com aviso explícito de que nenhum arquivo físico será excluído.
/// </summary>
public sealed class DeleteCollectionViewModel : ViewModelBase
{
    private DeleteMode _selectedMode = DeleteMode.PromoteChildren;

    public DeleteCollectionViewModel(
        long collectionId,
        string collectionName,
        int directCount,
        int subcollectionCount,
        int totalDescendantRelationsCount)
    {
        CollectionId = collectionId;
        CollectionName = collectionName;
        DirectCount = directCount;
        SubcollectionCount = subcollectionCount;
        TotalDescendantRelationsCount = totalDescendantRelationsCount;
    }

    public long CollectionId { get; }
    public string CollectionName { get; }
    public int DirectCount { get; }
    public int SubcollectionCount { get; }
    public int TotalDescendantRelationsCount { get; }

    public bool HasSubcollections => SubcollectionCount > 0;
    public bool HasNoSubcollections => SubcollectionCount == 0;

    public DeleteMode SelectedMode
    {
        get => _selectedMode;
        set { _selectedMode = value; OnPropertyChanged(); }
    }

    public bool IsPromoteSelected
    {
        get => _selectedMode == DeleteMode.PromoteChildren;
        set { if (value) SelectedMode = DeleteMode.PromoteChildren; }
    }

    public bool IsWithDescendantsSelected
    {
        get => _selectedMode == DeleteMode.WithDescendants;
        set { if (value) SelectedMode = DeleteMode.WithDescendants; }
    }

    public string SingleCollectionMessage =>
        $"Excluir a coleção «{CollectionName}»?\n\nAs {DirectCount} foto(s) associadas NÃO serão excluídas; elas apenas deixam de pertencer a esta coleção.";

    public string HeaderSummary =>
        $"A coleção «{CollectionName}» possui {SubcollectionCount} subcoleção(ões) e {DirectCount} foto(s) direta(s).";

    public static string SafetyWarning => "Nenhum arquivo físico será excluído.";
}

/// <summary>
/// ViewModel para o diálogo de mover coleção.
/// Exclui como destino a própria coleção e todos os seus descendentes.
/// </summary>
public sealed class MoveCollectionViewModel : ViewModelBase
{
    private CollectionDestinationOption? _selectedDestination;

    public MoveCollectionViewModel(CollectionNode sourceNode, IReadOnlyList<CollectionNode> allRoots)
    {
        SourceNode = sourceNode;
        Destinations = BuildDestinations(sourceNode, allRoots);
        _selectedDestination = Destinations.FirstOrDefault(d => d.IsEnabled && d.ParentId != sourceNode.ParentId);
    }

    public CollectionNode SourceNode { get; }
    public string CollectionName => SourceNode.Name;
    public ObservableCollection<CollectionDestinationOption> Destinations { get; }

    public CollectionDestinationOption? SelectedDestination
    {
        get => _selectedDestination;
        set
        {
            _selectedDestination = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanMove));
        }
    }

    public bool CanMove => SelectedDestination is { IsEnabled: true } && SelectedDestination.ParentId != SourceNode.ParentId;

    public long? TargetParentId => SelectedDestination?.ParentId;

    private static ObservableCollection<CollectionDestinationOption> BuildDestinations(
        CollectionNode sourceNode,
        IReadOnlyList<CollectionNode> roots)
    {
        var invalidIds = sourceNode.SelfAndDescendants().Select(n => n.Id).ToHashSet();
        var list = new ObservableCollection<CollectionDestinationOption>();

        // Opção especial: Raiz
        var isCurrentRoot = sourceNode.ParentId == null;
        list.Add(new CollectionDestinationOption(
            null,
            "(Raiz do catálogo)",
            "(Raiz)",
            IsEnabled: !isCurrentRoot,
            DisabledReason: isCurrentRoot ? "Já está na raiz" : null));

        void AddTree(CollectionNode node, int level)
        {
            var isSelfOrDescendant = invalidIds.Contains(node.Id);
            var isCurrentParent = sourceNode.ParentId == node.Id;
            var isEnabled = !isSelfOrDescendant && !isCurrentParent;
            string? reason = null;
            if (node.Id == sourceNode.Id) reason = "A própria coleção";
            else if (isSelfOrDescendant) reason = "Subcoleção da coleção a ser movida";
            else if (isCurrentParent) reason = "Pai atual";

            var indent = new string(' ', level * 4);
            list.Add(new CollectionDestinationOption(
                node.Id,
                $"{indent}{node.Name}",
                node.Path,
                IsEnabled: isEnabled,
                DisabledReason: reason));

            foreach (var child in node.Children)
                AddTree(child, level + 1);
        }

        foreach (var root in roots)
            AddTree(root, 1);

        return list;
    }
}

public sealed record CollectionDestinationOption(
    long? ParentId,
    string IndentedLabel,
    string FullPath,
    bool IsEnabled,
    string? DisabledReason = null)
{
    public bool IsRootOption => ParentId == null;
    public long? Id => ParentId;
}
