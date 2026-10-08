namespace PhotoManager.Application.Collections;

public enum CollectionDropAction
{
    Move,
    Blocked,
    NoOp
}

public sealed record CollectionDropPlan(
    CollectionDropAction Action,
    long DraggedCollectionId,
    string DraggedCollectionName,
    long? TargetParentId,
    string TargetParentName,
    string Message,
    bool CanDrop);

public sealed record CollectionDropPlanRequest(
    long DraggedCollectionId,
    string? DraggedCollectionName,
    bool TargetIsRoot,
    long? TargetCollectionId,
    string? TargetCollectionName,
    bool TargetIsVirtual,
    IReadOnlyDictionary<long, (string Name, long? ParentId)> TreeLookup);

public static class CollectionDropPlanner
{
    public static CollectionDropPlan Plan(CollectionDropPlanRequest request)
    {
        var draggedId = request.DraggedCollectionId;
        var draggedName = !string.IsNullOrWhiteSpace(request.DraggedCollectionName)
            ? request.DraggedCollectionName
            : (request.TreeLookup.TryGetValue(draggedId, out var dInfo) ? dInfo.Name : $"Coleção #{draggedId}");

        if (request.TargetIsVirtual)
        {
            return new CollectionDropPlan(
                CollectionDropAction.Blocked,
                draggedId,
                draggedName,
                null,
                string.Empty,
                "Não é possível mover coleções para itens virtuais (“Todas” / “Sem coleção”).",
                false);
        }

        if (!request.TargetIsRoot && !request.TargetCollectionId.HasValue)
        {
            return new CollectionDropPlan(
                CollectionDropAction.Blocked,
                draggedId,
                draggedName,
                null,
                string.Empty,
                "Destino inválido: selecione uma coleção existente ou arraste para o cabeçalho COLEÇÕES.",
                false);
        }

        request.TreeLookup.TryGetValue(draggedId, out var draggedInfo);

        // 1. Mover para a Raiz
        if (request.TargetIsRoot)
        {
            if (draggedInfo.ParentId == null)
            {
                return new CollectionDropPlan(
                    CollectionDropAction.NoOp,
                    draggedId,
                    draggedName,
                    null,
                    "Raiz",
                    "A coleção já está na raiz.",
                    true);
            }

            var rootSiblings = request.TreeLookup
                .Where(kv => kv.Key != draggedId && kv.Value.ParentId == null)
                .Select(kv => kv.Value.Name);

            if (rootSiblings.Any(name => string.Equals(name, draggedName, StringComparison.OrdinalIgnoreCase)))
            {
                return new CollectionDropPlan(
                    CollectionDropAction.Blocked,
                    draggedId,
                    draggedName,
                    null,
                    "Raiz",
                    $"Já existe uma coleção chamada “{draggedName}” na raiz.",
                    false);
            }

            return new CollectionDropPlan(
                CollectionDropAction.Move,
                draggedId,
                draggedName,
                null,
                "Raiz",
                $"Mover “{draggedName}” para a raiz",
                true);
        }

        // 2. Mover para outra coleção (TargetCollectionId)
        var targetId = request.TargetCollectionId!.Value;
        var targetName = !string.IsNullOrWhiteSpace(request.TargetCollectionName)
            ? request.TargetCollectionName
            : (request.TreeLookup.TryGetValue(targetId, out var tInfo) ? tInfo.Name : $"Coleção #{targetId}");

        if (draggedId == targetId)
        {
            return new CollectionDropPlan(
                CollectionDropAction.Blocked,
                draggedId,
                draggedName,
                targetId,
                targetName,
                "Não é possível mover uma coleção para dentro de si mesma.",
                false);
        }

        if (draggedInfo.ParentId == targetId)
        {
            return new CollectionDropPlan(
                CollectionDropAction.NoOp,
                draggedId,
                draggedName,
                targetId,
                targetName,
                "A coleção já está neste local.",
                true);
        }

        // Verificar ciclo: targetId é descendente de draggedId?
        var curr = (long?)targetId;
        while (curr.HasValue)
        {
            if (curr.Value == draggedId)
            {
                return new CollectionDropPlan(
                    CollectionDropAction.Blocked,
                    draggedId,
                    draggedName,
                    targetId,
                    targetName,
                    "Ciclo detectado: não é permitido mover uma coleção para dentro de seus próprios descendentes.",
                    false);
            }

            curr = request.TreeLookup.TryGetValue(curr.Value, out var parentLookup) ? parentLookup.ParentId : null;
        }

        // Verificar profundidade máxima (8 níveis)
        var targetDepth = 1;
        var depthCurr = request.TreeLookup.TryGetValue(targetId, out var tLookup) ? tLookup.ParentId : null;
        while (depthCurr.HasValue)
        {
            targetDepth++;
            depthCurr = request.TreeLookup.TryGetValue(depthCurr.Value, out var pLookup) ? pLookup.ParentId : null;
        }

        int GetSubtreeHeight(long id)
        {
            var children = request.TreeLookup.Where(kv => kv.Value.ParentId == id).Select(kv => kv.Key).ToList();
            if (children.Count == 0) return 1;
            return 1 + children.Max(GetSubtreeHeight);
        }

        var subtreeHeight = GetSubtreeHeight(draggedId);
        if (targetDepth + subtreeHeight > 8)
        {
            return new CollectionDropPlan(
                CollectionDropAction.Blocked,
                draggedId,
                draggedName,
                targetId,
                targetName,
                "A operação excede a profundidade máxima permitida de 8 níveis.",
                false);
        }

        // Verificar conflito de irmãos homônimos
        var targetSiblings = request.TreeLookup
            .Where(kv => kv.Key != draggedId && kv.Value.ParentId == targetId)
            .Select(kv => kv.Value.Name);

        if (targetSiblings.Any(name => string.Equals(name, draggedName, StringComparison.OrdinalIgnoreCase)))
        {
            return new CollectionDropPlan(
                CollectionDropAction.Blocked,
                draggedId,
                draggedName,
                targetId,
                targetName,
                $"Já existe uma coleção chamada “{draggedName}” em “{targetName}”.",
                false);
        }

        return new CollectionDropPlan(
            CollectionDropAction.Move,
            draggedId,
            draggedName,
            targetId,
            targetName,
            $"Mover “{draggedName}” para “{targetName}”",
            true);
    }
}
