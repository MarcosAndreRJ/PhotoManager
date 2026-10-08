using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Metadata;

public sealed class BatchMetadataService(IMetadataReader reader, IMetadataEditService editor) : IBatchMetadataService
{
    public async Task<IReadOnlyList<BatchItemResult>> PreviewAsync(IReadOnlyList<Photo> photos, BatchMetadataPlan plan, CancellationToken cancellationToken = default)
    {
        var results = new List<BatchItemResult>(photos.Count);
        foreach (var photo in photos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await EvaluateAsync(photo, plan, BatchItemStatus.WillChange, cancellationToken));
        }
        return results;
    }

    public async Task<BatchRunResult> ApplyAsync(IReadOnlyList<Photo> photos, BatchMetadataPlan plan, IProgress<BatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var problems = plan.Validate();
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems), nameof(plan));
        var results = new List<BatchItemResult>(photos.Count);
        for (var index = 0; index < photos.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var photo = photos[index];
            progress?.Report(new BatchProgress(index, photos.Count, photo.FileName));
            var evaluation = await EvaluateAsync(photo, plan, BatchItemStatus.Changed, cancellationToken);
            if (evaluation.Status != BatchItemStatus.Changed) { results.Add(evaluation); continue; }
            try
            {
                var saved = await editor.SaveAsync(photo, plan.Apply(MetadataEdit.From(await reader.ReadAsync(photo.CurrentPath, cancellationToken))), cancellationToken);
                results.Add(new BatchItemResult(photo, saved.Changed ? BatchItemStatus.Changed : BatchItemStatus.Unchanged, saved.ChangedFields, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new BatchItemResult(photo, BatchItemStatus.Failed, [], ex.Message));
            }
        }
        progress?.Report(new BatchProgress(photos.Count, photos.Count, string.Empty));
        return new BatchRunResult(results);
    }

    private async Task<BatchItemResult> EvaluateAsync(Photo photo, BatchMetadataPlan plan, BatchItemStatus changeStatus, CancellationToken cancellationToken)
    {
        if (photo.IsMissing || !File.Exists(photo.CurrentPath)) return new BatchItemResult(photo, BatchItemStatus.Skipped, [], "Arquivo ausente.");
        if (!editor.CanEdit(photo)) return new BatchItemResult(photo, BatchItemStatus.Skipped, [], "Formato somente leitura (só JPEG é gravável).");
        try
        {
            var current = MetadataEdit.From(await reader.ReadAsync(photo.CurrentPath, cancellationToken));
            var changed = current.ChangedFields(plan.Apply(current));
            return changed.Count == 0
                ? new BatchItemResult(photo, BatchItemStatus.Unchanged, [], null)
                : new BatchItemResult(photo, changeStatus, changed, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BatchItemResult(photo, BatchItemStatus.Failed, [], ex.Message);
        }
    }
}
