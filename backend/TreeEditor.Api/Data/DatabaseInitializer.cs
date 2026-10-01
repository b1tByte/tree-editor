using System.Reflection;
using Npgsql;

namespace TreeEditor.Api.Data;

/// <summary>Creates the schema on startup and (re)loads the sample data.</summary>
public sealed class DatabaseInitializer(NpgsqlDataSource dataSource, ILogger<DatabaseInitializer> logger)
{
    private const int MaxConnectAttempts = 30;

    /// <summary>Creates the schema if needed and seeds the sample tree when the table is empty.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await WaitForDatabaseAsync(ct);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using (var schema = new NpgsqlCommand(ReadSchemaScript(), connection))
        {
            await schema.ExecuteNonQueryAsync(ct);
        }

        await using var count = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM tree_nodes)", connection);
        var hasData = (bool)(await count.ExecuteScalarAsync(ct))!;
        if (!hasData)
        {
            logger.LogInformation("Database is empty, loading sample data");
            await ResetAsync(ct);
        }
    }

    /// <summary>Removes all nodes and restores the initial sample tree.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        var rows = Flatten(SeedData.Roots);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using (var truncate = new NpgsqlCommand("TRUNCATE tree_nodes RESTART IDENTITY", connection, tx))
        {
            await truncate.ExecuteNonQueryAsync(ct);
        }

        // Ids are assigned here (pre-order, starting at 1) so the sample tree is always identical.
        const string insertSql = """
            INSERT INTO tree_nodes (id, parent_id, value, path, depth)
            SELECT * FROM unnest(@ids, @parentIds, @values, @paths, @depths);

            SELECT setval(pg_get_serial_sequence('tree_nodes', 'id'), (SELECT max(id) FROM tree_nodes));
            """;
        await using (var insert = new NpgsqlCommand(insertSql, connection, tx))
        {
            insert.Parameters.AddWithValue("ids", rows.Select(r => r.Id).ToArray());
            insert.Parameters.AddWithValue("parentIds", rows.Select(r => r.ParentId).ToArray());
            insert.Parameters.AddWithValue("values", rows.Select(r => r.Value).ToArray());
            insert.Parameters.AddWithValue("paths", rows.Select(r => r.Path).ToArray());
            insert.Parameters.AddWithValue("depths", rows.Select(r => r.Depth).ToArray());
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        logger.LogInformation("Sample data loaded: {Count} nodes", rows.Count);
    }

    private sealed record SeedRow(long Id, long? ParentId, string Value, string Path, int Depth);

    private static List<SeedRow> Flatten(IEnumerable<SeedNode> roots)
    {
        var rows = new List<SeedRow>();
        long nextId = 1;

        void Visit(SeedNode node, SeedRow? parent)
        {
            var id = nextId++;
            var row = new SeedRow(
                id,
                parent?.Id,
                node.Value,
                (parent?.Path ?? "/") + id + "/",
                (parent?.Depth ?? -1) + 1);
            rows.Add(row);
            foreach (var child in node.Children) Visit(child, row);
        }

        foreach (var root in roots) Visit(root, null);
        return rows;
    }

    private async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException && attempt < MaxConnectAttempts)
            {
                logger.LogWarning("Database is not available yet (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static string ReadSchemaScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Schema.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
