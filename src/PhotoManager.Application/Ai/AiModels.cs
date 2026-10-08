using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Ai;

/// <summary>O que a IA local faz. Tudo roda no computador: nenhuma foto sai da máquina.</summary>
public enum AiCapability { SemanticSearch, Faces, AutoTags, Transcription }

/// <summary>Um modelo que o usuário pode baixar ao ativar um recurso (nunca automaticamente).</summary>
public sealed record AiModelInfo(string Id, AiCapability Capability, string Name, string Description, string Url, long ApproxBytes, string FileName, string? Sha256 = null);

public static class AiModelCatalog
{
    /// <summary>Modelos padrão (licenças abertas). URLs e tamanhos aproximados; o hash é conferido quando informado.</summary>
    public static IReadOnlyList<AiModelInfo> Models { get; } =
    [
        new("clip-vision", AiCapability.SemanticSearch, "CLIP ViT-B/32 — imagem", "Entende o conteúdo das fotos para a busca por descrição e as etiquetas automáticas.",
            "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model_quantized.onnx", 90L << 20, "clip-vision.onnx"),
        new("clip-text", AiCapability.SemanticSearch, "CLIP ViT-B/32 — texto", "Transforma a frase digitada na busca no mesmo espaço das imagens.",
            "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/text_model_quantized.onnx", 65L << 20, "clip-text.onnx"),
        new("clip-tokenizer", AiCapability.SemanticSearch, "CLIP — vocabulário", "Vocabulário do modelo de texto.",
            "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/tokenizer.json", 2L << 20, "clip-tokenizer.json"),
        new("face-detect", AiCapability.Faces, "YuNet — detecção de rostos", "Encontra rostos nas fotos e nos quadros de vídeo.",
            "https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx", 1L << 20, "face-yunet.onnx"),
        new("face-embed", AiCapability.Faces, "ArcFace — reconhecimento", "Gera a “assinatura” de cada rosto para agrupar a mesma pessoa.",
            "https://github.com/onnx/models/raw/main/validated/vision/body_analysis/arcface/model/arcfaceresnet100-8.onnx", 250L << 20, "face-arcface.onnx"),
        new("whisper-base", AiCapability.Transcription, "Whisper base (multilíngue)", "Transcreve a fala dos vídeos (português incluído) para busca e legendas.",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin", 148L << 20, "whisper-base.bin")
    ];

    public static IEnumerable<AiModelInfo> For(AiCapability capability) =>
        Models.Where(m => m.Capability == capability || (capability == AiCapability.AutoTags && m.Capability == AiCapability.SemanticSearch));

    public static string Describe(AiCapability capability) => capability switch
    {
        AiCapability.SemanticSearch => "Busca por descrição",
        AiCapability.Faces => "Pessoas (reconhecimento de rostos)",
        AiCapability.AutoTags => "Etiquetas automáticas de cena",
        _ => "Transcrição da fala dos vídeos"
    };
}

// ---------- motores (implementações plugáveis: ONNX Runtime / Whisper) ----------

public interface IAiEngine
{
    AiCapability Capability { get; }
    /// <summary>Motor instalado e modelos presentes.</summary>
    bool IsReady { get; }
}

/// <summary>CLIP: imagem e texto no mesmo espaço vetorial (vetores normalizados).</summary>
public interface IImageTextEmbedder : IAiEngine
{
    string ModelId { get; }
    Task<float[]> EmbedImageAsync(string path, CancellationToken cancellationToken = default);
    Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default);
}

public sealed record DetectedFace(float X, float Y, float Width, float Height, float[] Embedding);

public interface IFaceAnalyzer : IAiEngine
{
    Task<IReadOnlyList<DetectedFace>> DetectAsync(string path, CancellationToken cancellationToken = default);
}

public sealed record Transcript(string Language, string Text);

public interface ITranscriber : IAiEngine
{
    Task<Transcript?> TranscribeAsync(string videoPath, CancellationToken cancellationToken = default);
}

/// <summary>Motores registrados. Sem motor instalado o recurso fica visível, mas indisponível (com o motivo).</summary>
public sealed class AiEngineRegistry(IEnumerable<IAiEngine>? engines = null)
{
    private readonly List<IAiEngine> _engines = engines?.ToList() ?? [];
    public IImageTextEmbedder? Embedder => _engines.OfType<IImageTextEmbedder>().FirstOrDefault(e => e.IsReady);
    public IFaceAnalyzer? Faces => _engines.OfType<IFaceAnalyzer>().FirstOrDefault(e => e.IsReady);
    public ITranscriber? Transcriber => _engines.OfType<ITranscriber>().FirstOrDefault(e => e.IsReady);
    public bool HasEngineFor(AiCapability capability) => _engines.Any(e => e.Capability == capability || (capability == AiCapability.AutoTags && e is IImageTextEmbedder));
    public bool IsReady(AiCapability capability) => capability switch
    {
        AiCapability.SemanticSearch or AiCapability.AutoTags => Embedder is not null,
        AiCapability.Faces => Faces is not null,
        _ => Transcriber is not null
    };
}

// ---------- persistência do índice ----------

public sealed record AiPerson(long Id, string? Name, int FaceCount, long CoverPhotoId)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Pessoa {Id}" : Name;
}

public sealed record AiFace(long Id, long PhotoId, long? PersonId, float X, float Y, float Width, float Height, float[] Embedding);

public interface IAiRepository
{
    Task SaveEmbeddingAsync(long photoId, string model, float[] vector, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<long, float[]>> GetEmbeddingsAsync(string model, CancellationToken cancellationToken = default);
    Task SaveFacesAsync(long photoId, IReadOnlyList<DetectedFace> faces, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiFace>> GetFacesAsync(CancellationToken cancellationToken = default);
    Task<long> CreatePersonAsync(string? name, CancellationToken cancellationToken = default);
    Task AssignFacesAsync(IReadOnlyCollection<long> faceIds, long? personId, CancellationToken cancellationToken = default);
    Task RenamePersonAsync(long personId, string? name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiPerson>> GetPeopleAsync(CancellationToken cancellationToken = default);
    Task SaveTagsAsync(long photoId, IReadOnlyList<(string Tag, float Score)> tags, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<long, IReadOnlyList<string>>> GetTagsAsync(CancellationToken cancellationToken = default);
    Task SaveTranscriptAsync(long photoId, Transcript transcript, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<long, string>> GetTranscriptsAsync(CancellationToken cancellationToken = default);
    Task MarkIndexedAsync(long photoId, AiCapability kind, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<long>> GetIndexedAsync(AiCapability kind, CancellationToken cancellationToken = default);
}

// ---------- download de modelos ----------

public sealed record ModelDownloadProgress(AiModelInfo Model, long Received, long? Total);

public interface IAiModelManager
{
    string ModelsFolder { get; }
    bool IsInstalled(AiModelInfo model);
    /// <summary>Baixa (só quando o usuário pede), conferindo o hash quando informado. O arquivo parcial nunca fica com o nome final.</summary>
    Task DownloadAsync(AiModelInfo model, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    void Remove(AiModelInfo model);
}

// ---------- vetores ----------

public static class VectorMath
{
    public static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : (float)(dot / Math.Sqrt(na * nb));
    }

    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, vector.Length * sizeof(float));
        return vector;
    }
}

/// <summary>Agrupa rostos da mesma pessoa: cada rosto entra no grupo cujo centro é mais parecido (acima do limiar), ou abre um novo.</summary>
public static class FaceClustering
{
    public const float DefaultThreshold = 0.55f;

    public static IReadOnlyList<IReadOnlyList<AiFace>> Cluster(IReadOnlyList<AiFace> faces, float threshold = DefaultThreshold)
    {
        var clusters = new List<(List<AiFace> Members, float[] Centroid)>();
        foreach (var face in faces)
        {
            var best = -1; var bestScore = threshold;
            for (var i = 0; i < clusters.Count; i++)
            {
                var score = VectorMath.Cosine(face.Embedding, clusters[i].Centroid);
                if (score >= bestScore) { best = i; bestScore = score; }
            }
            if (best < 0) { clusters.Add(([face], (float[])face.Embedding.Clone())); continue; }
            var (members, centroid) = clusters[best];
            members.Add(face);
            for (var d = 0; d < centroid.Length; d++) centroid[d] += (face.Embedding[d] - centroid[d]) / members.Count;
        }
        return clusters.Select(c => (IReadOnlyList<AiFace>)c.Members).ToList();
    }
}

/// <summary>Vocabulário das etiquetas automáticas (comparado por similaridade com a imagem; termos em inglês funcionam melhor com o CLIP).</summary>
public static class AutoTagVocabulary
{
    public static IReadOnlyList<(string Tag, string Prompt)> Terms { get; } =
    [
        ("praia", "a photo of a beach"), ("montanha", "a photo of mountains"), ("cidade", "a photo of a city street"), ("estrada", "a photo of a road"),
        ("pôr do sol", "a photo of a sunset"), ("noite", "a photo taken at night"), ("neblina", "a foggy landscape"), ("chuva", "a rainy day"),
        ("feira", "a street market with stalls"), ("comida", "a photo of food"), ("carro", "a photo of a car"), ("ônibus", "a photo of a bus"), ("caminhão", "a photo of a truck"),
        ("pessoas", "a photo of people"), ("selfie", "a selfie"), ("cachorro", "a photo of a dog"), ("gato", "a photo of a cat"), ("árvore", "a photo of trees"),
        ("interior", "an indoor photo"), ("documento", "a photo of a document"), ("captura de tela", "a screenshot"), ("festa", "a party"), ("show", "a concert")
    ];
}
