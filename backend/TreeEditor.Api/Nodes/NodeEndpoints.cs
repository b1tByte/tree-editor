using TreeEditor.Api.Data;

namespace TreeEditor.Api.Nodes;

public static class NodeEndpoints
{
    public static void MapNodeEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // DBTreeView: lazy loading, one level per request.
        api.MapGet("/nodes/roots", (NodeQueries queries, CancellationToken ct) => queries.GetRootsAsync(ct));

        api.MapGet("/nodes/{id:long}/children", (long id, NodeQueries queries, CancellationToken ct) =>
            queries.GetChildrenAsync(id, ct));

        // Loads a single element into the cache (includes its ancestor chain).
        api.MapGet("/nodes/{id:long}", async (long id, NodeQueries queries, CancellationToken ct) =>
            await queries.GetAsync(id, ct) is { } node ? Results.Ok(node) : Results.NotFound());

        // Applies all pending cache changes in one transaction.
        api.MapPost("/nodes/apply", async (ApplyRequest request, ApplyService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.ApplyAsync(request, ct);
                return Results.Ok(result);
            }
            catch (ApplyValidationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid changes");
            }
        });

        // Restores the initial sample data.
        api.MapPost("/reset", async (DatabaseInitializer initializer, CancellationToken ct) =>
        {
            await initializer.ResetAsync(ct);
            return Results.NoContent();
        });
    }
}
