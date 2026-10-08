namespace PhotoManager.Application.Collections;

public enum DropAction
{
    Add,
    Move,
    AskMenu,
    Blocked,
    NoOp
}

public sealed record AskMenuOption(
    string Header,
    DropAction Action,
    long? SourceCollectionId,
    string? SourceCollectionName);

public sealed record DropPlan(
    DropAction Action,
    long? TargetCollectionId,
    string? TargetCollectionName,
    long? SourceCollectionId,
    string? SourceCollectionName,
    IReadOnlyList<long> PhotoIdsToAdd,
    IReadOnlyList<long> PhotoIdsAlreadyInTarget,
    IReadOnlyList<long> PhotoIdsToMove,
    IReadOnlyList<AskMenuOption> AskMenuOptions,
    string Message,
    bool CanDrop);

public sealed record DropPlanRequest(
    IReadOnlyList<long> PhotoIds,
    long? ActiveSourceCollectionId,
    long? TargetCollectionId,
    string? TargetCollectionName,
    bool TargetIsVirtual,
    bool IsTargetHeaderOrEmpty,
    bool CopyPressed,
    IReadOnlyDictionary<long, IReadOnlyList<long>> PhotosCollectionsMap,
    IReadOnlyDictionary<long, string> CollectionNames);

public static class DropPlanner
{
    public static DropPlan Plan(DropPlanRequest request)
    {
        if (request.PhotoIds.Count == 0)
        {
            return new DropPlan(
                DropAction.Blocked,
                request.TargetCollectionId,
                request.TargetCollectionName,
                null,
                null,
                [],
                [],
                [],
                [],
                "Nenhuma foto selecionada.",
                false);
        }

        if (request.TargetIsVirtual)
        {
            return new DropPlan(
                DropAction.Blocked,
                null,
                null,
                null,
                null,
                [],
                [],
                [],
                [],
                "Não é possível soltar em itens virtuais (“Todas” / “Sem coleção”).",
                false);
        }

        if (request.IsTargetHeaderOrEmpty || !request.TargetCollectionId.HasValue)
        {
            return new DropPlan(
                DropAction.Blocked,
                null,
                null,
                null,
                null,
                [],
                [],
                [],
                [],
                "Destino inválido: selecione uma coleção existente.",
                false);
        }

        var targetId = request.TargetCollectionId.Value;
        var targetName = !string.IsNullOrWhiteSpace(request.TargetCollectionName)
            ? request.TargetCollectionName
            : (request.CollectionNames.TryGetValue(targetId, out var name) ? name : "Coleção");

        var distinctPhotoIds = request.PhotoIds.Distinct().ToList();

        // 1. Sem Ctrl/Shift = MOVER (padrão); Ctrl ou Shift = COPIAR (adicionar sem tirar da origem)
        if (!request.CopyPressed)
        {
            if (request.ActiveSourceCollectionId.HasValue)
            {
                var sourceId = request.ActiveSourceCollectionId.Value;
                if (sourceId == targetId)
                {
                    return new DropPlan(
                        DropAction.NoOp,
                        targetId,
                        targetName,
                        sourceId,
                        targetName,
                        [],
                        [],
                        [],
                        [],
                        "A foto já está nesta coleção.",
                        true);
                }

                var sourceName = request.CollectionNames.TryGetValue(sourceId, out var sName) ? sName : "Coleção de origem";
                var moveMsg = distinctPhotoIds.Count == 1
                    ? $"→ Mover 1 foto de “{sourceName}” para “{targetName}”"
                    : $"→ Mover {distinctPhotoIds.Count} fotos de “{sourceName}” para “{targetName}”";

                return new DropPlan(
                    DropAction.Move,
                    targetId,
                    targetName,
                    sourceId,
                    sourceName,
                    [],
                    [],
                    distinctPhotoIds,
                    [],
                    moveMsg,
                    true);
            }

            // Origem ambígua (mover, mas nenhuma coleção real ativa na sidebar): só há o que perguntar se as fotos têm coleções em comum; senão é só adicionar
            IEnumerable<long>? common = null;
            foreach (var pid in distinctPhotoIds)
            {
                var list = request.PhotosCollectionsMap.TryGetValue(pid, out var cols) ? cols : [];
                if (common == null)
                    common = list;
                else
                    common = common.Intersect(list);
            }

            var commonCollections = (common?.Where(c => c != targetId).Distinct() ?? []).ToList();

            if (commonCollections.Count > 0)
            {
            var menuOptions = new List<AskMenuOption>
            {
                new($"Adicionar à coleção “{targetName}”", DropAction.Add, null, null)
            };

            foreach (var cId in commonCollections)
            {
                var cName = request.CollectionNames.TryGetValue(cId, out var cn) ? cn : $"Coleção #{cId}";
                menuOptions.Add(new($"Mover de “{cName}” para “{targetName}”", DropAction.Move, cId, cName));
            }

            menuOptions.Add(new("Cancelar", DropAction.NoOp, null, null));

            return new DropPlan(
                DropAction.AskMenu,
                targetId,
                targetName,
                null,
                null,
                distinctPhotoIds,
                [],
                [],
                menuOptions,
                $"Solte para escolher a ação em “{targetName}”",
                true);
            }
        }

        // 2. Ctrl/Shift = Copiar (adicionar); também é o que sobra quando não há coleção de origem de onde mover
        var toAdd = new List<long>();
        var alreadyIn = new List<long>();

        foreach (var pid in distinctPhotoIds)
        {
            var existing = request.PhotosCollectionsMap.TryGetValue(pid, out var list) ? list : [];
            if (existing.Contains(targetId))
                alreadyIn.Add(pid);
            else
                toAdd.Add(pid);
        }

        if (toAdd.Count == 0)
        {
            var msg = distinctPhotoIds.Count == 1
                ? $"A foto já pertence à coleção “{targetName}”."
                : $"Todas as {distinctPhotoIds.Count} fotos já pertencem à coleção “{targetName}”.";

            return new DropPlan(
                DropAction.NoOp,
                targetId,
                targetName,
                null,
                null,
                toAdd,
                alreadyIn,
                [],
                [],
                msg,
                true);
        }

        string addMsg;
        if (alreadyIn.Count == 0)
        {
            addMsg = toAdd.Count == 1
                ? $"+ Copiar 1 foto para “{targetName}”"
                : $"+ Copiar {toAdd.Count} fotos para “{targetName}”";
        }
        else
        {
            addMsg = $"+ Copiar {toAdd.Count} foto(s) para “{targetName}” ({alreadyIn.Count} já pertence(m))";
        }

        return new DropPlan(
            DropAction.Add,
            targetId,
            targetName,
            null,
            null,
            toAdd,
            alreadyIn,
            [],
            [],
            addMsg,
            true);
    }
}

public static class PhotoSelectionDragHelper
{
    public static IReadOnlyList<long> ResolvePhotoIdsToDrag(long clickedPhotoId, IReadOnlyList<long> selectedPhotoIds)
    {
        if (selectedPhotoIds.Contains(clickedPhotoId))
        {
            return selectedPhotoIds.Distinct().ToList();
        }
        return [clickedPhotoId];
    }
}
