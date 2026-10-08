using System.Numerics;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Library;

/// <summary>
/// Análise leve sobre a miniatura em tons de cinza (nunca o original): hash perceptual (dHash) para achar imagens parecidas
/// e nitidez (variância do Laplaciano) para apontar fotos tremidas/desfocadas.
/// </summary>
public static class ImageAnalysis
{
    /// <summary>dHash de 64 bits: reduz para 9×8 e compara cada pixel com o vizinho da direita. Robusto a redimensionar/recomprimir.</summary>
    public static long DifferenceHash(byte[] gray, int width, int height)
    {
        var small = Resize(gray, width, height, 9, 8);
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++, bit++)
                if (small[y * 9 + x] > small[y * 9 + x + 1]) hash |= 1UL << bit;
        return unchecked((long)hash);
    }

    public static int Distance(long a, long b) => BitOperations.PopCount(unchecked((ulong)(a ^ b)));

    /// <summary>Variância do Laplaciano (kernel 4-vizinhos) numa versão de até 256 px. Típico: &lt; 60 desfocada, &gt; 150 nítida.</summary>
    public static double Sharpness(byte[] gray, int width, int height)
    {
        var scale = Math.Min(1.0, 256.0 / Math.Max(width, height));
        int w = Math.Max(3, (int)(width * scale)), h = Math.Max(3, (int)(height * scale));
        var img = scale < 1 ? Resize(gray, width, height, w, h) : gray.Select(b => (double)b).ToArray();
        double sum = 0, sumSq = 0;
        var n = 0;
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                var lap = img[i - 1] + img[i + 1] + img[i - w] + img[i + w] - 4 * img[i];
                sum += lap; sumSq += lap * lap; n++;
            }
        if (n == 0) return 0;
        var mean = sum / n;
        return sumSq / n - mean * mean;
    }

    /// <summary>Redução por média de área (sem serrilhado), devolvendo valores 0–255 em double.</summary>
    private static double[] Resize(byte[] gray, int width, int height, int targetW, int targetH)
    {
        var result = new double[targetW * targetH];
        for (var ty = 0; ty < targetH; ty++)
        {
            int y0 = ty * height / targetH, y1 = Math.Max(y0 + 1, (ty + 1) * height / targetH);
            for (var tx = 0; tx < targetW; tx++)
            {
                int x0 = tx * width / targetW, x1 = Math.Max(x0 + 1, (tx + 1) * width / targetW);
                double total = 0; var count = 0;
                for (var y = y0; y < y1 && y < height; y++)
                    for (var x = x0; x < x1 && x < width; x++) { total += gray[y * width + x]; count++; }
                result[ty * targetW + tx] = count == 0 ? 0 : total / count;
            }
        }
        return result;
    }
}

public sealed record SimilarGroup(IReadOnlyList<Photo> Photos, Photo Best)
{
    public long ReclaimableBytes => Photos.Where(p => p.Id != Best.Id).Sum(p => p.FileSize);
}

/// <summary>Agrupa fotos/vídeos visualmente parecidos (mesma cena repetida, cópia reexportada) e sugere a melhor de cada grupo.</summary>
public static class SimilarityFinder
{
    public const int DefaultThreshold = 6;

    public static IReadOnlyList<SimilarGroup> Find(IReadOnlyList<Photo> photos, int threshold = DefaultThreshold)
    {
        var items = photos.Where(p => p.PerceptualHash.HasValue && !p.IsMissing).ToList();
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var hashes = items.Select(p => p.PerceptualHash!.Value).ToArray();
        for (var i = 0; i < items.Count; i++)
            for (var j = i + 1; j < items.Count; j++)
                if (items[i].IsVideo == items[j].IsVideo && ImageAnalysis.Distance(hashes[i], hashes[j]) <= threshold)
                {
                    int a = Root(i), b = Root(j);
                    if (a != b) parent[b] = a;
                }
        return Enumerable.Range(0, items.Count).GroupBy(Root).Where(g => g.Count() > 1)
            .Select(g => g.Select(i => items[i]).ToList())
            .Select(group => new SimilarGroup(group, PickBest(group)))
            .OrderByDescending(g => g.Photos.Count).ThenBy(g => g.Best.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Melhor = escolhida na triagem, depois mais estrelas, depois mais nítida, depois maior resolução.</summary>
    public static Photo PickBest(IReadOnlyList<Photo> group) => group
        .OrderByDescending(p => p.Pick == PickFlag.Picked)
        .ThenBy(p => p.Pick == PickFlag.Rejected)
        .ThenByDescending(p => p.Rating)
        .ThenByDescending(p => p.Sharpness ?? 0)
        .ThenByDescending(p => (long)(p.Width ?? 0) * (p.Height ?? 0))
        .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
        .First();
}

public enum StackKind { RawJpeg, Burst }

/// <summary>Uma pilha: RAW+JPEG do mesmo clique ou uma rajada. A grade mostra só o <see cref="Top"/> com um contador.</summary>
public sealed record PhotoStack(string Key, StackKind Kind, IReadOnlyList<Photo> Photos, Photo Top);

public static class StackBuilder
{
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2" };

    /// <summary>
    /// RAW+JPEG: mesma pasta e mesmo nome-base. Rajada: fotos da mesma pasta com data de captura a no máximo <paramref name="burstGapSeconds"/> uma da outra.
    /// Um item pertence a no máximo uma pilha (RAW+JPEG tem prioridade).
    /// </summary>
    public static IReadOnlyList<PhotoStack> Build(IReadOnlyList<Photo> photos, double burstGapSeconds = 1.0)
    {
        var stacks = new List<PhotoStack>();
        var used = new HashSet<long>();
        foreach (var group in photos.Where(p => !p.IsVideo)
                     .GroupBy(p => (Folder: Path.GetDirectoryName(p.CurrentPath) ?? string.Empty, Stem: Path.GetFileNameWithoutExtension(p.FileName)), new FolderStemComparer())
                     .Where(g => g.Count() > 1 && g.Any(p => RawExtensions.Contains(p.Extension)) && g.Any(p => !RawExtensions.Contains(p.Extension))))
        {
            var members = group.ToList();
            var top = members.Where(p => !RawExtensions.Contains(p.Extension)).OrderByDescending(p => p.Rating).First();
            stacks.Add(new PhotoStack($"raw:{group.Key.Folder}|{group.Key.Stem}", StackKind.RawJpeg, members, top));
            used.UnionWith(members.Select(p => p.Id));
        }

        foreach (var folder in photos.Where(p => !p.IsVideo && p.DateTaken.HasValue && !used.Contains(p.Id)).GroupBy(p => Path.GetDirectoryName(p.CurrentPath) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = folder.OrderBy(p => p.DateTaken).ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            var run = new List<Photo> { ordered[0] };
            void Flush()
            {
                if (run.Count >= 2)
                {
                    var top = SimilarityFinder.PickBest(run);
                    stacks.Add(new PhotoStack($"burst:{folder.Key}|{run[0].Id}", StackKind.Burst, run.ToList(), top));
                }
                run.Clear();
            }
            for (var i = 1; i < ordered.Count; i++)
            {
                if ((ordered[i].DateTaken!.Value - ordered[i - 1].DateTaken!.Value).TotalSeconds > burstGapSeconds) Flush();
                run.Add(ordered[i]);
            }
            Flush();
        }
        return stacks;
    }

    private sealed class FolderStemComparer : IEqualityComparer<(string Folder, string Stem)>
    {
        public bool Equals((string Folder, string Stem) x, (string Folder, string Stem) y) =>
            string.Equals(x.Folder, y.Folder, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Stem, y.Stem, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Folder, string Stem) obj) =>
            HashCode.Combine(obj.Folder.ToUpperInvariant(), obj.Stem.ToUpperInvariant());
    }
}
