using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Metadata;

/// <summary>Operação aplicada a um campo na edição em lote. Cada campo aceita um subconjunto (ver <see cref="BatchMetadataPlan.Allowed"/>).</summary>
public enum BatchOperation { Keep, Replace, Append, Add, Remove, Clear }

/// <summary>
/// O que fazer em cada campo, de forma independente. Um plano pode ser salvo como preset.
/// Campos com <see cref="BatchOperation.Keep"/> nunca são tocados.
/// </summary>
public sealed record BatchMetadataPlan
{
    public BatchOperation TitleOperation { get; init; }
    public string? Title { get; init; }
    public BatchOperation DescriptionOperation { get; init; }
    public string? Description { get; init; }
    public BatchOperation KeywordsOperation { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public BatchOperation AuthorOperation { get; init; }
    public string? Author { get; init; }
    public BatchOperation CopyrightOperation { get; init; }
    public string? Copyright { get; init; }

    public static IReadOnlyList<BatchOperation> Allowed(string field) => field switch
    {
        nameof(Title) or nameof(Author) or nameof(Copyright) => [BatchOperation.Keep, BatchOperation.Replace],
        nameof(Description) => [BatchOperation.Keep, BatchOperation.Replace, BatchOperation.Append],
        nameof(Keywords) => [BatchOperation.Keep, BatchOperation.Replace, BatchOperation.Add, BatchOperation.Remove, BatchOperation.Clear],
        _ => [BatchOperation.Keep]
    };

    public bool IsNoOp => TitleOperation == BatchOperation.Keep && DescriptionOperation == BatchOperation.Keep && KeywordsOperation == BatchOperation.Keep
        && AuthorOperation == BatchOperation.Keep && CopyrightOperation == BatchOperation.Keep;

    /// <summary>Problemas que impedem aplicar o plano (lista vazia = válido).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        void Check(string field, string label, BatchOperation operation, bool hasValue)
        {
            if (!Allowed(field).Contains(operation)) problems.Add($"{label}: operação não permitida.");
            else if (operation is BatchOperation.Replace or BatchOperation.Append or BatchOperation.Add or BatchOperation.Remove && !hasValue)
                problems.Add($"{label}: informe um valor para esta operação.");
        }
        Check(nameof(Title), "Título", TitleOperation, !string.IsNullOrWhiteSpace(Title));
        Check(nameof(Description), "Descrição", DescriptionOperation, !string.IsNullOrWhiteSpace(Description));
        Check(nameof(Keywords), "Palavras-chave", KeywordsOperation, Keywords.Any(k => !string.IsNullOrWhiteSpace(k)));
        Check(nameof(Author), "Autor", AuthorOperation, !string.IsNullOrWhiteSpace(Author));
        Check(nameof(Copyright), "Copyright", CopyrightOperation, !string.IsNullOrWhiteSpace(Copyright));
        return problems;
    }

    /// <summary>Resultado de aplicar o plano aos metadados atuais de uma foto (função pura).</summary>
    public MetadataEdit Apply(MetadataEdit current)
    {
        var edit = current.Normalize();
        var wanted = new MetadataEdit { Keywords = Keywords }.Normalize().Keywords;
        return new MetadataEdit
        {
            Title = TitleOperation == BatchOperation.Replace ? Title : edit.Title,
            Description = DescriptionOperation switch
            {
                BatchOperation.Replace => Description,
                BatchOperation.Append => string.IsNullOrWhiteSpace(Description) ? edit.Description : string.IsNullOrWhiteSpace(edit.Description) ? Description : edit.Description + " " + Description!.Trim(),
                _ => edit.Description
            },
            Keywords = KeywordsOperation switch
            {
                BatchOperation.Replace => wanted,
                BatchOperation.Add => edit.Keywords.Concat(wanted).ToList(),
                BatchOperation.Remove => edit.Keywords.Where(k => !wanted.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList(),
                BatchOperation.Clear => [],
                _ => edit.Keywords
            },
            Author = AuthorOperation == BatchOperation.Replace ? Author : edit.Author,
            Copyright = CopyrightOperation == BatchOperation.Replace ? Copyright : edit.Copyright
        }.Normalize();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() }, WriteIndented = false };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static BatchMetadataPlan FromJson(string json) => JsonSerializer.Deserialize<BatchMetadataPlan>(json, JsonOptions) ?? new BatchMetadataPlan();
}

public sealed record MetadataPreset(long Id, string Name, BatchMetadataPlan Plan);

public interface IMetadataPresetRepository
{
    Task<IReadOnlyList<MetadataPreset>> GetAllAsync(CancellationToken cancellationToken = default);
    /// <summary>Cria ou atualiza (o nome é único, sem diferenciar maiúsculas).</summary>
    Task<MetadataPreset> SaveAsync(string name, BatchMetadataPlan plan, CancellationToken cancellationToken = default);
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}

public enum BatchItemStatus { WillChange, Changed, Unchanged, Skipped, Failed }

public sealed record BatchItemResult(Photo Photo, BatchItemStatus Status, IReadOnlyList<string> ChangedFields, string? Message);

public sealed record BatchRunResult(IReadOnlyList<BatchItemResult> Items)
{
    public int Changed => Items.Count(i => i.Status == BatchItemStatus.Changed);
    public int Unchanged => Items.Count(i => i.Status == BatchItemStatus.Unchanged);
    public int Skipped => Items.Count(i => i.Status == BatchItemStatus.Skipped);
    public int Failed => Items.Count(i => i.Status == BatchItemStatus.Failed);
}

public sealed record BatchProgress(int Processed, int Total, string CurrentFile);

public interface IBatchMetadataService
{
    /// <summary>Calcula, sem gravar nada, o que mudaria em cada foto.</summary>
    Task<IReadOnlyList<BatchItemResult>> PreviewAsync(IReadOnlyList<Photo> photos, BatchMetadataPlan plan, CancellationToken cancellationToken = default);
    /// <summary>Aplica foto a foto pelo pipeline seguro; uma falha não interrompe as demais.</summary>
    Task<BatchRunResult> ApplyAsync(IReadOnlyList<Photo> photos, BatchMetadataPlan plan, IProgress<BatchProgress>? progress = null, CancellationToken cancellationToken = default);
}
