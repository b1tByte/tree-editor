namespace TreeEditor.Api.Nodes;

/// <summary>A tree node as seen by the client.</summary>
/// <param name="Id">Database id.</param>
/// <param name="ParentId">Parent id, or null for a root node.</param>
/// <param name="Value">Node value.</param>
/// <param name="IsDeleted">True when the node (or one of its ancestors) has been deleted.</param>
/// <param name="AncestorIds">Ids of all ancestors, ordered from the root down to the parent.</param>
/// <param name="HasChildren">True when the node has at least one child in the database.</param>
public sealed record NodeDto(
    long Id,
    long? ParentId,
    string Value,
    bool IsDeleted,
    long[] AncestorIds,
    bool HasChildren);

/// <summary>A node created in the client cache. Temporary ids are negative.</summary>
/// <param name="TempId">Temporary (negative) id assigned by the client.</param>
/// <param name="ParentId">Parent id: a database id (positive) or another new node's temporary id (negative).</param>
/// <param name="Value">Node value.</param>
public sealed record AddedNode(long TempId, long ParentId, string Value);

/// <summary>A changed value of an existing node.</summary>
public sealed record UpdatedNode(long Id, string Value);

/// <summary>All pending changes of the client cache, applied in a single transaction.</summary>
/// <param name="Added">Nodes created in the cache.</param>
/// <param name="Updated">Existing nodes whose value was edited.</param>
/// <param name="Deleted">Existing nodes deleted in the cache; their whole subtrees are deleted.</param>
/// <param name="CachedIds">Database ids currently held in the cache; their fresh state is returned.</param>
public sealed record ApplyRequest(
    IReadOnlyList<AddedNode>? Added,
    IReadOnlyList<UpdatedNode>? Updated,
    IReadOnlyList<long>? Deleted,
    IReadOnlyList<long>? CachedIds);

/// <summary>Maps a client temporary id to the id assigned by the database.</summary>
public sealed record IdMapping(long TempId, long Id);

/// <summary>Result of applying cache changes.</summary>
/// <param name="IdMap">Temporary id to database id mapping for inserted nodes.</param>
/// <param name="Skipped">Temporary ids of new nodes that were not inserted because their parent no longer exists or was deleted.</param>
/// <param name="Nodes">Fresh state of all cached and inserted nodes.</param>
/// <param name="InsertedCount">Number of inserted nodes.</param>
/// <param name="UpdatedCount">Number of nodes whose value was updated.</param>
/// <param name="DeletedCount">Number of nodes marked as deleted, including descendants that were not cached.</param>
public sealed record ApplyResponse(
    IReadOnlyList<IdMapping> IdMap,
    IReadOnlyList<long> Skipped,
    IReadOnlyList<NodeDto> Nodes,
    int InsertedCount,
    int UpdatedCount,
    int DeletedCount);

/// <summary>Thrown when an apply request is malformed.</summary>
public sealed class ApplyValidationException(string message) : Exception(message);
