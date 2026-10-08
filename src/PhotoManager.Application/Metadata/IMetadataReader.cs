namespace PhotoManager.Application.Metadata;

/// <summary>Metadados lidos do arquivo (somente leitura). Campos ausentes ficam nulos/vazios.</summary>
public sealed record PhotoMetadata
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public string? Author { get; init; }
    public string? Copyright { get; init; }

    public string? Camera { get; init; }
    public string? Lens { get; init; }
    public int? Iso { get; init; }
    public string? ExposureTime { get; init; }
    public string? Aperture { get; init; }
    public string? FocalLength { get; init; }
    public DateTime? DateTaken { get; init; }

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// <summary>Quais blocos existem no arquivo (EXIF, IPTC, XMP, GPS), para exibição.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];
    /// <summary>Preenchido quando o arquivo não pôde ser lido; a UI mostra a mensagem em vez de falhar.</summary>
    public string? Error { get; init; }

    public bool HasGps => Latitude.HasValue && Longitude.HasValue;
    public bool HasExif => Camera is not null || Lens is not null || Iso.HasValue || ExposureTime is not null || Aperture is not null || FocalLength is not null || DateTaken.HasValue;
}

public interface IMetadataReader
{
    Task<PhotoMetadata> ReadAsync(string path, CancellationToken cancellationToken = default);
}
