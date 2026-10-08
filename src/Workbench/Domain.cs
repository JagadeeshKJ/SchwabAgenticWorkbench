using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workbench;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options)
        ?? throw new InvalidDataException("Empty JSON value.");
    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public enum StageStatus { Pending, Running, WaitingApproval, Completed, Failed, Superseded, Cancelled }
public sealed record Requirements(string Description, bool Expiration, bool Aliases, bool Clarified);
public sealed record RunRequest(string Scenario, string Provider = "demo", string? BaselineId = null,
    string Fault = "none");
public sealed record Decision(int Revision, string Fingerprint, string Actor, string Note);
public sealed record RevisionRequest(int ExpectedRevision, bool Aliases, string Description, string Actor);
public sealed record Artifact(string Name, string Sha256, string Producer, int Revision, string MediaType);
public sealed class Stage
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Revision { get; set; }
    public string[] DependsOn { get; set; } = [];
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public int Attempts { get; set; }
    public string Summary { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool IsApproval => Kind.EndsWith("Approval", StringComparison.Ordinal);
}
public sealed class WorkflowRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Scenario { get; set; } = "";
    public string Provider { get; set; } = "demo";
    public string? BaselineId { get; set; }
    public string BaselineHash { get; set; } = "";
    public int Revision { get; set; } = 1;
    public Requirements Requirements { get; set; } = new("", false, false, true);
    public string Status { get; set; } = "Running";
    public string Fault { get; set; } = "none";
    public int Repairs { get; set; }
    public int ProviderRetries { get; set; }
    public int Rollbacks { get; set; }
    public string? RollbackError { get; set; }
    public int AgentCalls { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FirstFailureAt { get; set; }
    public DateTimeOffset? RecoveredAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<Stage> Stages { get; set; } = [];
    public List<Artifact> Artifacts { get; set; } = [];
    public IEnumerable<Stage> Current => Stages.Where(s => s.Revision == Revision);
}
public static class Plans
{
    public static List<Stage> Create(int revision, bool aliases)
    {
        var stages = new List<Stage>();
        void Add(string kind, params string[] dependencies) => stages.Add(new Stage
        {
            Id = $"{revision}:{kind}", Kind = kind, Revision = revision,
            DependsOn = dependencies.Select(d => $"{revision}:{d}").ToArray()
        });
        Add("Requirements");
        Add("Analysis", "Requirements");
        Add("Design", "Analysis");
        Add("PlanApproval", "Design");
        if (aliases) Add("AliasPolicy", "PlanApproval");
        Add("Implementation", aliases ? "AliasPolicy" : "PlanApproval");
        Add("Tests", "PlanApproval");
        Add("Documentation", "PlanApproval");
        Add("Policy", "Implementation", "Tests", "Documentation");
        Add("ExecutionApproval", "Policy");
        Add("Validation", "ExecutionApproval");
        Add("ReleaseApproval", "Validation");
        Add("Release", "ReleaseApproval");
        Validate(stages);
        return stages;
    }
    public static void Validate(IReadOnlyList<Stage> stages)
    {
        var map = stages.ToDictionary(s => s.Id);
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();
        void Visit(string id)
        {
            if (!map.TryGetValue(id, out var node)) throw new InvalidDataException("Missing dependency.");
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidDataException("Dependency cycle.");
            foreach (var dependency in node.DependsOn) Visit(dependency);
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var node in stages) Visit(node.Id);
    }
    public static bool Ready(Stage stage, WorkflowRun run) =>
        stage.Status == StageStatus.Pending &&
        stage.DependsOn.All(id => run.Stages.Single(n => n.Id == id).Status == StageStatus.Completed);
}
public sealed record AuditEntry(long Sequence, string RunId, DateTimeOffset At, string Type,
    string Node, int Revision, string Detail, string PreviousHash, string Hash);
public sealed record RunView(WorkflowRun Run, IReadOnlyList<AuditEntry> Events, object Metrics);
