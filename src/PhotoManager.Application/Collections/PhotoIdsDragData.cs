namespace PhotoManager.Application.Collections;

public static class CollectionDragDropFormats
{
    public const string PhotoIds = "PhotoManager.PhotoIds";
    public const string CollectionId = "PhotoManager.CollectionId";
}

public sealed class PhotoIdsDragData(IReadOnlyList<long> photoIds, long? activeSourceCollectionId)
{
    public IReadOnlyList<long> PhotoIds { get; } = photoIds;
    public long? ActiveSourceCollectionId { get; } = activeSourceCollectionId;
}
