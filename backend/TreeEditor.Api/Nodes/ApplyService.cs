using System.Data;
using Npgsql;

namespace TreeEditor.Api.Nodes;

public sealed class ApplyService(NpgsqlDataSource dataSource)
{
    public const int MaxValueLength = 200;

    private sealed record ParentInfo(string Path, int Depth, bool IsDeleted);

    public async Task<ApplyResponse> ApplyAsync(ApplyRequest request, CancellationToken ct)
    {
        var added = request.Added ?? [];
        var updated = request.Updated ?? [];
        var deleted = (request.Deleted ?? []).Distinct().ToList();
        var cachedIds = (request.CachedIds ?? []).Distinct().ToList();

        Validate(added, updated, deleted, cachedIds);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Serialize concurrent Apply/Reset calls; plain reads are not blocked.
        await using (var lockCmd = new NpgsqlCommand("LOCK TABLE tree_nodes IN SHARE ROW EXCLUSIVE MODE", connection, tx))
        {
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        var (idMap, skipped) = await InsertAsync(connection, tx, added, ct);
        var updatedCount = await UpdateAsync(connection, tx, updated, ct);
        var deletedCount = await DeleteAsync(connection, tx, deleted, ct);

        var refreshIds = cachedIds
            .Concat(idMap.Select(m => m.Id))
            .Distinct()
            .ToList();

        var nodes = await NodeQueries.GetManyAsync(connection, tx, refreshIds, ct);

        await tx.CommitAsync(ct);
        return new ApplyResponse(idMap, skipped, nodes, idMap.Count, updatedCount, deletedCount);
    }

    private static void Validate(
        IReadOnlyList<AddedNode> added,
        IReadOnlyList<UpdatedNode> updated,
        IReadOnlyList<long> deleted,
        IReadOnlyList<long> cachedIds)
    {
        foreach (var value in added.Select(a => a.Value).Concat(updated.Select(u => u.Value)))
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ApplyValidationException("Value must not be empty.");

            if (value.Trim().Length > MaxValueLength)
                throw new ApplyValidationException($"Value must not be longer than {MaxValueLength} characters.");
        }

        if (added.Any(a => a.TempId >= 0))
            throw new ApplyValidationException("Temporary ids of new nodes must be negative.");

        if (added.Select(a => a.TempId).Distinct().Count() != added.Count)
            throw new ApplyValidationException("Temporary ids of new nodes must be unique.");

        var tempIds = added.Select(a => a.TempId).ToHashSet();

        if (added.Any(a => a.ParentId == 0 || (a.ParentId < 0 && !tempIds.Contains(a.ParentId))))
            throw new ApplyValidationException("A new node references an unknown parent.");

        if (updated.Any(u => u.Id <= 0) || deleted.Any(id => id <= 0) || cachedIds.Any(id => id <= 0))
            throw new ApplyValidationException("Updated, deleted and cached ids must be database ids.");

        if (updated.Select(u => u.Id).Distinct().Count() != updated.Count)
            throw new ApplyValidationException("A node can be updated only once per request.");
    }

    private static async Task<(List<IdMapping> IdMap, List<long> Skipped)> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<AddedNode> added, CancellationToken ct)
    {
        var idMap = new List<IdMapping>();
        var skipped = new List<long>();

        if (added.Count == 0) return (idMap, skipped);

        // Parents of new nodes: existing database nodes, and new nodes once they are inserted.
        var parentIds = added
            .Where(a => a.ParentId > 0)
            .Select(a => a.ParentId)
            .Distinct()
            .ToArray();

        var parents = await LoadParentsAsync(
            connection,
            tx,
            parentIds,
            ct);

        var insertedByTempId = new Dictionary<long, (long Id, ParentInfo Info)>();

        const string insertSql = """
            WITH new_id AS (SELECT nextval(pg_get_serial_sequence('tree_nodes', 'id')) AS id)
            INSERT INTO tree_nodes (id, parent_id, value, path, depth)
            SELECT id, @parentId, @value, @parentPath || id || '/', @depth FROM new_id
            RETURNING id, path
            """;

        foreach (var node in TopologicalOrder(added))
        {
            long parentId;
            ParentInfo? parent;

            if (node.ParentId > 0)
            {
                parentId = node.ParentId;
                parent = parents.GetValueOrDefault(node.ParentId);
            }
            else if (insertedByTempId.TryGetValue(node.ParentId, out var newParent))
            {
                parentId = newParent.Id;
                parent = newParent.Info;
            }
            else
            {
                // The new parent itself was skipped.
                skipped.Add(node.TempId);
                continue;
            }

            if (parent is null || parent.IsDeleted)
            {
                skipped.Add(node.TempId);
                continue;
            }

            await using var insert = new NpgsqlCommand(insertSql, connection, tx);

            insert.Parameters.AddWithValue("parentId", parentId);
            insert.Parameters.AddWithValue("value", node.Value.Trim());
            insert.Parameters.AddWithValue("parentPath", parent.Path);
            insert.Parameters.AddWithValue("depth", parent.Depth + 1);

            await using var reader = await insert.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);

            var id = reader.GetInt64(0);
            var path = reader.GetString(1);

            insertedByTempId[node.TempId] = (id, new ParentInfo(path, parent.Depth + 1, false));
            idMap.Add(new IdMapping(node.TempId, id));
        }

        return (idMap, skipped);
    }

    private static List<AddedNode> TopologicalOrder(IReadOnlyList<AddedNode> added)
    {
        var childrenByParent = added.ToLookup(a => a.ParentId);
        var ordered = new List<AddedNode>(added.Count);
        var queue = new Queue<AddedNode>(added.Where(a => a.ParentId > 0));

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            ordered.Add(node);

            foreach (var child in childrenByParent[node.TempId])
            {
                queue.Enqueue(child);
            }
        }

        if (ordered.Count != added.Count)
            throw new ApplyValidationException("New nodes contain a cycle.");

        return ordered;
    }

    private static async Task<Dictionary<long, ParentInfo>> LoadParentsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, long[] ids, CancellationToken ct)
    {
        var result = new Dictionary<long, ParentInfo>();
        if (ids.Length == 0) return result;

        await using var command = new NpgsqlCommand(
            "SELECT id, path, depth, is_deleted FROM tree_nodes WHERE id = ANY(@ids)", connection, tx);

        command.Parameters.AddWithValue("ids", ids);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetInt64(0)] = new ParentInfo(
                Path: reader.GetString(1),
                Depth: reader.GetInt32(2), 
                IsDeleted: reader.GetBoolean(3));
        }

        return result;
    }

    private static async Task<int> UpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<UpdatedNode> updated, CancellationToken ct)
    {
        if (updated.Count == 0) return 0;

        const string sql = """
            UPDATE tree_nodes t
            SET value = u.value, updated_at = now()
            FROM unnest(@ids, @values) AS u(id, value)
            WHERE t.id = u.id AND NOT t.is_deleted AND t.value <> u.value
            """;

        await using var command = new NpgsqlCommand(sql, connection, tx);

        command.Parameters.AddWithValue("ids", updated.Select(u => u.Id).ToArray());
        command.Parameters.AddWithValue("values", updated.Select(u => u.Value.Trim()).ToArray());

        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> DeleteAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<long> deleted, CancellationToken ct)
    {
        if (deleted.Count == 0) return 0;

        var paths = new List<string>();
        await using (var select = new NpgsqlCommand("SELECT path FROM tree_nodes WHERE id = ANY(@ids)", connection, tx))
        {
            select.Parameters.AddWithValue("ids", deleted.ToArray());

            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) paths.Add(reader.GetString(0));
        }

        // One prefix query per deleted node marks the node and its entire subtree,
        // including descendants that were never loaded into the cache.
        // Paths contain only digits and '/', so they are safe to use as LIKE prefixes.
        var total = 0;
        foreach (var path in paths)
        {
            await using var update = new NpgsqlCommand(
                "UPDATE tree_nodes SET is_deleted = TRUE, updated_at = now() WHERE path LIKE @prefix AND NOT is_deleted",
                connection, tx);

            update.Parameters.AddWithValue("prefix", path + "%");
            total += await update.ExecuteNonQueryAsync(ct);
        }

        return total;
    }
}
