using Microsoft.EntityFrameworkCore;

namespace Workbench;

public sealed class RunRow
{
    public string Id { get; set; } = "";
    public string State { get; set; } = "";
}
public sealed class EventRow
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public string Payload { get; set; } = "";
}
public sealed class WorkflowDb(DbContextOptions<WorkflowDb> options) : DbContext(options)
{
    public DbSet<RunRow> Runs => Set<RunRow>();
    public DbSet<EventRow> Events => Set<EventRow>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<RunRow>().HasKey(r => r.Id);
        builder.Entity<EventRow>().HasIndex(e => e.RunId);
    }
}
public sealed class RunStore(IDbContextFactory<WorkflowDb> factory)
{
    private readonly SemaphoreSlim _write = new(1, 1);
    public async Task Initialize()
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
    public async Task<List<WorkflowRun>> All()
    {
        await using var db = await factory.CreateDbContextAsync();
        return (await db.Runs.AsNoTracking().ToListAsync())
            .Select(r => Json.Read<WorkflowRun>(r.State)).OrderByDescending(r => r.CreatedAt).ToList();
    }
    public async Task<WorkflowRun> Get(string id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id)
            ?? throw new KeyNotFoundException("Run not found.");
        return Json.Read<WorkflowRun>(row.State);
    }
    public async Task<List<AuditEntry>> Events(string id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return (await db.Events.AsNoTracking().Where(e => e.RunId == id).OrderBy(e => e.Id)
            .ToListAsync()).Select(e => Json.Read<AuditEntry>(e.Payload)).ToList();
    }
    public Task Add(WorkflowRun run) => Change(run.Id, "RunCreated", "", Json.Write(new
    {
        run.Scenario, run.Provider, run.Fault, run.BaselineId,
        mode = run.Provider == "demo" ? "DETERMINISTIC TEMPLATE DEMO - not live AI" : "LIVE COPILOT"
    }), _ => { }, run);

    public async Task Change(string id, string type, string node, string detail,
        Action<WorkflowRun> mutate, WorkflowRun? newRun = null)
    {
        await _write.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var row = await db.Runs.SingleOrDefaultAsync(r => r.Id == id);
            var state = newRun ?? (row is null ? throw new KeyNotFoundException("Run not found.")
                : Json.Read<WorkflowRun>(row.State));
            mutate(state);
            state.UpdatedAt = DateTimeOffset.UtcNow;
            var last = await db.Events.Where(e => e.RunId == id).OrderByDescending(e => e.Id)
                .FirstOrDefaultAsync();
            var previous = last is null ? null : Json.Read<AuditEntry>(last.Payload);
            var entry = new AuditEntry((previous?.Sequence ?? 0) + 1, id, state.UpdatedAt,
                type, node, state.Revision, detail, previous?.Hash ?? "", "");
            entry = entry with { Hash = Json.Hash(Json.Write(entry)) };
            db.Events.Add(new EventRow { RunId = id, Payload = Json.Write(entry) });
            if (row is null) db.Runs.Add(new RunRow { Id = id, State = Json.Write(state) });
            else row.State = Json.Write(state);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        finally { _write.Release(); }
    }
    public static bool VerifyAudit(IReadOnlyList<AuditEntry> entries)
    {
        string previous = "";
        long sequence = 0;
        foreach (var e in entries)
        {
            if (e.Sequence != ++sequence || e.PreviousHash != previous ||
                e.Hash != Json.Hash(Json.Write(e with { Hash = "" }))) return false;
            previous = e.Hash;
        }
        return true;
    }
}
