using System.Text.Json;
using PhotoManager.Application.Metadata;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Microstock;

public enum MicrostockPreparationStatus
{
    NotPrepared,
    MetadataIncomplete,
    ReadyForSubmission,
    PartiallyUploaded,
    UploadedToAll,
    Error,
    ChangedAfterUpload
}

public enum UploadStatusSnapshot
{
    Pending,
    Uploaded,
    Rejected,
    Error
}

/// <summary>Snapshot usado pela função pura de estado. A Fase 8 não persiste nem cria envios.</summary>
public sealed record UploadRecordSnapshot(
    long PhotoId,
    long AgencyId,
    string AgencyName,
    bool AgencyIsActive,
    UploadStatusSnapshot Status,
    int MetadataVersion,
    DateTime UploadedAt);

public sealed record ValidationRules
{
    public bool TitleRequired { get; init; } = true;
    public int TitleMinLength { get; init; } = 1;
    public int TitleMaxLength { get; init; } = 200;
    public bool DescriptionRequired { get; init; }
    public int DescriptionMinLength { get; init; }
    public int DescriptionMaxLength { get; init; } = 2000;
    public int KeywordsMinCount { get; init; } = 5;
    public int KeywordsMaxCount { get; init; } = 50;
    public bool AuthorRequired { get; init; }
    public bool CopyrightRequired { get; init; }
    public IReadOnlyList<string> AllowedExtensions { get; init; } = [];
    public double? MinimumMegapixels { get; init; }
    public int MinimumRating { get; init; }
    public bool RequireEditableFormat { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static ValidationRules FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ValidationRules>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }
}

public sealed record ValidationProfile(long Id, string Name, ValidationRules Rules, bool IsActive = false)
{
    public static ValidationProfile GenericDefault => new(0, "Padrão genérico", new());
}

public sealed record ValidationIssue(string Code, string Message);

public sealed record MetadataValidationResult(
    bool HasMinimumMetadata,
    bool IsValid,
    IReadOnlyList<ValidationIssue> Issues)
{
    public string Summary => IsValid ? "Metadata válida" : Issues.Count == 0 ? "Sem metadata" : Issues[0].Message;
}

public static class ValidationProfileValidator
{
    public static MetadataValidationResult Validate(Photo photo, PhotoMetadata metadata, ValidationProfile profile)
    {
        var rules = profile.Rules;
        var issues = new List<ValidationIssue>();
        var normalized = MetadataEdit.From(metadata);
        var hasMinimumMetadata = !string.IsNullOrWhiteSpace(normalized.Title)
            || !string.IsNullOrWhiteSpace(normalized.Description)
            || normalized.Keywords.Count > 0;

        if (photo.IsMissing || !File.Exists(photo.CurrentPath))
            issues.Add(new("missing-file", "Arquivo ausente."));
        if (metadata.Error is not null)
            issues.Add(new("metadata-read", $"Não foi possível ler os metadados: {metadata.Error}"));
        if (!hasMinimumMetadata)
            issues.Add(new("no-metadata", "Nenhum título, descrição ou palavra-chave."));

        CheckText(issues, "title", "título", normalized.Title, rules.TitleRequired, rules.TitleMinLength, rules.TitleMaxLength);
        CheckText(issues, "description", "descrição", normalized.Description, rules.DescriptionRequired, rules.DescriptionMinLength, rules.DescriptionMaxLength);
        if (normalized.Keywords.Count < rules.KeywordsMinCount)
            issues.Add(new("keywords-min", $"Faltam {rules.KeywordsMinCount - normalized.Keywords.Count} palavra(s)-chave (mín. {rules.KeywordsMinCount})."));
        if (normalized.Keywords.Count > rules.KeywordsMaxCount)
            issues.Add(new("keywords-max", $"Há palavras-chave demais (máx. {rules.KeywordsMaxCount})."));
        if (rules.AuthorRequired && string.IsNullOrWhiteSpace(normalized.Author))
            issues.Add(new("author-required", "Informe o autor."));
        if (rules.CopyrightRequired && string.IsNullOrWhiteSpace(normalized.Copyright))
            issues.Add(new("copyright-required", "Informe o copyright."));

        var extension = Path.GetExtension(photo.CurrentPath).ToLowerInvariant();
        if (rules.AllowedExtensions.Count > 0 && !rules.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            issues.Add(new("format", $"Formato {extension} não permitido neste perfil."));
        if (rules.RequireEditableFormat && !IsEditableFormat(extension))
            issues.Add(new("read-only-format", "O perfil exige um formato editável (JPEG)."));
        if (rules.MinimumMegapixels is { } minimumMp)
        {
            var megapixels = photo.Width.HasValue && photo.Height.HasValue ? photo.Width.Value * (double)photo.Height.Value / 1_000_000d : 0d;
            if (megapixels < minimumMp)
                issues.Add(new("dimensions", $"Dimensão mínima: {minimumMp:0.##} MP."));
        }
        if (photo.Rating < Math.Clamp(rules.MinimumRating, 0, 5))
            issues.Add(new("rating", $"Nota mínima: {rules.MinimumRating} estrela(s)."));

        return new(hasMinimumMetadata, issues.Count == 0, issues);
    }

    private static void CheckText(List<ValidationIssue> issues, string code, string label, string? value, bool required, int minimum, int maximum)
    {
        var length = value?.Trim().Length ?? 0;
        if (required && length == 0) issues.Add(new($"{code}-required", $"Informe o {label}."));
        if (length > 0 && length < Math.Max(0, minimum)) issues.Add(new($"{code}-min", $"O {label} precisa ter pelo menos {minimum} caracteres."));
        if (length > maximum) issues.Add(new($"{code}-max", $"O {label} excede o limite de {maximum} caracteres."));
    }

    private static bool IsEditableFormat(string extension) => extension is ".jpg" or ".jpeg";
}

public static class PreparationStatusCalculator
{
    /// <summary>
    /// Precedência: Erro, Alterada após envio, Enviada para todos, Enviada parcialmente,
    /// Pronta, Metadata incompleto, Não preparada.
    /// </summary>
    public static MicrostockPreparationStatus Calculate(
        Photo photo,
        MetadataValidationResult validation,
        IReadOnlyCollection<UploadRecordSnapshot> uploads,
        IReadOnlyCollection<long>? activeAgencyIds = null)
    {
        if (uploads.Any(u => u.Status is UploadStatusSnapshot.Error or UploadStatusSnapshot.Rejected))
            return MicrostockPreparationStatus.Error;
        if (uploads.Any(u => u.Status == UploadStatusSnapshot.Uploaded && u.MetadataVersion < photo.MetadataVersion))
            return MicrostockPreparationStatus.ChangedAfterUpload;

        var activeAgencies = uploads.Where(u => u.AgencyIsActive).Select(u => u.AgencyId).Distinct().ToHashSet();
        if (activeAgencyIds is not null) activeAgencies.UnionWith(activeAgencyIds);
        var uploadedAgencies = uploads.Where(u => u.AgencyIsActive && u.Status == UploadStatusSnapshot.Uploaded).Select(u => u.AgencyId).Distinct().ToHashSet();
        if (activeAgencies.Count > 0 && uploadedAgencies.Count == activeAgencies.Count)
            return MicrostockPreparationStatus.UploadedToAll;
        if (uploadedAgencies.Count > 0)
            return MicrostockPreparationStatus.PartiallyUploaded;
        if (validation.IsValid)
            return MicrostockPreparationStatus.ReadyForSubmission;
        return validation.HasMinimumMetadata ? MicrostockPreparationStatus.MetadataIncomplete : MicrostockPreparationStatus.NotPrepared;
    }
}

public interface IValidationProfileRepository
{
    Task<IReadOnlyList<ValidationProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ValidationProfile> SaveAsync(ValidationProfile profile, CancellationToken cancellationToken = default);
    Task SetActiveAsync(long profileId, CancellationToken cancellationToken = default);
    Task DeleteAsync(long profileId, CancellationToken cancellationToken = default);
}

/// <summary>Fallback usado somente por testes/hosts que não configuram persistência.</summary>
public sealed class InMemoryValidationProfileRepository : IValidationProfileRepository
{
    private readonly List<ValidationProfile> _profiles = [ValidationProfile.GenericDefault with { Id = 1, IsActive = true }];

    public Task<IReadOnlyList<ValidationProfile>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ValidationProfile>>(_profiles.ToList());

    public Task<ValidationProfile> SaveAsync(ValidationProfile profile, CancellationToken cancellationToken = default)
    {
        var saved = profile with { Id = profile.Id == 0 ? (_profiles.Max(p => p.Id) + 1) : profile.Id, Name = profile.Name.Trim() };
        var index = _profiles.FindIndex(p => p.Id == saved.Id);
        if (index >= 0) _profiles[index] = saved;
        else _profiles.Add(saved);
        return Task.FromResult(saved);
    }

    public Task SetActiveAsync(long profileId, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _profiles.Count; i++) _profiles[i] = _profiles[i] with { IsActive = _profiles[i].Id == profileId };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(long profileId, CancellationToken cancellationToken = default)
    {
        if (_profiles.Count <= 1) throw new InvalidOperationException("Não é possível excluir o último perfil de validação.");
        _profiles.RemoveAll(p => p.Id == profileId);
        if (!_profiles.Any(p => p.IsActive)) _profiles[0] = _profiles[0] with { IsActive = true };
        return Task.CompletedTask;
    }
}
