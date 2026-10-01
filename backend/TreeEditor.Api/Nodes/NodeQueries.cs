using Npgsql;

namespace TreeEditor.Api.Nodes;

/// <summary>Read-only queries used by the tree views. Each query returns one level at most, never the whole tree.</summary>
public sealed class NodeQueries(NpgsqlDataSource dataSource)
{
    private const string SelectNode = """
        SELECT n.id, n.parent_id, n.value, n.is_deleted, n.path,
               EXISTS (SELECT 1 FROM tree_nodes c WHERE c.parent_id = n.id) AS has_children
        FROM tree_nodes n
        """;

    public async Task<IReadOnlyList<NodeDto>> GetRootsAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ReadAsync(connection, null, $"{SelectNode} WHERE n.parent_id IS NULL ORDER BY n.id", ct);
    }

    public async Task<IReadOnlyList<NodeDto>> GetChildrenAsync(long parentId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ReadAsync(connection, null, $"{SelectNode} WHERE n.parent_id = @id ORDER BY n.id", ct,
            new NpgsqlParameter("id", parentId));
    }

    public async Task<NodeDto?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var nodes = await ReadAsync(connection, null, $"{SelectNode} WHERE n.id = @id", ct,
            new NpgsqlParameter("id", id));
        return nodes.SingleOrDefault();
    }

    /// <summary>Loads the given nodes using an existing connection/transaction.</summary>
    public static Task<IReadOnlyList<NodeDto>> GetManyAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, IReadOnlyCollection<long> ids, CancellationToken ct) =>
        ReadAsync(connection, tx, $"{SelectNode} WHERE n.id = ANY(@ids) ORDER BY n.id", ct,
            new NpgsqlParameter("ids", ids.ToArray()));

    /// <summary>Parses '/1/4/9/' into [1, 4, 9].</summary>
    public static long[] ParsePath(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();

    private static async Task<IReadOnlyList<NodeDto>> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string sql, CancellationToken ct,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddRange(parameters);

        var result = new List<NodeDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var pathIds = ParsePath(reader.GetString(4));
            result.Add(new NodeDto(
                Id: reader.GetInt64(0),
                ParentId: reader.IsDBNull(1) ? null : reader.GetInt64(1),
                Value: reader.GetString(2),
                IsDeleted: reader.GetBoolean(3),
                AncestorIds: pathIds[..^1],
                HasChildren: reader.GetBoolean(5)));
        }

        return result;
    }
}
