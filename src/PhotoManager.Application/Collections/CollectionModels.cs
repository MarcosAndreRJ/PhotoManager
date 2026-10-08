using PhotoManager.Domain.Collections;

namespace PhotoManager.Application.Collections;

public sealed record CollectionTreeNode(
    long Id,
    string Name,
    long? ParentCollectionId,
    int SortOrder,
    int DirectCount,
    IReadOnlyList<CollectionTreeNode> Children);

public sealed record CollectionTree(
    IReadOnlyList<CollectionTreeNode> Roots,
    int TotalDistinctInAny,
    int WithoutCollection);

public enum DeleteMode
{
    PromoteChildren,
    WithDescendants
}

public sealed record DeleteResult(
    long DeletedId,
    int DeletedCount,
    int PromotedCount,
    IReadOnlyList<(long Id, string OldName, string NewName)> RenamedChildren);

public sealed record MoveResult(
    bool Success,
    string? Message = null);

public sealed record MovePhotosResult(
    int MovedCount,
    int AlreadyInTargetCount);
