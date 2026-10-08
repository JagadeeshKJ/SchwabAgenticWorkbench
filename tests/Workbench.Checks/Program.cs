using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Workbench;

var checks = 0;
void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}
void Throws(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { Assert(true, message); return; }
    throw new InvalidOperationException("FAIL (no expected exception): " + message);
}
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var appRoot = Path.Combine(root, "src", "Workbench");
var data = Path.Combine(root, "data", "checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(data);
var graph = Plans.Create(1, true);
Assert(graph.Single(s => s.Kind == "Implementation").DependsOn.Contains("1:AliasPolicy"),
    "alias requirement introduces a real graph dependency");
graph[0].DependsOn = [graph[^1].Id];
Throws(() => Plans.Validate(graph), "cyclic graph rejected");
Throws(() => Workspace.CheckRules("System.Diagnostics.Process.Start(\"evil\");"),
    "unsafe generated source rejected");
Assert(RunStore.VerifyAudit([]), "empty audit chain verifies");

int port;
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var url = $"http://127.0.0.1:{port}";
using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(15) };
Process? server = null;
Task<string>? serverOutput = null, serverErrors = null;
async Task Start()
{
    var info = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = appRoot, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    info.ArgumentList.Add(Path.Combine(appRoot, "bin", "Release", "net10.0", "Workbench.dll"));
    info.Environment["DataRoot"] = data;
    info.Environment["WorkbenchUrl"] = url;
    server = Process.Start(info) ?? throw new InvalidOperationException("Host not started.");
    serverOutput = server.StandardOutput.ReadToEndAsync();
    serverErrors = server.StandardError.ReadToEndAsync();
    for (var i = 0; i < 80; i++)
    {
        if (server.HasExited) throw new InvalidOperationException(await serverOutput + await serverErrors);
        try
        {
            using var result = await http.GetAsync("/health");
            if (result.IsSuccessStatusCode)
            {
                var session = await http.GetFromJsonAsync<JsonElement>("/api/session");
                http.DefaultRequestHeaders.Remove("X-Workbench-Token");
                http.DefaultRequestHeaders.Add("X-Workbench-Token", session.GetProperty("token").GetString());
                await Task.Delay(300);
                return;
            }
        }
        catch (HttpRequestException) { }
        await Task.Delay(200);
    }
    throw new TimeoutException("Host startup failed.");
}
async Task Kill()
{
    if (server is null) return;
    if (!server.HasExited) server.Kill(entireProcessTree: true);
    await server.WaitForExitAsync();
    await File.AppendAllTextAsync(Path.Combine(data, "host.log"), await serverOutput! + await serverErrors!);
    server.Dispose();
    server = null;
}
async Task<T> Post<T>(string path, object body)
{
    using var response = await http.PostAsJsonAsync(path, body, Json.Options);
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException(path + ": " + text);
    return Json.Read<T>(text);
}
async Task Command(string path, object body)
{
    using var response = await http.PostAsJsonAsync(path, body, Json.Options);
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException(path + ": " + await response.Content.ReadAsStringAsync());
}
async Task<RunView> Read(string id) =>
    Json.Read<RunView>(await http.GetStringAsync("/api/runs/" + id));
async Task<RunView> Wait(string id, Func<RunView, bool> predicate, int seconds = 150)
{
    var clock = Stopwatch.StartNew();
    RunView view;
    do
    {
        view = await Read(id);
        if (predicate(view)) return view;
        if (view.Run.Status == "SafeStopped")
            throw new InvalidOperationException("Unexpected safe stop: " + Json.Write(view.Events.TakeLast(4)));
        await Task.Delay(200);
    } while (clock.Elapsed.TotalSeconds < seconds);
    throw new TimeoutException(Json.Write(view.Run));
}
async Task Approve(WorkflowRun run, Stage stage)
{
    await Command($"/api/runs/{run.Id}/approve/{stage.Id}", new Decision(
        run.Revision, stage.Fingerprint, "AUTOMATED TEST - simulated operator", "Test-only approval of deterministic generated source."));
}
async Task<RunView> Finish(string id, string target = "Completed")
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed < TimeSpan.FromMinutes(4))
    {
        var v = await Read(id);
        if (v.Run.Status == target) return v;
        if (v.Run.Status == "SafeStopped") throw new InvalidOperationException(Json.Write(v.Events.TakeLast(5)));
        foreach (var stage in v.Run.Current.Where(s => s.Status == StageStatus.WaitingApproval))
            await Approve(v.Run, stage);
        await Task.Delay(250);
    }
    throw new TimeoutException("Scenario completion exceeded four minutes.");
}
try
{
    await Start();
    if (args.Contains("--live"))
    {
        var live = await Post<WorkflowRun>("/api/runs", new RunRequest("greenfield", Provider: "copilot"));
        var result = await Finish(live.Id);
        Assert(result.Run.Provider == "copilot" && result.Run.Status == "Completed",
            "real Copilot greenfield run produced source and passed external build/HTTP checks");
        Console.WriteLine("Live evidence: " + data);
        return;
    }
    using (var unauthorized = new HttpClient { BaseAddress = new Uri(url) })
    using (var response = await unauthorized.PostAsJsonAsync("/api/runs", new RunRequest("greenfield")))
        Assert(response.StatusCode == HttpStatusCode.Forbidden, "mutations require the local operator token");
    using (var message = new HttpRequestMessage(HttpMethod.Post, "/api/runs"))
    {
        message.Headers.Add("Origin", "https://untrusted.example");
        message.Content = JsonContent.Create(new RunRequest("greenfield"));
        using var response = await http.SendAsync(message);
        Assert(response.StatusCode == HttpStatusCode.Forbidden, "cross-origin mutation rejected");
    }
    var green = await Post<WorkflowRun>("/api/runs", new RunRequest("greenfield", Fault: "provider-once"));
    var firstGate = await Wait(green.Id, v => v.Run.Current.Any(s => s.Status == StageStatus.WaitingApproval));
    var gate = firstGate.Run.Current.Single(s => s.Status == StageStatus.WaitingApproval);
    using (var response = await http.PostAsJsonAsync($"/api/runs/{green.Id}/approve/{gate.Id}",
        new Decision(0, gate.Fingerprint, "Test", "stale"), Json.Options))
        Assert(response.StatusCode == HttpStatusCode.Conflict, "stale revision approval rejected");
    var completed = await Finish(green.Id);
    Assert(completed.Run.ProviderRetries == 1, "provider transient failure retried exactly once");
    Assert(completed.Run.Status == "Completed", "greenfield completed with real build and HTTP assertions");
    Assert(RunStore.VerifyAudit(completed.Events), "persisted hash-linked audit verifies");
    var tampered = completed.Events.ToList();
    tampered[0] = tampered[0] with { Detail = "tampered" };
    Assert(!RunStore.VerifyAudit(tampered), "audit mutation is detected");
    var branch = completed.Run.Current.Where(s => s.Kind is "Implementation" or "Tests" or "Documentation").ToArray();
    Assert(branch.Max(s => s.StartedAt) < branch.Min(s => s.FinishedAt),
        "implementation, tests and docs actually overlap in time");
    var artifact = completed.Run.Artifacts.Single(a => a.Name == "validation-0.json");
    var report = Json.Read<ValidationReport>(await http.GetStringAsync(
        $"/api/runs/{green.Id}/artifacts/1/{artifact.Name}"));
    Assert(report.Passed && report.BuildExitCode == 0 && report.Checks.Count >= 7,
        "release has independently executed validator evidence");
    var archive = await http.GetByteArrayAsync($"/api/runs/{green.Id}/release");
    using (var zip = new ZipArchive(new MemoryStream(archive)))
    {
        Assert(zip.GetEntry("shortener/LinkRules.cs") is not null && zip.GetEntry("audit.json") is not null,
            "approved source and evidence are exported without build caches");
        Assert(zip.GetEntry("shortener/openapi.json") is not null && zip.GetEntry("engineering-summary.json") is not null,
            "release includes API schema and final engineering summary");
    }

    var brown = await Post<WorkflowRun>("/api/runs", new RunRequest("brownfield", BaselineId: green.Id, Fault: "expiration-once"));
    var repaired = await Finish(brown.Id);
    Assert(repaired.Run.Repairs == 1 && repaired.Run.RecoveredAt.HasValue,
        "brownfield controlled expiration defect repaired and revalidated");
    Assert(repaired.Events.Count(e => e.Type == "ValidationFailed") == 1 &&
        repaired.Events.Count(e => e.Type == "HumanApproved" && e.Node.EndsWith(":ExecutionApproval")) == 2,
        "repair revokes previous execution approval and requires new human checkpoint");
    var broken = await Post<WorkflowRun>("/api/runs", new RunRequest("brownfield", BaselineId: green.Id, Fault: "expiration-always"));
    var stopped = await Finish(broken.Id, "SafeStopped");
    Assert(stopped.Run.Repairs == 1 && stopped.Run.Rollbacks == 1,
        "persistent defect exhausts repair bound and rolls back");
    var workspace = new Workspace(Path.Combine(data, "runs"), Path.Combine(appRoot, "Templates"));
    Assert(workspace.SourceHash(workspace.Restored(broken.Id)) == stopped.Run.BaselineHash,
        "rollback restores exact approved baseline source hash");
    using (var response = await http.GetAsync($"/api/runs/{broken.Id}/release"))
        Assert(response.StatusCode == HttpStatusCode.Conflict, "failed run cannot export a release");

    var ambiguous = await Post<WorkflowRun>("/api/runs", new RunRequest("ambiguous"));
    Assert((await Read(ambiguous.Id)).Run.Status == "AwaitingClarification", "ambiguous run stops before generation");
    await Command($"/api/runs/{ambiguous.Id}/clarify",
        new RevisionRequest(1, false, "Standard links and aggregate analytics only.", "Test operator"));
    var old = await Wait(ambiguous.Id, v => v.Run.Current.Any(s => s.Kind == "PlanApproval" && s.Status == StageStatus.WaitingApproval));
    var oldGate = old.Run.Current.Single(s => s.Kind == "PlanApproval");
    await Command($"/api/runs/{ambiguous.Id}/revise",
        new RevisionRequest(1, true, "Enable branded aliases and reject conflicts with HTTP 409.", "Test operator"));
    using (var response = await http.PostAsJsonAsync($"/api/runs/{ambiguous.Id}/approve/{oldGate.Id}",
        new Decision(1, oldGate.Fingerprint, "Test", "stale old graph"), Json.Options))
        Assert(response.StatusCode == HttpStatusCode.Conflict, "old graph approval invalidated by requirement revision");
    var replanned = await Finish(ambiguous.Id);
    Assert(replanned.Run.Revision == 2 && replanned.Run.Current.Any(s => s.Kind == "AliasPolicy"),
        "dynamic replan introduces alias policy task and dependency");
    Assert(replanned.Run.Stages.Where(s => s.Revision == 1).All(s => s.Status == StageStatus.Superseded) &&
        replanned.Run.Artifacts.Any(a => a.Revision == 1), "superseded lineage and original artifacts remain reviewable");

    var restart = await Post<WorkflowRun>("/api/runs", new RunRequest("greenfield"));
    await Wait(restart.Id, v => v.Run.Current.Any(s => s.Status == StageStatus.WaitingApproval));
    await Kill();
    await Start();
    Assert((await Read(restart.Id)).Run.Status == "Paused", "process restart requires explicit operator resumption");
    await Command($"/api/runs/{restart.Id}/resume", new { });
    var resumed = await Finish(restart.Id);
    Assert(resumed.Events.Any(e => e.Type == "RestartRecovery"), "restart is recorded in persisted audit history");
    var cancel = await Post<WorkflowRun>("/api/runs", new RunRequest("greenfield"));
    await Command($"/api/runs/{cancel.Id}/stop", new { });
    await Task.Delay(1000);
    Assert((await Read(cancel.Id)).Run.Status == "SafeStopped", "safe stop fences late agent results");
    var tamperRun = await Post<WorkflowRun>("/api/runs", new RunRequest("greenfield"));
    var scopeGate = await Wait(tamperRun.Id, v => v.Run.Current.Any(s => s.Status == StageStatus.WaitingApproval));
    await Approve(scopeGate.Run, scopeGate.Run.Current.Single(s => s.Status == StageStatus.WaitingApproval));
    var executionGate = await Wait(tamperRun.Id, v => v.Run.Current.Any(s => s.Kind == "ExecutionApproval" && s.Status == StageStatus.WaitingApproval));
    var execution = executionGate.Run.Current.Single(s => s.Kind == "ExecutionApproval");
    File.AppendAllText(Path.Combine(workspace.Candidate(executionGate.Run), "LinkRules.cs"), "\n// source changed after approval request");
    using (var response = await http.PostAsJsonAsync($"/api/runs/{tamperRun.Id}/approve/{execution.Id}",
        new Decision(executionGate.Run.Revision, execution.Fingerprint, "Test", "stale bytes"), Json.Options))
        Assert(response.StatusCode == HttpStatusCode.Conflict, "approval is invalid when source bytes change");
    await Command($"/api/runs/{tamperRun.Id}/stop", new { });
    var totals = await http.GetFromJsonAsync<JsonElement>("/api/metrics");
    Assert(totals.GetProperty("rollbackFrequency").GetDouble() > 0 &&
        totals.GetProperty("mttrMs").ValueKind == JsonValueKind.Number, "aggregate rollback frequency and measured recovery are exposed");
    var failedRollback = await Post<WorkflowRun>("/api/runs", new RunRequest("brownfield", BaselineId: green.Id));
    var baselinePath = Path.Combine(workspace.Checkpoint(green.Id), "LinkRules.cs");
    var baselineText = File.ReadAllText(baselinePath);
    try
    {
        File.AppendAllText(baselinePath, "\n// deliberate integrity-test corruption");
        await Command($"/api/runs/{failedRollback.Id}/stop", new { });
        var failureState = (await Read(failedRollback.Id)).Run;
        Assert(failureState.Status == "SafeStopped" && failureState.RollbackError is not null,
            "rollback failure still safe-stops and reports integrity error explicitly");
    }
    finally { File.WriteAllText(baselinePath, baselineText); }
    Console.WriteLine($"\nSUCCESS: {checks} checks. Evidence: {data}");
}
finally { await Kill(); }
