namespace PhotoManager.Application.Catalog;

public enum FolderDropAction
{
    Move,
    Copy,
    NoOp,
    Blocked
}

public sealed record FolderDropPhoto(long Id, string? Folder, bool IsMissing);

/// <param name="PhotoIds">Fotos que realmente serão movidas/copiadas (já sem as que não precisam ou não podem).</param>
public sealed record FolderDropPlan(
    FolderDropAction Action,
    string? TargetFolder,
    string TargetLabel,
    IReadOnlyList<long> PhotoIds,
    int SkippedAlreadyThere,
    int SkippedMissing,
    string Message,
    bool CanDrop);

/// <summary>
/// Decide o que soltar fotos sobre uma pasta (física) significa: arrastar = MOVER o arquivo; Ctrl ou Shift = COPIAR.
/// Função pura: toda a regra e o texto de feedback ficam aqui, testáveis sem interface.
/// </summary>
public static class FolderDropPlanner
{
    public static FolderDropPlan Plan(IReadOnlyList<FolderDropPhoto> photos, string? targetFolder, string targetLabel, bool copy)
    {
        if (photos.Count == 0)
            return Blocked(targetFolder, targetLabel, "Nenhuma foto selecionada.");
        if (string.IsNullOrWhiteSpace(targetFolder))
            return Blocked(targetFolder, targetLabel, "Solte sobre uma pasta da árvore.");

        var already = 0;
        var missing = 0;
        var ids = new List<long>();
        foreach (var photo in photos.DistinctBy(p => p.Id))
        {
            if (photo.IsMissing) { missing++; continue; }
            if (string.Equals(TrimEnd(photo.Folder), TrimEnd(targetFolder), StringComparison.OrdinalIgnoreCase)) { already++; continue; }
            ids.Add(photo.Id);
        }

        if (ids.Count == 0)
        {
            var reason = already > 0 && missing == 0
                ? (already == 1 ? "A foto já está nesta pasta." : "As fotos já estão nesta pasta.")
                : missing > 0 && already == 0
                    ? "Os arquivos não estão disponíveis (ausentes)."
                    : "Nada a fazer: as fotos já estão nesta pasta ou estão ausentes.";
            return new FolderDropPlan(FolderDropAction.NoOp, targetFolder, targetLabel, [], already, missing, reason, false);
        }

        var count = ids.Count == 1 ? "1 foto" : $"{ids.Count} fotos";
        var extra = already + missing > 0 ? $" ({already + missing} ignorada(s))" : string.Empty;
        return copy
            ? new FolderDropPlan(FolderDropAction.Copy, targetFolder, targetLabel, ids, already, missing, $"+ Copiar {count} para a pasta “{targetLabel}”{extra}", true)
            : new FolderDropPlan(FolderDropAction.Move, targetFolder, targetLabel, ids, already, missing, $"→ Mover {count} para a pasta “{targetLabel}”{extra}", true);
    }

    private static FolderDropPlan Blocked(string? folder, string label, string message) =>
        new(FolderDropAction.Blocked, folder, label, [], 0, 0, message, false);

    private static string TrimEnd(string? path) => (path ?? string.Empty).TrimEnd('\\', '/');
}
