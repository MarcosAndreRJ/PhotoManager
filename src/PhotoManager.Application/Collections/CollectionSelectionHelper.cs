namespace PhotoManager.Application.Collections;

public static class CollectionSelectionHelper
{
    /// <summary>
    /// Se TODAS as fotos têm exatamente o mesmo conjunto de coleções (e ele não é vazio), devolve esse conjunto; senão, vazio.
    /// É o caso em que faz sentido oferecer "remover de todas" no painel de lote.
    /// </summary>
    public static IReadOnlyList<long> CommonWhenIdentical(IEnumerable<IReadOnlyCollection<long>> collectionIdsPerPhoto)
    {
        HashSet<long>? first = null;
        var count = 0;
        foreach (var ids in collectionIdsPerPhoto)
        {
            count++;
            var set = ids.ToHashSet();
            if (first is null) first = set;
            else if (!first.SetEquals(set)) return [];
        }
        return count < 2 || first is null ? [] : first.OrderBy(id => id).ToList();
    }
}
