using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Catalog;

/// <summary>
/// Organização aplicada a várias fotos de uma vez. Todo campo vazio/nulo significa "manter":
/// nada é apagado, tags e coleções só são acrescentadas e as demais informações de cada foto permanecem.
/// </summary>
public sealed record OrganizationBatch
{
    public string? Category { get; init; }
    public IReadOnlyList<string> TagsToAdd { get; init; } = [];
    public IReadOnlyList<long> CollectionsToAdd { get; init; } = [];
    /// <summary>0–5 define a avaliação; nulo mantém.</summary>
    public int? Rating { get; init; }
    /// <summary>true marca, false desmarca, nulo mantém.</summary>
    public bool? Favorite { get; init; }
    public string? Note { get; init; }

    public bool IsNoOp => string.IsNullOrWhiteSpace(Category) && TagsToAdd.Count == 0 && CollectionsToAdd.Count == 0 && Rating is null && Favorite is null && string.IsNullOrWhiteSpace(Note);

    /// <summary>Divide texto "a, b; c" em itens limpos e sem repetição (ignorando maiúsculas).</summary>
    public static IReadOnlyList<string> ParseList(string? text) =>
        (text ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Aplica à foto (mutando-a) e informa se algo mudou.</summary>
    public bool ApplyTo(Photo photo)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(Category) && !string.Equals(photo.CategoryName, Category.Trim(), StringComparison.Ordinal)) { photo.CategoryName = Category.Trim(); changed = true; }
        foreach (var tag in TagsToAdd)
            if (!photo.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) { photo.Tags.Add(tag); changed = true; }
        foreach (var collectionId in CollectionsToAdd)
            if (!photo.CollectionIds.Contains(collectionId)) { photo.CollectionIds.Add(collectionId); changed = true; }
        if (Rating is { } rating && photo.Rating != Math.Clamp(rating, 0, 5)) { photo.Rating = Math.Clamp(rating, 0, 5); changed = true; }
        if (Favorite is { } favorite && photo.IsFavorite != favorite) { photo.IsFavorite = favorite; changed = true; }
        if (!string.IsNullOrWhiteSpace(Note) && !string.Equals(photo.PersonalNote, Note.Trim(), StringComparison.Ordinal)) { photo.PersonalNote = Note.Trim(); changed = true; }
        return changed;
    }
}
