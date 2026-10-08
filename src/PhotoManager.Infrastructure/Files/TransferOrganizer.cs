using System.Globalization;
using Microsoft.VisualBasic.FileIO;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Metadata;
using PhotoManager.Application.Transfer;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Infrastructure.Files;

/// <summary>
/// Operações de organização do módulo Transferência. O disco é a fonte da verdade; o catálogo é ajustado logo depois para não "perder" itens:
/// renomear/mover reescreve os caminhos catalogados (e as cores de pasta), excluir (sempre para a Lixeira) marca os itens como ausentes.
/// </summary>
public sealed class TransferOrganizer(
    ICatalogRepository repository,
    ICatalogService catalog,
    ITransferRepository? transferRepository = null,
    IMetadataReader? metadataReader = null,
    IPhotoInfoReader? infoReader = null,
    IFileHashService? hashService = null) : ITransferOrganizer
{
    private const int BufferSize = 1 << 20;
    private readonly ITransferRepository? _transfer = transferRepository ?? repository as ITransferRepository;
    private readonly IFileHashService _hash = hashService ?? new Sha256FileHashService();

    public event EventHandler<TransferCatalogChange>? CatalogChanged;

    // ---------- pastas: criar, renomear, Lixeira ----------

    public Task<string> CreateFolderAsync(string parentFolder, string name, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!Directory.Exists(parentFolder)) throw new IOException("A pasta onde a nova pasta seria criada não está disponível.");
        if (TransferNames.Validate(name, Siblings(parentFolder)) is { } error) throw new IOException(error);
        var path = Path.Combine(parentFolder, name.Trim());
        Directory.CreateDirectory(path);
        return path;
    }, cancellationToken);

    public async Task<TransferRenameResult> RenameAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(path.TrimEnd('\\')) ?? throw new IOException("Não é possível renomear a raiz de um disco.");
        var isFolder = Directory.Exists(path);
        if (!isFolder && !File.Exists(path)) throw new IOException("O item não existe mais (foi movido ou excluído fora do PhotoManager).");
        var currentName = Path.GetFileName(path.TrimEnd('\\'));
        var finalName = isFolder ? newName.Trim() : TransferNames.WithOriginalExtension(newName, currentName);
        if (TransferNames.Validate(finalName, Siblings(parent), currentName) is { } error) throw new IOException(error);
        var destination = Path.Combine(parent, finalName);
        if (string.Equals(destination, path, StringComparison.Ordinal)) return new TransferRenameResult(path, 0);

        await Task.Run(() => MoveItem(path, destination, isFolder), cancellationToken);
        var updated = await FollowInCatalogAsync([(path, destination)], cancellationToken);
        return new TransferRenameResult(destination, updated);
    }

    public async Task<TransferRecycleResult> RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var recycled = new List<string>();
        var failed = new List<(string, string)>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await Task.Run(() => Recycle(path), cancellationToken);
                recycled.Add(path);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { failed.Add((path, "Exclusão cancelada.")); }   // o usuário cancelou o diálogo do Windows
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add((path, ex.Message)); }
        }
        // Os itens catalogados continuam no catálogo (com tags/coleções/histórico), apenas como ausentes — dá para restaurar da Lixeira.
        if (recycled.Count > 0)
        {
            if (_transfer is not null) await _transfer.RemoveFolderColorsAsync(recycled, cancellationToken);
            await repository.UpdateMissingStatesAsync(cancellationToken);
            CatalogChanged?.Invoke(this, new TransferCatalogChange(recycled, null));
        }
        return new TransferRecycleResult(recycled, failed);
    }

    // ---------- copiar / mover ----------

    public async Task<TransferBatchResult> TransferAsync(TransferRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var destination = request.Destination.TrimEnd('\\');
        if (destination.Length == 2 && destination[1] == ':') destination += "\\";
        if (!Directory.Exists(destination)) throw new IOException("A pasta de destino não está disponível.");
        var sources = TransferConflicts.Normalize(request.Sources, destination, request.Move).Where(s => File.Exists(s) || Directory.Exists(s)).ToList();
        var plan = await Task.Run(() => sources.Select(s => (Source: s, IsFolder: Directory.Exists(s), Files: Directory.Exists(s) ? EnumerateFiles(s) : [new FileInfo(s)])).ToList(), cancellationToken);

        var totalFiles = plan.Sum(p => p.Files.Count);
        var totalBytes = plan.Sum(p => p.Files.Sum(f => f.Length));
        long bytesDone = 0;
        var filesDone = 0;
        var moves = new List<(string From, string To)>();
        var created = new List<string>();
        var catalogMoves = new List<(string From, string To)>();
        var failed = new List<(string, string)>();
        var skipped = 0;
        var cancelled = false;
        void Report(string current) => progress?.Report(new TransferProgress(filesDone, totalFiles, bytesDone, totalBytes, current));

        try
        {
            foreach (var (source, isFolder, files) in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(destination, Path.GetFileName(source));
                // "Manter os dois" com pasta: a nova ganha número ("Viagem (2)") em vez de mesclar; copiar um item para a própria pasta também.
                if ((File.Exists(target) || Directory.Exists(target)) && (request.Policy == ConflictPolicy.KeepBoth && isFolder || string.Equals(target, source, StringComparison.OrdinalIgnoreCase)))
                    target = TransferConflicts.KeepBothName(destination, Path.GetFileName(source), p => File.Exists(p) || Directory.Exists(p), isFolder);
                var sameVolume = SameVolume(source, destination);

                if (isFolder && request.Move && sameVolume && !Directory.Exists(target) && !File.Exists(target))
                {
                    // Mesmo disco e sem conflito: a pasta inteira muda de lugar de uma vez (instantâneo).
                    Report(source);
                    await Task.Run(() => Directory.Move(source, target), cancellationToken);
                    moves.Add((source, target));
                    catalogMoves.Add((source, target));
                    filesDone += files.Count; bytesDone += files.Sum(f => f.Length);
                    Report(target);
                    continue;
                }

                var targetExisted = Directory.Exists(target) || File.Exists(target);
                if (isFolder)
                {
                    Directory.CreateDirectory(target);
                    foreach (var directory in Directory.EnumerateDirectories(source, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                        Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
                }

                var trackFiles = !isFolder || targetExisted;                     // pasta mesclada: desfazer precisa de cada arquivo
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fileTarget = isFolder ? Path.Combine(target, Path.GetRelativePath(source, file.FullName)) : target;
                    if (File.Exists(fileTarget) || Directory.Exists(fileTarget))
                    {
                        switch (request.Policy)
                        {
                            case ConflictPolicy.Skip:
                                skipped++; filesDone++; bytesDone += file.Length; Report(file.FullName);
                                continue;
                            case ConflictPolicy.KeepBoth:
                                fileTarget = TransferConflicts.KeepBothName(Path.GetDirectoryName(fileTarget)!, Path.GetFileName(fileTarget), p => File.Exists(p) || Directory.Exists(p));
                                break;
                            case ConflictPolicy.Replace:
                                await Task.Run(() => Recycle(fileTarget), cancellationToken);
                                break;
                        }
                    }
                    try
                    {
                        Report(file.FullName);
                        var before = bytesDone;
                        if (request.Move && sameVolume) await Task.Run(() => MoveItem(file.FullName, fileTarget, isFolder: false), cancellationToken);
                        else
                        {
                            await CopyFileAsync(file.FullName, fileTarget, read => { bytesDone += read; Report(file.FullName); }, cancellationToken);
                            CopySidecar(file.FullName, fileTarget);
                            if (request.Move) { DeleteSidecarOf(file.FullName); File.Delete(file.FullName); }
                        }
                        bytesDone = before + file.Length;
                        filesDone++;
                        if (request.Move) { catalogMoves.Add((file.FullName, fileTarget)); if (trackFiles) moves.Add((file.FullName, fileTarget)); }
                        else if (trackFiles) created.Add(fileTarget);
                        Report(fileTarget);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add((file.FullName, ex.Message)); filesDone++; }
                }

                if (isFolder && !targetExisted)
                {
                    if (request.Move) { moves.Add((source, target)); TryRemoveEmptyTree(source); }
                    else created.Add(target);
                }
                else if (isFolder && request.Move) TryRemoveEmptyTree(source);
            }
        }
        catch (OperationCanceledException) { cancelled = true; }

        if (catalogMoves.Count > 0) await FollowInCatalogAsync(catalogMoves, CancellationToken.None);
        return new TransferBatchResult(moves, created, filesDone - skipped - failed.Count, skipped, failed, cancelled);
    }

    public async Task<IReadOnlyList<(string Path, string Error)>> MovePathsAsync(IReadOnlyList<(string From, string To)> moves, CancellationToken cancellationToken = default)
    {
        var failed = new List<(string, string)>();
        var done = new List<(string, string)>();
        // Trocas (a.jpg → b.jpg e b.jpg → a.jpg, comum em renomear em lote/desfazer): quem ocupa o nome de outro passa antes por um nome temporário.
        var sources = moves.Select(m => m.From).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in moves.Where(m => sources.Contains(m.To) && !string.Equals(m.From, m.To, StringComparison.OrdinalIgnoreCase)))
        {
            // mantém a extensão no nome temporário para o .xmp acompanhar
            var temporary = Path.Combine(Path.GetDirectoryName(from)!, $"{Path.GetFileNameWithoutExtension(from)}.pmswap{Guid.NewGuid().ToString("N")[..6]}{Path.GetExtension(from)}");
            try { await Task.Run(() => MoveItem(from, temporary, Directory.Exists(from)), cancellationToken); staged[from] = temporary; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add((from, ex.Message)); }
        }
        foreach (var (original, to) in moves)
        {
            if (failed.Any(f => string.Equals(f.Item1, original, StringComparison.OrdinalIgnoreCase))) continue;
            var from = staged.GetValueOrDefault(original, original);
            try
            {
                var isFolder = Directory.Exists(from);
                if (!isFolder && !File.Exists(from)) throw new IOException("O item não existe mais.");
                if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && (File.Exists(to) || Directory.Exists(to))) throw new IOException($"Já existe “{Path.GetFileName(to)}” no destino.");
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (isFolder && !SameVolume(from, to)) throw new IOException("Pastas só podem voltar para o mesmo disco.");
                    if (!isFolder && !SameVolume(from, to)) { File.Copy(from, to); File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from)); CopySidecar(from, to); DeleteSidecarOf(from); File.Delete(from); }
                    else MoveItem(from, to, isFolder);
                }, cancellationToken);
                done.Add((original, to));                                          // o catálogo conhece o caminho original, não o temporário
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((original, ex.Message));
                if (!string.Equals(from, original, StringComparison.OrdinalIgnoreCase)) try { MoveItem(from, original, Directory.Exists(from)); } catch (IOException) { }
            }
        }
        if (done.Count > 0) await FollowInCatalogAsync(done, cancellationToken);
        return failed;
    }

    public Task<IReadOnlyList<string>> RemoveEmptyFoldersAsync(IReadOnlyList<string> folders, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var kept = new List<string>();
        foreach (var folder in folders.OrderByDescending(f => f.Length))
        {
            if (!Directory.Exists(folder)) continue;
            if (Directory.EnumerateFileSystemEntries(folder).Any()) { kept.Add(folder); continue; }
            try { Directory.Delete(folder); } catch (IOException) { kept.Add(folder); }
        }
        return kept;
    }, cancellationToken);

    // ---------- cores ----------

    public async Task<IReadOnlyDictionary<string, PhotoColor>> GetColorsAsync(string folder, CancellationToken cancellationToken = default)
    {
        var entries = await repository.GetEntriesUnderAsync(folder, recursive: false, cancellationToken);
        var result = new Dictionary<string, PhotoColor>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) result[entry.Path] = entry.ColorLabel;
        return result;
    }

    public async Task<TransferColorResult> SetColorAsync(IReadOnlyList<string> paths, PhotoColor color, bool addMissing, CancellationToken cancellationToken = default)
    {
        var media = paths.Where(MediaFormats.IsSupported).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var skipped = paths.Count - media.Count;                                                   // pastas e arquivos que não são foto/vídeo
        var known = await LookupAsync(media, cancellationToken);
        var added = 0;
        if (addMissing)
        {
            var missing = media.Where(p => !known.ContainsKey(p)).ToList();
            if (missing.Count > 0)
            {
                added = (await catalog.ImportPathsAsync(missing, cancellationToken: cancellationToken)).Imported;
                known = await LookupAsync(media, cancellationToken);
            }
        }
        skipped += media.Count(p => !known.ContainsKey(p));
        var ids = media.Where(known.ContainsKey).Select(p => known[p]).ToList();
        await repository.UpdateColorLabelAsync(ids, color, cancellationToken);
        if (added > 0) CatalogChanged?.Invoke(this, new TransferCatalogChange(media, null));
        else if (ids.Count > 0) CatalogChanged?.Invoke(this, new TransferCatalogChange(media.Where(known.ContainsKey).ToList(), color));
        return new TransferColorResult(ids.Count, added, skipped);
    }

    public async Task<IReadOnlyDictionary<string, PhotoColor>> GetFolderColorsAsync(string parent, CancellationToken cancellationToken = default) =>
        _transfer is null ? new Dictionary<string, PhotoColor>() : await _transfer.GetFolderColorsAsync(parent, cancellationToken);

    public Task SetFolderColorAsync(IReadOnlyList<string> folders, PhotoColor color, CancellationToken cancellationToken = default) =>
        _transfer?.SetFolderColorAsync(folders, color, cancellationToken) ?? Task.CompletedTask;

    // ---------- duplicatas, datas e detalhes ----------

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FindDuplicatesAsync(IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (_transfer is null || files.Count == 0) return result;
        var infos = files.Select(f => new FileInfo(f)).Where(f => f.Exists && f.Length > 0).ToList();
        var matches = (await _transfer.GetSizeMatchesAsync(infos.Select(f => f.Length).ToHashSet(), cancellationToken)).ToLookup(m => m.Size);
        var candidateHashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in infos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var others = matches[file.Length].Where(m => !string.Equals(m.Path, file.FullName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (others.Count == 0) continue;
            string own;
            try { own = await _hash.ComputeSha256Async(file.FullName, cancellationToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var same = new List<string>();
            foreach (var other in others)
            {
                if (!candidateHashes.TryGetValue(other.Path, out var hash))
                {
                    hash = other.ContentHash is { Length: > 0 } stored && other.HashedAtSize == other.Size ? stored : null;
                    if (hash is null && File.Exists(other.Path))
                        try { hash = await _hash.ComputeSha256Async(other.Path, cancellationToken); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    candidateHashes[other.Path] = hash;
                }
                if (string.Equals(hash, own, StringComparison.OrdinalIgnoreCase)) same.Add(other.Path);
            }
            if (same.Count > 0) result[file.FullName] = same;
        }
        return result;
    }

    public async Task<CaptureDate> GetCaptureDateAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if ((await repository.FindByPathAsync(path, cancellationToken))?.DateTaken is { } cataloged) return new(cataloged, true);
            if (ImageFormats.IsSupported(path) && metadataReader is not null && (await metadataReader.ReadAsync(path, cancellationToken)).DateTaken is { } exif) return new(exif, true);
            if (MediaFormats.IsVideo(path) && infoReader is not null && (await infoReader.ReadVideoInfoAsync(path, cancellationToken)).CreatedUtc is { } video) return new(video.ToLocalTime(), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        return new(File.GetLastWriteTime(path), false);
    }

    public async Task<TransferItemDetails> GetDetailsAsync(string path, CancellationToken cancellationToken = default)
    {
        var rows = new List<(string, string)>();
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            rows.Add(("Pasta", info.Name));
            var (files, folders) = await Task.Run(() =>
            {
                try { return (info.EnumerateFiles().Count(), info.EnumerateDirectories().Count()); } catch (Exception) { return (-1, -1); }
            }, cancellationToken);
            if (files >= 0) rows.Add(("Conteúdo", $"{files} arquivo(s), {folders} pasta(s)"));
            rows.Add(("Modificada", info.LastWriteTime.ToString("dd/MM/yyyy HH:mm", culture)));
            if (_transfer is not null && (await _transfer.GetFolderColorsAsync(info.Parent?.FullName ?? path, cancellationToken)).TryGetValue(info.FullName, out var folderColor))
                rows.Add(("Cor", PhotoColors.Name(folderColor)));
            rows.Add(("Local", info.FullName));
            return new(rows);
        }
        if (!File.Exists(path)) return new([("Arquivo", "Não existe mais.")]);
        var file = new FileInfo(path);
        rows.Add(("Arquivo", file.Name));
        rows.Add(("Tamanho", FormatSize(file.Length)));
        try
        {
            if (MediaFormats.IsVideo(path) && infoReader is not null)
            {
                var video = await infoReader.ReadVideoInfoAsync(path, cancellationToken);
                if (video.Width is { } w && video.Height is { } h) rows.Add(("Resolução", $"{w} × {h}"));
                if (video.DurationSeconds is { } seconds) rows.Add(("Duração", TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss")));
                if (video.CreatedUtc is { } created) rows.Add(("Gravado em", created.ToLocalTime().ToString("dd/MM/yyyy HH:mm", culture)));
            }
            else if (ImageFormats.IsSupported(path))
            {
                if (infoReader is not null)
                {
                    var (w, h) = await infoReader.ReadDimensionsAsync(path, cancellationToken);
                    if (w > 0 && h > 0) rows.Add(("Resolução", w * (double)h >= 500_000 ? $"{w} × {h}  ({w * (double)h / 1_000_000:0.#} MP)" : $"{w} × {h}"));
                }
                if (metadataReader is not null)
                {
                    var meta = await metadataReader.ReadAsync(path, cancellationToken);
                    if (meta.DateTaken is { } taken) rows.Add(("Data da foto", taken.ToString("dd/MM/yyyy HH:mm", culture)));
                    if (meta.Camera is { Length: > 0 } camera) rows.Add(("Câmera", camera));
                    if (meta.Lens is { Length: > 0 } lens) rows.Add(("Lente", lens));
                    var exposure = string.Join("  ·  ", new[] { meta.FocalLength, meta.Aperture, meta.ExposureTime, meta.Iso is { } iso ? $"ISO {iso}" : null }.Where(v => !string.IsNullOrWhiteSpace(v)));
                    if (exposure.Length > 0) rows.Add(("Exposição", exposure));
                    if (meta.HasGps) rows.Add(("GPS", $"{meta.Latitude:0.#####}, {meta.Longitude:0.#####}"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { rows.Add(("Leitura", "Não foi possível ler os metadados.")); }
        rows.Add(("Modificado", file.LastWriteTime.ToString("dd/MM/yyyy HH:mm", culture)));
        if (MediaFormats.IsSupported(path))
        {
            var cataloged = await repository.FindByPathAsync(file.FullName, cancellationToken);
            rows.Add(("Catálogo", cataloged is null ? "Não está no catálogo" : cataloged.ColorLabel == PhotoColor.None ? "Sim" : $"Sim · cor {PhotoColors.Name(cataloged.ColorLabel).ToLowerInvariant()}"));
        }
        rows.Add(("Local", file.DirectoryName ?? string.Empty));
        return new(rows);
    }

    // ---------- apoio ----------

    /// <summary>Catálogo e cores de pasta seguem itens que mudaram de caminho (arquivo ou pasta inteira).</summary>
    private async Task<int> FollowInCatalogAsync(IReadOnlyList<(string From, string To)> moved, CancellationToken cancellationToken)
    {
        var updates = new List<(long, string)>();
        foreach (var group in moved.GroupBy(m => Directory.Exists(m.To)))
        {
            if (group.Key)
                foreach (var (from, to) in group)
                {
                    updates.AddRange((await repository.GetEntriesUnderAsync(from, recursive: true, cancellationToken)).Select(e => (e.Id, TransferNames.Rebase(e.Path, from, to))));
                    if (_transfer is not null) await _transfer.RebaseFolderColorsAsync(from, to, cancellationToken);
                }
            else
                foreach (var folder in group.GroupBy(m => Path.GetDirectoryName(m.From) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    var byPath = folder.ToDictionary(m => m.From, m => m.To, StringComparer.OrdinalIgnoreCase);
                    updates.AddRange((await repository.GetEntriesUnderAsync(folder.Key, recursive: false, cancellationToken)).Where(e => byPath.ContainsKey(e.Path)).Select(e => (e.Id, byPath[e.Path])));
                }
        }
        await repository.UpdatePathsAsync(updates, cancellationToken);
        if (updates.Count > 0) CatalogChanged?.Invoke(this, new TransferCatalogChange(moved.SelectMany(m => new[] { m.From, m.To }).ToList(), null));
        return updates.Count;
    }

    /// <summary>Caminho → Id do catálogo, uma consulta por pasta envolvida.</summary>
    private async Task<Dictionary<string, long>> LookupAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in paths.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var entry in await repository.GetEntriesUnderAsync(folder, recursive: false, cancellationToken))
                result[entry.Path] = entry.Id;
        return result;
    }

    /// <summary>Copia para "nome.pmpart" e só renomeia no fim, preservando as datas do original (fotógrafos dependem delas).</summary>
    private static async Task CopyFileAsync(string source, string destination, Action<int> onRead, CancellationToken cancellationToken)
    {
        var temporary = destination + ".pmpart";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    onRead(read);
                }
            }
            File.SetCreationTimeUtc(temporary, File.GetCreationTimeUtc(source));
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            File.Move(temporary, destination);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Todos os arquivos da pasta (recursivo), menos o .xmp de cada mídia — ele vai junto com ela, não como item próprio.</summary>
    private static IReadOnlyList<FileInfo> EnumerateFiles(string folder)
    {
        var all = new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System }).ToList();
        var sidecars = all.Where(f => XmpSidecar.UsesSidecar(f.FullName)).Select(f => XmpSidecar.PathFor(f.FullName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all.Where(f => !sidecars.Contains(f.FullName)).ToList();
    }

    private static void MoveItem(string from, string to, bool isFolder)
    {
        if (isFolder) { MoveDirectory(from, to); return; }
        File.Move(from, to);
        var sidecar = XmpSidecar.PathFor(from);
        if (XmpSidecar.UsesSidecar(from) && File.Exists(sidecar) && !File.Exists(XmpSidecar.PathFor(to))) File.Move(sidecar, XmpSidecar.PathFor(to));
    }

    private static void CopySidecar(string sourceMedia, string destinationMedia)
    {
        if (!XmpSidecar.UsesSidecar(sourceMedia)) return;
        var from = XmpSidecar.PathFor(sourceMedia);
        var to = XmpSidecar.PathFor(destinationMedia);
        if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
    }

    private static void DeleteSidecarOf(string media)
    {
        if (XmpSidecar.UsesSidecar(media) && File.Exists(XmpSidecar.PathFor(media))) File.Delete(XmpSidecar.PathFor(media));
    }

    private static void Recycle(string path)
    {
        if (Directory.Exists(path)) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else if (File.Exists(path))
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            var sidecar = XmpSidecar.PathFor(path);
            if (XmpSidecar.UsesSidecar(path) && File.Exists(sidecar)) FileSystem.DeleteFile(sidecar, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        else throw new IOException("O item não existe mais.");
    }

    /// <summary>Depois de mover arquivo a arquivo, a pasta de origem vazia (e subpastas vazias) some — como no Explorer.</summary>
    private static void TryRemoveEmptyTree(string folder)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folder, "*", System.IO.SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Siblings(string folder) =>
        Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>();

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };

    /// <summary>Directory.Move não aceita trocar só maiúsculas/minúsculas ("fotos" → "Fotos"): passa por um nome temporário.</summary>
    private static void MoveDirectory(string source, string destination)
    {
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = destination + "~pm" + Guid.NewGuid().ToString("N")[..6];
            Directory.Move(source, temporary);
            Directory.Move(temporary, destination);
        }
        else Directory.Move(source, destination);
    }
}
