using Npgsql;
using TreeEditor.Api.Data;
using TreeEditor.Api.Nodes;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("TreeEditor")
    ?? throw new InvalidOperationException("Connection string 'TreeEditor' is not configured.");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<NodeQueries>();
builder.Services.AddSingleton<ApplyService>();
builder.Services.AddProblemDetails();

// Only needed when the UI dev server is used without its proxy.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapNodeEndpoints();

await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();

app.Run();
