using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Workbench;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["WorkbenchUrl"] ?? "http://127.0.0.1:5187");
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(
    new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
var dataRoot = Path.GetFullPath(builder.Configuration["DataRoot"] ??
    Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data"));
Directory.CreateDirectory(dataRoot);
using var instanceLock = new FileStream(Path.Combine(dataRoot, "instance.lock"),
    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
builder.Services.AddDbContextFactory<WorkflowDb>(o =>
    o.UseSqlite($"Data Source={Path.Combine(dataRoot, "workflow.db")};Default Timeout=30"));
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton(new Workspace(Path.Combine(dataRoot, "runs"),
    Path.Combine(builder.Environment.ContentRootPath, "Templates")));
builder.Services.AddSingleton<DemoAgentProvider>();
builder.Services.AddSingleton<CopilotAgentProvider>();
builder.Services.AddSingleton<IValidationRunner, ValidationRunner>();
builder.Services.AddSingleton<Orchestrator>();
builder.Services.AddHostedService(s => s.GetRequiredService<Orchestrator>());
var app = builder.Build();
await app.Services.GetRequiredService<RunStore>().Initialize();
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var host = context.Request.Host.Host;
    if ((host is not ("localhost" or "127.0.0.1" or "::1")) ||
        (context.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip)))
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.ContentLength > 32_000)
        { context.Response.StatusCode = 413; return; }
        if (context.Request.Headers.Origin is { Count: > 0 } origin &&
            origin.ToString() != $"{context.Request.Scheme}://{context.Request.Host}")
        { context.Response.StatusCode = 403; return; }
        if (context.Request.Method != "GET" &&
            context.Request.Headers["X-Workbench-Token"].ToString() != token)
        { context.Response.StatusCode = 403; return; }
        if (context.Request.Method != "GET" &&
            !context.RequestServices.GetRequiredService<Orchestrator>().Ready)
        { context.Response.StatusCode = 503; return; }
    }
    try { await next(context); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
    {
        var status = ex is KeyNotFoundException ? 404 : ex is ArgumentException ? 400 : 409;
        await Results.Problem(ex.Message, statusCode: status).ExecuteAsync(context);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/session", () => new
{
    token, mode = "LOCAL OPERATOR - not multi-user authentication",
    defaults = new { provider = "demo", maxParallel = 3, maxRepairs = 1, maxProviderRetries = 1,
        maxCalls = 24, maxRevisions = 5, maxRunMinutes = 120, maxStageSeconds = 180 }
});
app.MapGet("/health", (Orchestrator engine) => engine.Ready
    ? Results.Ok(new { status = "healthy" }) : Results.StatusCode(503));
app.MapGet("/api/runs", (RunStore store) => store.All());
app.MapGet("/api/metrics", async (RunStore store) => Metrics.Overall(await store.All()));
app.MapPost("/api/runs", async (RunRequest request, Orchestrator engine) =>
{
    var run = await engine.Create(request);
    return Results.Created($"/api/runs/{run.Id}", run);
});
app.MapGet("/api/runs/{id}", async (string id, RunStore store) =>
{
    var run = await store.Get(id);
    var events = await store.Events(id);
    return new RunView(run, events, Metrics.ForRun(run, events));
});
app.MapPost("/api/runs/{id}/clarify", async (string id, RevisionRequest r, Orchestrator engine) =>
{ await engine.Clarify(id, r); return Results.Ok(); });
app.MapPost("/api/runs/{id}/revise", async (string id, RevisionRequest r, Orchestrator engine) =>
{ await engine.Revise(id, r); return Results.Ok(); });
app.MapPost("/api/runs/{id}/approve/{stage}", async (string id, string stage, Decision decision, Orchestrator engine) =>
{ await engine.Approve(id, stage, decision); return Results.Ok(); });
app.MapPost("/api/runs/{id}/stop", async (string id, Orchestrator engine) =>
{ await engine.Stop(id, "Operator requested safe stop and restoration of approved baseline."); return Results.Ok(); });
app.MapPost("/api/runs/{id}/resume", async (string id, Orchestrator engine) =>
{ await engine.Resume(id); return Results.Ok(); });
app.MapGet("/api/runs/{id}/artifacts/{revision:int}/{name}", async (
    string id, int revision, string name, RunStore store, Workspace workspace) =>
{
    var run = await store.Get(id);
    var artifact = run.Artifacts.SingleOrDefault(a => a.Name == name && a.Revision == revision)
        ?? throw new KeyNotFoundException("Artifact not registered.");
    var path = Path.Combine(workspace.RunRoot(id), $"revision-{revision}", "evidence", artifact.Name);
    var text = await File.ReadAllTextAsync(path);
    if (Json.Hash(text) != artifact.Sha256) throw new InvalidDataException("Artifact integrity mismatch.");
    return Results.Text(text, "text/plain");
});
app.MapGet("/api/runs/{id}/source", async (string id, RunStore store, Workspace workspace) =>
{
    var run = await store.Get(id);
    return Results.Ok(Workspace.SourceFiles.Select(name => new
    {
        name, content = File.Exists(Path.Combine(workspace.Candidate(run), name))
            ? File.ReadAllText(Path.Combine(workspace.Candidate(run), name)) : "(not generated yet)"
    }));
});
app.MapGet("/api/runs/{id}/release", async (string id, RunStore store, Workspace workspace) =>
{
    var run = await store.Get(id);
    if (run.Status != "Completed") throw new InvalidOperationException("No approved release.");
    return Results.File(Path.Combine(workspace.RunRoot(id), "release.zip"), "application/zip", $"shortener-{id[..8]}.zip");
});
app.Run();
