using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Metadata;

/// <summary>Campos editáveis na Fase 6 (IPTC/XMP). Sempre normalizado: sem espaços sobrando, vazio vira nulo, palavras-chave sem duplicatas.</summary>
public sealed record MetadataEdit
{
    public const int TitleLimit = 200;
    public const int DescriptionLimit = 2000;
    public const int KeywordLimit = 50;
    public const int AuthorLimit = 200;
    public const int CopyrightLimit = 200;

    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public string? Author { get; init; }
    public string? Copyright { get; init; }

    public bool IsEmpty => Title is null && Description is null && Keywords.Count == 0 && Author is null && Copyright is null;

    public static MetadataEdit From(PhotoMetadata metadata) => new MetadataEdit
    {
        Title = metadata.Title, Description = metadata.Description, Keywords = metadata.Keywords, Author = metadata.Author, Copyright = metadata.Copyright
    }.Normalize();

    public MetadataEdit Normalize() => new()
    {
        Title = Clean(Title),
        Description = Clean(Description),
        Keywords = Keywords.Select(k => k?.Trim() ?? string.Empty).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Author = Clean(Author),
        Copyright = Clean(Copyright)
    };

    /// <summary>Igualdade de conteúdo (ordem das palavras-chave é relevante: é a ordem gravada no arquivo).</summary>
    public bool SameAs(MetadataEdit other)
    {
        var a = Normalize();
        var b = other.Normalize();
        return a.Title == b.Title && a.Description == b.Description && a.Author == b.Author && a.Copyright == b.Copyright
            && a.Keywords.SequenceEqual(b.Keywords, StringComparer.Ordinal);
    }

    /// <summary>Nomes (em português) dos campos que diferem; usado no resumo do histórico.</summary>
    public IReadOnlyList<string> ChangedFields(MetadataEdit other)
    {
        var a = Normalize();
        var b = other.Normalize();
        var changed = new List<string>();
        if (a.Title != b.Title) changed.Add("Título");
        if (a.Description != b.Description) changed.Add("Descrição");
        if (!a.Keywords.SequenceEqual(b.Keywords, StringComparer.Ordinal)) changed.Add("Palavras-chave");
        if (a.Author != b.Author) changed.Add("Autor");
        if (a.Copyright != b.Copyright) changed.Add("Copyright");
        return changed;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record MetadataVersionEntry(long PhotoId, int Version, DateTime ChangedAt, MetadataEdit Values, string? Note);

/// <summary>Grava IPTC/XMP no arquivo de forma segura (temporário → validação → substituição). Não altera EXIF nem pixels.</summary>
public interface IMetadataWriter
{
    bool CanWrite(string path);
    Task WriteAsync(string path, MetadataEdit edit, CancellationToken cancellationToken = default);
}

public interface IMetadataVersionRepository
{
    /// <summary>Registra o estado dos metadados na versão indicada e atualiza <c>Photos.MetadataVersion</c> (nunca diminui).</summary>
    Task RecordVersionAsync(long photoId, int version, MetadataEdit values, string? note, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MetadataVersionEntry>> GetHistoryAsync(long photoId, CancellationToken cancellationToken = default);
}

public sealed record MetadataSaveResult(bool Changed, int Version, IReadOnlyList<string> ChangedFields);

public interface IMetadataEditService
{
    bool CanEdit(Photo photo);
    Task<MetadataSaveResult> SaveAsync(Photo photo, MetadataEdit edit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MetadataVersionEntry>> GetHistoryAsync(long photoId, CancellationToken cancellationToken = default);
}
