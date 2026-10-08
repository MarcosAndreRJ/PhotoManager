namespace PhotoManager.Domain.Photos;

public sealed class Photo
{
    public long Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string CurrentPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public DateTime? DateTaken { get; set; }
    public DateTime ImportedAt { get; set; }
    public bool IsMissing { get; set; }
    public string? CategoryName { get; set; }
    public string? PersonalNote { get; set; }
    public int Rating { get; set; }
    public bool IsFavorite { get; set; }
    /// <summary>0 = nunca editada pelo PhotoManager; incrementa a cada gravação de metadados (Fase 6).</summary>
    public int MetadataVersion { get; set; }
    /// <summary>SHA-256 do conteúdo no momento em que o arquivo foi conferido.</summary>
    public string? ContentHash { get; set; }
    public long? HashedAtSize { get; set; }
    /// <summary>Duração em segundos (somente vídeos).</summary>
    public double? DurationSeconds { get; set; }
    /// <summary>0 = dimensões/duração de vídeo ainda não conferidas (versões antigas ignoravam a rotação do celular); 1 = lidas com a regra atual.</summary>
    public int MediaInfoRevision { get; set; }
    /// <summary>Giro horário (0/90/180/270) que o contêiner do vídeo manda aplicar ao exibir (já refletido em Width/Height e nas miniaturas; o player precisa dele).</summary>
    public int AutoRotation { get; set; }
    /// <summary>Giro manual do usuário (0/90/180/270, horário), para corrigir arquivos sem metadado ou com metadado errado. Não altera o arquivo.</summary>
    public int UserRotation { get; set; }
    /// <summary>Etiqueta de cor para triagem visual rápida (validação/processamento). Só organização: não toca no arquivo.</summary>
    public PhotoColor ColorLabel { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Nome do local (geocodificação reversa), quando já foi buscado.</summary>
    public string? PlaceName { get; set; }
    /// <summary>O arquivo já foi examinado em busca de coordenadas GPS (com ou sem resultado).</summary>
    public bool GpsChecked { get; set; }
    public bool HasGps => Latitude.HasValue && Longitude.HasValue;
    /// <summary>Data usada nos filtros por período: data da captura, ou a de criação do arquivo quando não há.</summary>
    public DateTime DisplayDate => (DateTaken ?? CreatedAt).ToLocalTime();
    /// <summary>Rotação total a aplicar ao reproduzir um vídeo.</summary>
    public int VideoDisplayRotation => (AutoRotation + UserRotation) % 360;
    public bool IsVideo => PhotoManager.Domain.Photos.MediaKind.IsVideo(Extension);
    /// <summary>Paisagem, retrato ou quadrada a partir das dimensões como exibidas; desconhecida quando não há dimensões.</summary>
    public PhotoOrientation Orientation => Width is > 0 and var w && Height is > 0 and var h
        ? (UserRotation is 90 or 270 ? (w < h ? PhotoOrientation.Landscape : w > h ? PhotoOrientation.Portrait : PhotoOrientation.Square) : (w > h ? PhotoOrientation.Landscape : w < h ? PhotoOrientation.Portrait : PhotoOrientation.Square))
        : PhotoOrientation.Unknown;
    public DateTime? HashedAtModified { get; set; }
    /// <summary>Triagem (como no Lightroom): escolhida, rejeitada ou sem bandeira. Só organização.</summary>
    public PickFlag Pick { get; set; }
    /// <summary>Para quem produz conteúdo: o clipe/foto já foi usado num projeto ou publicado.</summary>
    public UsageStatus Usage { get; set; }
    /// <summary>Onde foi usado (ex.: "Vlog Valinhos #12").</summary>
    public string? UsageNote { get; set; }
    /// <summary>Hash perceptual (dHash 64 bits) da miniatura: imagens parecidas têm hashes próximos (distância de Hamming).</summary>
    public long? PerceptualHash { get; set; }
    /// <summary>Nitidez estimada (variância do Laplaciano da miniatura): quanto maior, mais nítida. Nula = ainda não analisada.</summary>
    public double? Sharpness { get; set; }
    public bool IsVertical => Orientation == PhotoOrientation.Portrait;
    public List<string> Tags { get; set; } = [];
    public List<long> CollectionIds { get; set; } = [];
    public List<string> Collections { get; set; } = [];
}

/// <summary>Distingue vídeo de foto pela extensão (a lista vive no Domain para o modelo saber; o catálogo usa a mesma).</summary>
public static class MediaKind
{
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".mts", ".m2ts" };
    public static bool IsVideo(string? extensionOrPath) => !string.IsNullOrEmpty(extensionOrPath) && Video.Contains(Path.GetExtension(extensionOrPath));
}

public enum PhotoOrientation { Unknown, Landscape, Portrait, Square }

/// <summary>Etiquetas de cor (como no Lightroom/Bridge) para classificar rápido e identificar visualmente.</summary>
public enum PhotoColor { None = 0, Red = 1, Orange = 2, Yellow = 3, Green = 4, Blue = 5, Purple = 6 }

/// <summary>Bandeira de triagem: P = escolher, X = rejeitar, U = desmarcar.</summary>
public enum PickFlag { None = 0, Picked = 1, Rejected = -1 }

/// <summary>Status de uso do material: sem uso, usado num projeto, publicado.</summary>
public enum UsageStatus { None = 0, Used = 1, Published = 2 }
