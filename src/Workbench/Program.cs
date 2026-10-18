using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Workbench;

var builder = WebApplication.CreateBuilder(args);

// Environment detection: enabled via PUBLIC_DEMO env var in container / public deployments
var isPublicDemo = builder.Configuration.GetValue<bool>("PUBLIC_DEMO") ||
                   builder.Configuration.GetValue<bool>("PublicDemo");

// Port & URL binding
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}
else
{
    builder.WebHost.UseUrls(builder.Configuration["WorkbenchUrl"] ?? "http://127.0.0.1:5187");
}

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(
    new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

// Data root resolution (supports container DATA_ROOT, local ../../data, or ./data)
var configuredDataRoot = builder.Configuration["DataRoot"] ?? builder.Configuration["DATA_ROOT"];
var dataRoot = Path.GetFullPath(!string.IsNullOrEmpty(configuredDataRoot)
    ? configuredDataRoot
    : Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data"))
        ? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data")
        : Path.Combine(builder.Environment.ContentRootPath, "data"));
Directory.CreateDirectory(dataRoot);

var templatesDir = Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "Templates"))
    ? Path.Combine(builder.Environment.ContentRootPath, "Templates")
    : Path.Combine(builder.Environment.ContentRootPath, "src", "Workbench", "Templates");

var webRootDir = Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "wwwroot"))
    ? Path.Combine(builder.Environment.ContentRootPath, "wwwroot")
    : Path.Combine(builder.Environment.ContentRootPath, "src", "Workbench", "wwwroot");
if (Directory.Exists(webRootDir))
{
    builder.Environment.WebRootPath = webRootDir;
}

using var instanceLock = new FileStream(Path.Combine(dataRoot, "instance.lock"),
    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
builder.Services.AddDbContextFactory<WorkflowDb>(o =>
    o.UseSqlite($"Data Source={Path.Combine(dataRoot, "workflow.db")};Default Timeout=30"));
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton(new Workspace(Path.Combine(dataRoot, "runs"), templatesDir));
builder.Services.AddSingleton<DemoAgentProvider>();
builder.Services.AddSingleton<CopilotAgentProvider>();
builder.Services.AddSingleton<IValidationRunner, ValidationRunner>();

builder.Services.AddSingleton(sp =>
{
    var orchestrator = new Orchestrator(
        sp.GetRequiredService<RunStore>(),
        sp.GetRequiredService<Workspace>(),
        sp.GetRequiredService<IValidationRunner>(),
        sp.GetRequiredService<DemoAgentProvider>(),
        sp.GetRequiredService<CopilotAgentProvider>(),
        sp.GetRequiredService<ILogger<Orchestrator>>());
    orchestrator.CopilotEnabled = !isPublicDemo && builder.Configuration.GetValue<bool>("EnableCopilot", true);
    orchestrator.MaxRunDuration = isPublicDemo ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(2);
    return orchestrator;
});
builder.Services.AddHostedService(s => s.GetRequiredService<Orchestrator>());

// Configure reverse proxy forwarded headers (for Render, Railway, Vercel, Cloudflare)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Helper to partition rate limiting by session token or remote IP
static string GetClientIdentifier(HttpContext ctx)
{
    var token = ctx.Request.Headers["X-Workbench-Token"].ToString();
    if (!string.IsNullOrEmpty(token)) return "token:" + token;
    return "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
}

// Rate limiting for public demo protection
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("create-run", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            GetClientIdentifier(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = isPublicDemo ? 5 : 1000,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("api-general", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            GetClientIdentifier(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = isPublicDemo ? 120 : 10000,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();
await app.Services.GetRequiredService<RunStore>().Initialize();

var masterToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var activeSessions = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();
activeSessions[masterToken] = DateTimeOffset.UtcNow;

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseRateLimiter();

var allowedOrigins = (builder.Configuration["ALLOWED_ORIGINS"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

app.Use(async (context, next) =>
{
    if (!isPublicDemo)
    {
        var host = context.Request.Host.Host;
        if ((host is not ("localhost" or "127.0.0.1" or "::1")) ||
            (context.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip)))
        { context.Response.StatusCode = 403; return; }
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.ContentLength > 32_000)
        { context.Response.StatusCode = 413; return; }
        if (context.Request.Headers.Origin is { Count: > 0 } origin)
        {
            var originStr = origin.ToString();
            var selfOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
            if (originStr != selfOrigin && !allowedOrigins.Contains(originStr, StringComparer.OrdinalIgnoreCase))
            { context.Response.StatusCode = 403; return; }
        }
        var reqToken = context.Request.Headers["X-Workbench-Token"].ToString();
        if (context.Request.Method != "GET" &&
            (string.IsNullOrEmpty(reqToken) || (!activeSessions.ContainsKey(reqToken) && reqToken != masterToken)))
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

app.MapGet("/api/session", (HttpContext context) =>
{
    string sessionToken;
    if (context.Request.Cookies.TryGetValue("workbench_session", out var cookieToken) &&
        !string.IsNullOrEmpty(cookieToken) &&
        activeSessions.ContainsKey(cookieToken))
    {
        sessionToken = cookieToken;
    }
    else
    {
        sessionToken = isPublicDemo ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) : masterToken;
        activeSessions[sessionToken] = DateTimeOffset.UtcNow;
        context.Response.Cookies.Append("workbench_session", sessionToken, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = isPublicDemo || context.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddDays(1)
        });
    }

    return Results.Ok(new
    {
        token = sessionToken,
        mode = isPublicDemo ? "PUBLIC DEMO · Guarded multi-session execution" : "LOCAL OPERATOR - not multi-user authentication",
        isPublicDemo,
        copilotEnabled = !isPublicDemo,
        defaults = new
        {
            provider = "demo",
            maxParallel = 3,
            maxRepairs = 1,
            maxProviderRetries = 1,
            maxCalls = 24,
            maxRevisions = 5,
            maxRunMinutes = isPublicDemo ? 15 : 120,
            maxStageSeconds = 180
        }
    });
});

app.MapGet("/health", (Orchestrator engine) => engine.Ready
    ? Results.Ok(new { status = "healthy", isPublicDemo }) : Results.StatusCode(503));
app.MapGet("/api/runs", (RunStore store) => store.All());
app.MapGet("/api/metrics", async (RunStore store) => Metrics.Overall(await store.All()));

app.MapPost("/api/runs", async (RunRequest request, Orchestrator engine, HttpContext ctx) =>
{
    var callerToken = ctx.Request.Headers["X-Workbench-Token"].ToString();
    var run = await engine.Create(request, callerToken);
    return Results.Created($"/api/runs/{run.Id}", run);
}).RequireRateLimiting("create-run");

app.MapGet("/api/runs/{id}", async (string id, RunStore store, HttpContext ctx) =>
{
    var run = await store.Get(id);
    var events = await store.Events(id);
    var token = ctx.Request.Headers["X-Workbench-Token"].ToString();
    var isOwner = !isPublicDemo || string.IsNullOrEmpty(run.OwnerToken) || run.OwnerToken == token || token == masterToken;
    return new RunView(run, events, Metrics.ForRun(run, events), isOwner);
});

async Task<IResult?> AuthorizeRunMutation(string runId, RunStore store, HttpContext ctx)
{
    var run = await store.Get(runId);
    var token = ctx.Request.Headers["X-Workbench-Token"].ToString();
    if (isPublicDemo)
    {
        if (string.IsNullOrEmpty(token) || (run.OwnerToken != token && token != masterToken))
        {
            return Results.Problem("Forbidden: You cannot modify a run created by another visitor session.", statusCode: 403);
        }
    }
    return null;
}

app.MapPost("/api/runs/{id}/clarify", async (string id, RevisionRequest r, Orchestrator engine, RunStore store, HttpContext ctx) =>
{
    if (await AuthorizeRunMutation(id, store, ctx) is { } forbidden) return forbidden;
    await engine.Clarify(id, r);
    return Results.Ok();
});

app.MapPost("/api/runs/{id}/revise", async (string id, RevisionRequest r, Orchestrator engine, RunStore store, HttpContext ctx) =>
{
    if (await AuthorizeRunMutation(id, store, ctx) is { } forbidden) return forbidden;
    await engine.Revise(id, r);
    return Results.Ok();
});

app.MapPost("/api/runs/{id}/approve/{stage}", async (string id, string stage, Decision decision, Orchestrator engine, RunStore store, HttpContext ctx) =>
{
    if (await AuthorizeRunMutation(id, store, ctx) is { } forbidden) return forbidden;
    await engine.Approve(id, stage, decision);
    return Results.Ok();
});

app.MapPost("/api/runs/{id}/stop", async (string id, Orchestrator engine, RunStore store, HttpContext ctx) =>
{
    if (await AuthorizeRunMutation(id, store, ctx) is { } forbidden) return forbidden;
    await engine.Stop(id, "Operator requested safe stop and restoration of approved baseline.");
    return Results.Ok();
});

app.MapPost("/api/runs/{id}/resume", async (string id, Orchestrator engine, RunStore store, HttpContext ctx) =>
{
    if (await AuthorizeRunMutation(id, store, ctx) is { } forbidden) return forbidden;
    await engine.Resume(id);
    return Results.Ok();
});

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
