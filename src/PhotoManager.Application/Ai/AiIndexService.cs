using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Ai;

public sealed record AiIndexProgress(AiCapability Capability, int Done, int Total, string Current);

/// <summary>
/// Indexação local em segundo plano (pausável por cancelamento): vetores CLIP + etiquetas, rostos e transcrições, só para o que ainda não foi indexado.
/// Também responde à busca por significado e reagrupa os rostos em pessoas.
/// </summary>
public sealed class AiIndexService(AiEngineRegistry engines, IAiRepository repository)
{
    private float[][]? _vocabulary;
    private IReadOnlyDictionary<long, float[]>? _embeddings;

    public AiEngineRegistry Engines { get; } = engines;

    /// <summary>
    /// Indexa o que falta para cada recurso ativo e pronto. <paramref name="imagePathFor"/> devolve a imagem a analisar
    /// (o próprio arquivo para fotos; a miniatura para vídeos). Devolve quantos itens foram processados.
    /// </summary>
    public async Task<int> IndexAsync(IReadOnlyList<Photo> photos, IReadOnlyCollection<AiCapability> enabled, Func<Photo, Task<string?>> imagePathFor,
        IProgress<AiIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var processed = 0;
        foreach (var capability in enabled.Distinct().Where(Engines.IsReady))
        {
            var done = await repository.GetIndexedAsync(capability, cancellationToken);
            var pending = photos.Where(p => !p.IsMissing && !done.Contains(p.Id) && (capability != AiCapability.Transcription || p.IsVideo)).ToList();
            for (var i = 0; i < pending.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var photo = pending[i];
                progress?.Report(new AiIndexProgress(capability, i, pending.Count, photo.FileName));
                try
                {
                    await IndexOneAsync(photo, capability, imagePathFor, cancellationToken);
                    await repository.MarkIndexedAsync(photo.Id, capability, cancellationToken);
                    processed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException) { /* um arquivo ruim não para a fila */ }
            }
            progress?.Report(new AiIndexProgress(capability, pending.Count, pending.Count, string.Empty));
            if (capability == AiCapability.Faces && pending.Count > 0) await ClusterPeopleAsync(cancellationToken);
            if (capability == AiCapability.SemanticSearch) _embeddings = null;
        }
        return processed;
    }

    private async Task IndexOneAsync(Photo photo, AiCapability capability, Func<Photo, Task<string?>> imagePathFor, CancellationToken cancellationToken)
    {
        switch (capability)
        {
            case AiCapability.SemanticSearch:
            case AiCapability.AutoTags:
            {
                var embedder = Engines.Embedder!;
                if (await imagePathFor(photo) is not { } path) return;
                var vector = await embedder.EmbedImageAsync(path, cancellationToken);
                await repository.SaveEmbeddingAsync(photo.Id, embedder.ModelId, vector, cancellationToken);
                if (capability == AiCapability.AutoTags) await repository.SaveTagsAsync(photo.Id, await TagsForAsync(vector, cancellationToken), cancellationToken);
                break;
            }
            case AiCapability.Faces:
                if (await imagePathFor(photo) is { } facePath) await repository.SaveFacesAsync(photo.Id, await Engines.Faces!.DetectAsync(facePath, cancellationToken), cancellationToken);
                break;
            case AiCapability.Transcription:
                if (await Engines.Transcriber!.TranscribeAsync(photo.CurrentPath, cancellationToken) is { Text.Length: > 0 } transcript)
                    await repository.SaveTranscriptAsync(photo.Id, transcript, cancellationToken);
                break;
        }
    }

    /// <summary>As etiquetas do vocabulário mais parecidas com a imagem (até 5, só as que se destacam da média).</summary>
    private async Task<IReadOnlyList<(string Tag, float Score)>> TagsForAsync(float[] image, CancellationToken cancellationToken)
    {
        _vocabulary ??= await Task.WhenAll(AutoTagVocabulary.Terms.Select(t => Engines.Embedder!.EmbedTextAsync(t.Prompt, cancellationToken)));
        var scores = _vocabulary.Select((v, i) => (AutoTagVocabulary.Terms[i].Tag, Score: VectorMath.Cosine(image, v))).ToList();
        var mean = scores.Average(s => s.Score);
        return scores.Where(s => s.Score >= mean + 0.03f).OrderByDescending(s => s.Score).Take(5).ToList();
    }

    /// <summary>Busca por significado: a frase vira vetor e volta a lista das imagens mais parecidas (acima de <paramref name="minScore"/>).</summary>
    public async Task<IReadOnlyList<(long PhotoId, float Score)>> SearchAsync(string text, int max = 300, float minScore = 0.2f, CancellationToken cancellationToken = default)
    {
        if (Engines.Embedder is not { } embedder || string.IsNullOrWhiteSpace(text)) return [];
        _embeddings ??= await repository.GetEmbeddingsAsync(embedder.ModelId, cancellationToken);
        var query = await embedder.EmbedTextAsync(text, cancellationToken);
        return _embeddings.Select(e => (e.Key, Score: VectorMath.Cosine(query, e.Value))).Where(r => r.Score >= minScore)
            .OrderByDescending(r => r.Score).Take(max).ToList();
    }

    /// <summary>Rostos ainda sem pessoa entram na pessoa mais parecida (pelo centro do grupo) ou formam pessoas novas.</summary>
    public async Task ClusterPeopleAsync(CancellationToken cancellationToken = default)
    {
        var faces = await repository.GetFacesAsync(cancellationToken);
        var centroids = faces.Where(f => f.PersonId.HasValue).GroupBy(f => f.PersonId!.Value)
            .Select(g => (Person: g.Key, Centroid: Average(g.Select(f => f.Embedding).ToList()))).ToList();
        var unassigned = faces.Where(f => !f.PersonId.HasValue).ToList();
        var toExisting = new Dictionary<long, List<long>>();
        var remaining = new List<AiFace>();
        foreach (var face in unassigned)
        {
            var best = centroids.Select(c => (c.Person, Score: VectorMath.Cosine(face.Embedding, c.Centroid))).OrderByDescending(c => c.Score).FirstOrDefault();
            if (centroids.Count > 0 && best.Score >= FaceClustering.DefaultThreshold) (toExisting.TryGetValue(best.Person, out var list) ? list : toExisting[best.Person] = []).Add(face.Id);
            else remaining.Add(face);
        }
        foreach (var (person, ids) in toExisting) await repository.AssignFacesAsync(ids, person, cancellationToken);
        foreach (var cluster in FaceClustering.Cluster(remaining))
        {
            var person = await repository.CreatePersonAsync(null, cancellationToken);
            await repository.AssignFacesAsync(cluster.Select(f => f.Id).ToList(), person, cancellationToken);
        }
    }

    private static float[] Average(IReadOnlyList<float[]> vectors)
    {
        var result = new float[vectors[0].Length];
        foreach (var v in vectors) for (var i = 0; i < result.Length; i++) result[i] += v[i] / vectors.Count;
        return result;
    }
}
