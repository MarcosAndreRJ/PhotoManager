namespace PhotoManager.Domain.Collections;

public sealed class Collection
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long? ParentCollectionId { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
}
