namespace PhotoManager.Application.Catalog;

public enum ExternalDropAction
{
    /// <summary>Cataloga no lugar em que está (nada é copiado nem movido).</summary>
    AddToCatalog,
    Copy,
    Move,
    /// <summary>Solta numa pasta sem Ctrl/Shift: pergunta se é para copiar ou mover.</summary>
    AskCopyOrMove,
    Blocked
}

public sealed record ExternalDropPlan(
    ExternalDropAction Action,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Folders,
    int Unsupported,
    string? TargetFolder,
    string TargetLabel,
    string Message,
    bool CanDrop);

/// <summary>
/// Decide o que significa soltar arquivos/pastas vindos do Explorer: na grade, só cataloga (não mexe em nada no disco);
/// numa pasta da árvore, copia (Ctrl) ou move (Shift) — sem tecla, pergunta. Função pura: o texto de feedback também vem daqui.
/// </summary>
public static class ExternalDropPlanner
{
    public static ExternalDropPlan Plan(IReadOnlyList<string> paths, string? targetFolder, string targetLabel, bool ctrl, bool shift, Func<string, bool> isDirectory)
    {
        var folders = new List<string>();
        var files = new List<string>();
        var unsupported = 0;
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (isDirectory(path)) folders.Add(path);
            else if (MediaFormats.IsSupported(path)) files.Add(path);
            else if (!string.Equals(Path.GetExtension(path), ".xmp", StringComparison.OrdinalIgnoreCase)) unsupported++;   // sidecars acompanham a mídia
        }

        if (files.Count == 0 && folders.Count == 0)
            return Blocked(unsupported, targetFolder, targetLabel, "Nenhuma foto ou vídeo suportado entre os itens soltos.");

        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            var what = (files.Count, folders.Count) switch
            {
                (> 0, 0) => files.Count == 1 ? "1 arquivo" : $"{files.Count} arquivos",
                (0, > 0) => folders.Count == 1 ? "1 pasta" : $"{folders.Count} pastas",
                _ => $"{files.Count} arquivo(s) e {folders.Count} pasta(s)"
            };
            return new ExternalDropPlan(ExternalDropAction.AddToCatalog, files, folders, unsupported, null, string.Empty, $"+ Adicionar {what} ao catálogo", true);
        }

        if (files.Count == 0)
            return Blocked(unsupported, targetFolder, targetLabel, "Para adicionar pastas inteiras, solte na grade; numa pasta da árvore só se copiam/movem arquivos.");

        var here = files.Where(f => !string.Equals(Path.GetDirectoryName(f)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), targetFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)).ToList();
        if (here.Count == 0)
            return new ExternalDropPlan(ExternalDropAction.Blocked, [], folders, unsupported, targetFolder, targetLabel, "Os arquivos já estão nesta pasta.", false);

        var count = here.Count == 1 ? "1 arquivo" : $"{here.Count} arquivos";
        var ignored = folders.Count > 0 ? $" ({folders.Count} pasta(s) ignorada(s))" : string.Empty;
        if (ctrl) return new ExternalDropPlan(ExternalDropAction.Copy, here, folders, unsupported, targetFolder, targetLabel, $"+ Copiar {count} para a pasta “{targetLabel}”{ignored}", true);
        if (shift) return new ExternalDropPlan(ExternalDropAction.Move, here, folders, unsupported, targetFolder, targetLabel, $"→ Mover {count} para a pasta “{targetLabel}”{ignored}", true);
        return new ExternalDropPlan(ExternalDropAction.AskCopyOrMove, here, folders, unsupported, targetFolder, targetLabel, $"Solte para copiar ou mover {count} para “{targetLabel}” (Ctrl = copiar, Shift = mover)", true);
    }

    private static ExternalDropPlan Blocked(int unsupported, string? folder, string label, string message) =>
        new(ExternalDropAction.Blocked, [], [], unsupported, folder, label, message, false);
}
