using PhotoManager.Domain.Collections;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Collections;

public sealed class CollectionService(ICollectionRepository repository) : ICollectionService
{
    public async Task<CollectionTree> GetTreeAsync(CancellationToken cancellationToken = default)
    {
        var all = await repository.GetAllAsync(cancellationToken);
        var directCounts = await repository.GetDirectCountsAsync(cancellationToken);
        var totalDistinct = await repository.GetTotalDistinctInAnyCollectionAsync(cancellationToken);
        var totalPhotos = await repository.GetTotalPhotosCountAsync(cancellationToken);
        var withoutCollection = Math.Max(0, totalPhotos - totalDistinct);

        var byParent = all.ToLookup(c => c.ParentCollectionId);

        List<CollectionTreeNode> BuildNodes(long? parentId)
        {
            var children = byParent[parentId].OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var list = new List<CollectionTreeNode>();
            foreach (var item in children)
            {
                var count = directCounts.TryGetValue(item.Id, out var c) ? c : 0;
                var subChildren = BuildNodes(item.Id);
                list.Add(new CollectionTreeNode(item.Id, item.Name, item.ParentCollectionId, item.SortOrder, count, subChildren));
            }
            return list;
        }

        var roots = BuildNodes(null);
        return new CollectionTree(roots, totalDistinct, withoutCollection);
    }

    public async Task<Collection> CreateAsync(string name, long? parentId, CancellationToken cancellationToken = default)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("O nome da coleção não pode ser vazio.", nameof(name));
        if (trimmed.Length > 100)
            throw new ArgumentException("O nome da coleção não pode ter mais de 100 caracteres.", nameof(name));
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException("O nome da coleção não pode conter caracteres de controle.", nameof(name));

        if (parentId.HasValue)
        {
            var parent = await repository.GetByIdAsync(parentId.Value, cancellationToken)
                ?? throw new InvalidOperationException("Coleção pai não encontrada.");
            var parentDepth = await GetDepthAsync(parentId.Value, cancellationToken);
            if (parentDepth >= 8)
                throw new InvalidOperationException("A profundidade máxima de coleções é de 8 níveis.");
        }

        var siblings = await repository.GetChildrenAsync(parentId, cancellationToken);
        if (siblings.Any(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Já existe uma coleção com este nome neste nível.");

        var nextOrder = siblings.Count > 0 ? siblings.Max(s => s.SortOrder) + 1 : 0;
        var collection = new Collection
        {
            Name = trimmed,
            ParentCollectionId = parentId,
            SortOrder = nextOrder,
            CreatedAt = DateTime.UtcNow
        };

        return await repository.InsertAsync(collection, cancellationToken);
    }

    public async Task<Collection> RenameAsync(long id, string newName, CancellationToken cancellationToken = default)
    {
        var trimmed = newName?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("O novo nome não pode ser vazio.", nameof(newName));
        if (trimmed.Length > 100)
            throw new ArgumentException("O novo nome não pode ter mais de 100 caracteres.", nameof(newName));
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException("O nome da coleção não pode conter caracteres de controle.", nameof(newName));

        var collection = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Coleção não encontrada.");

        if (string.Equals(collection.Name, trimmed, StringComparison.Ordinal))
            return collection;

        var siblings = await repository.GetChildrenAsync(collection.ParentCollectionId, cancellationToken);
        if (siblings.Any(s => s.Id != id && string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Já existe uma coleção com este nome neste nível.");

        collection.Name = trimmed;
        await repository.UpdateAsync(collection, cancellationToken);
        return collection;
    }

    public async Task<MoveResult> MoveAsync(long id, long? newParentId, CancellationToken cancellationToken = default)
    {
        var collection = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Coleção não encontrada.");

        if (id == newParentId)
            return new MoveResult(false, "Uma coleção não pode ser movida para dentro de si mesma.");

        if (collection.ParentCollectionId == newParentId)
            return new MoveResult(true, "A coleção já está neste local.");

        if (newParentId.HasValue)
        {
            var targetParent = await repository.GetByIdAsync(newParentId.Value, cancellationToken);
            if (targetParent is null)
                return new MoveResult(false, "Coleção pai de destino não encontrada.");

            if (await IsDescendantAsync(newParentId.Value, id, cancellationToken))
                return new MoveResult(false, "Ciclo detectado: não é permitido mover uma coleção para dentro de seus próprios descendentes.");
        }

        var targetDepth = newParentId.HasValue ? await GetDepthAsync(newParentId.Value, cancellationToken) : 0;
        var subtreeHeight = await GetSubtreeHeightAsync(id, cancellationToken);
        if (targetDepth + subtreeHeight > 8)
            return new MoveResult(false, "A operação excede a profundidade máxima permitida de 8 níveis.");

        var targetSiblings = await repository.GetChildrenAsync(newParentId, cancellationToken);
        if (targetSiblings.Any(s => s.Id != id && string.Equals(s.Name, collection.Name, StringComparison.OrdinalIgnoreCase)))
            return new MoveResult(false, "Já existe uma coleção com este nome no nível de destino.");

        collection.ParentCollectionId = newParentId;
        collection.SortOrder = targetSiblings.Count > 0 ? targetSiblings.Max(s => s.SortOrder) + 1 : 0;
        await repository.UpdateAsync(collection, cancellationToken);
        return new MoveResult(true);
    }

    public async Task<DeleteResult> DeleteAsync(long id, DeleteMode mode, CancellationToken cancellationToken = default)
    {
        var collection = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Coleção não encontrada.");

        if (mode == DeleteMode.PromoteChildren)
        {
            var children = await repository.GetChildrenAsync(id, cancellationToken);
            var targetSiblings = await repository.GetChildrenAsync(collection.ParentCollectionId, cancellationToken);

            var renamedChildren = new List<(long Id, string OldName, string NewName)>();
            var takenNames = targetSiblings
                .Where(s => s.Id != id)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var child in children)
            {
                if (takenNames.Contains(child.Name))
                {
                    var suffix = 2;
                    string candidate;
                    do
                    {
                        candidate = $"{child.Name} ({suffix})";
                        suffix++;
                    } while (takenNames.Contains(candidate));

                    renamedChildren.Add((child.Id, child.Name, candidate));
                    takenNames.Add(candidate);
                }
                else
                {
                    takenNames.Add(child.Name);
                }
            }

            return await repository.DeleteHierarchyAsync(id, DeleteMode.PromoteChildren, renamedChildren, cancellationToken);
        }

        return await repository.DeleteHierarchyAsync(id, DeleteMode.WithDescendants, [], cancellationToken);
    }

    public async Task<int> AddPhotosAsync(long collectionId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default)
    {
        var collection = await repository.GetByIdAsync(collectionId, cancellationToken)
            ?? throw new InvalidOperationException("Coleção não encontrada.");
        if (photoIds.Count == 0) return 0;
        return await repository.AddPhotosAsync(collectionId, photoIds, cancellationToken);
    }

    public async Task<int> RemovePhotosAsync(long collectionId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default)
    {
        var collection = await repository.GetByIdAsync(collectionId, cancellationToken)
            ?? throw new InvalidOperationException("Coleção não encontrada.");
        if (photoIds.Count == 0) return 0;
        return await repository.RemovePhotosAsync(collectionId, photoIds, cancellationToken);
    }

    public async Task<MovePhotosResult> MovePhotosAsync(long fromId, long toId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default)
    {
        if (fromId == toId) return new MovePhotosResult(0, 0);
        var from = await repository.GetByIdAsync(fromId, cancellationToken)
            ?? throw new InvalidOperationException("Coleção de origem não encontrada.");
        var to = await repository.GetByIdAsync(toId, cancellationToken)
            ?? throw new InvalidOperationException("Coleção de destino não encontrada.");
        if (photoIds.Count == 0) return new MovePhotosResult(0, 0);
        return await repository.MovePhotosAsync(fromId, toId, photoIds, cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetAncestorsAsync(long id, CancellationToken cancellationToken = default)
    {
        var list = new List<long>();
        var current = await repository.GetByIdAsync(id, cancellationToken);
        while (current?.ParentCollectionId is { } parentId)
        {
            list.Add(parentId);
            current = await repository.GetByIdAsync(parentId, cancellationToken);
        }
        return list;
    }

    public async Task<bool> IsDescendantAsync(long id, long possibleAncestorId, CancellationToken cancellationToken = default)
    {
        var current = await repository.GetByIdAsync(id, cancellationToken);
        while (current?.ParentCollectionId is { } parentId)
        {
            if (parentId == possibleAncestorId) return true;
            current = await repository.GetByIdAsync(parentId, cancellationToken);
        }
        return false;
    }

    public Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default) =>
        repository.GetAllAsync(cancellationToken);

    public Task<Collection?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        repository.GetByIdAsync(id, cancellationToken);

    public Task<bool> HasOrphansAsync(CancellationToken cancellationToken = default) =>
        repository.HasOrphansAsync(cancellationToken);

    public async Task SyncPhotoCollectionsAsync(IEnumerable<Photo> photos, CancellationToken cancellationToken = default)
    {
        var all = await repository.GetAllAsync(cancellationToken);
        var parentLookup = all.ToDictionary(c => c.Id, c => c.ParentCollectionId);
        var nameLookup = all.ToDictionary(c => c.Id, c => (c.Name, ParentName: c.ParentCollectionId.HasValue && all.FirstOrDefault(p => p.Id == c.ParentCollectionId.Value) is { } parent ? parent.Name : (string?)null));

        foreach (var photo in photos)
        {
            photo.Collections = FormatNames(photo.CollectionIds, nameLookup);
        }
    }

    private static List<string> FormatNames(IReadOnlyCollection<long> collectionIds, IReadOnlyDictionary<long, (string Name, string? ParentName)> lookup)
    {
        var list = new List<(long Id, string Name, string? ParentName)>();
        foreach (var id in collectionIds)
        {
            if (lookup.TryGetValue(id, out var info))
                list.Add((id, info.Name, info.ParentName));
        }

        var duplicates = list
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<string>();
        foreach (var item in list)
        {
            if (duplicates.Contains(item.Name) && !string.IsNullOrWhiteSpace(item.ParentName))
                result.Add($"{item.ParentName} / {item.Name}");
            else
                result.Add(item.Name);
        }
        return result;
    }

    private async Task<int> GetDepthAsync(long id, CancellationToken cancellationToken)
    {
        var depth = 1;
        var current = await repository.GetByIdAsync(id, cancellationToken);
        while (current?.ParentCollectionId is { } parentId)
        {
            depth++;
            current = await repository.GetByIdAsync(parentId, cancellationToken);
        }
        return depth;
    }

    private async Task<int> GetSubtreeHeightAsync(long id, CancellationToken cancellationToken)
    {
        var children = await repository.GetChildrenAsync(id, cancellationToken);
        if (children.Count == 0) return 1;
        var maxChildHeight = 0;
        foreach (var child in children)
        {
            var childHeight = await GetSubtreeHeightAsync(child.Id, cancellationToken);
            if (childHeight > maxChildHeight) maxChildHeight = childHeight;
        }
        return 1 + maxChildHeight;
    }
}
