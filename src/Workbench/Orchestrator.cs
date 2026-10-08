using System.Collections.Concurrent;

namespace Workbench;

public sealed class Orchestrator(RunStore store, Workspace workspace, IValidationRunner validator,
    DemoAgentProvider demo, CopilotAgentProvider copilot, ILogger<Orchestrator> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly SemaphoreSlim _slots = new(3);
    public bool Ready { get; private set; }
    public async Task<WorkflowRun> Create(RunRequest request)
    {
        if (request.Scenario is not ("greenfield" or "brownfield" or "ambiguous") ||
            request.Provider is not ("demo" or "copilot") ||
            request.Fault is not ("none" or "expiration-once" or "expiration-always" or "provider-once"))
            throw new ArgumentException("Unknown scenario, provider or fault.");
        if (request.Provider != "demo" && request.Fault != "none")
            throw new ArgumentException("Fault injection is available only in explicit demo mode.");
        if (request.Fault.StartsWith("expiration") && request.Scenario != "brownfield")
            throw new ArgumentException("Expiration fault requires the brownfield scenario.");
        if ((await store.All()).Count(r => r.Status is not ("Completed" or "SafeStopped")) >= 4)
            throw new InvalidOperationException("At most four unfinished runs are allowed.");
        WorkflowRun? baseline = null;
        if (request.Scenario == "brownfield")
        {
            baseline = await store.Get(request.BaselineId ?? throw new ArgumentException("Select a completed greenfield baseline."));
            if (baseline.Status != "Completed" || baseline.Scenario != "greenfield")
                throw new ArgumentException("Brownfield requires a completed greenfield baseline.");
        }
        var run = new WorkflowRun
        {
            Scenario = request.Scenario, Provider = request.Provider, Fault = request.Fault,
            BaselineId = baseline?.Id,
            BaselineHash = baseline is null ? "" : workspace.SourceHash(workspace.Checkpoint(baseline.Id)),
            Requirements = new(
                request.Scenario == "ambiguous" ? "Support branded links with useful analytics. Decisions needed: alias behavior?"
                : request.Scenario == "brownfield" ? "Add inclusive URL expiration. Expired redirects return 410 and do not increment clicks."
                : "Create HTTP(S) short links, redirect by code and report aggregate clicks.",
                request.Scenario == "brownfield", false, request.Scenario != "ambiguous"),
            Status = request.Scenario == "ambiguous" ? "AwaitingClarification" : "Running",
            Stages = Plans.Create(1, false)
        };
        workspace.Initialize(run);
        await store.Add(run);
        return run;
    }
    public async Task Clarify(string id, RevisionRequest input)
    {
        ValidateActor(input.Actor);
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Length > 2000)
            throw new ArgumentException("Provide a description of 1-2000 characters.");
        await store.Change(id, "ClarificationAnswered", "", Json.Write(input), run =>
        {
            if (run.Status != "AwaitingClarification" || run.Revision != input.ExpectedRevision)
                throw new InvalidOperationException("Clarification is stale or not requested.");
            run.Requirements = run.Requirements with { Description = input.Description, Aliases = input.Aliases, Clarified = true };
            run.Stages = Plans.Create(run.Revision, input.Aliases);
            run.Status = "Running";
        });
    }
    public async Task Revise(string id, RevisionRequest input)
    {
        ValidateActor(input.Actor);
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Length > 2000)
            throw new ArgumentException("Provide a description of 1-2000 characters.");
        await store.Change(id, "RequirementRevisedAndDescendantsInvalidated", "", Json.Write(input), run =>
        {
            if (run.Status is "Completed" or "SafeStopped" or "AwaitingClarification" ||
                input.ExpectedRevision != run.Revision || run.Revision >= 5 ||
                run.Current.Any(s => s.Kind == "Release" && s.Status == StageStatus.Running))
                throw new InvalidOperationException("Run cannot be revised (stale, terminal, unclarified or revision budget reached).");
            foreach (var stage in run.Current) stage.Status = StageStatus.Superseded;
            run.Revision++;
            run.Requirements = run.Requirements with { Description = input.Description, Aliases = input.Aliases };
            run.Stages.AddRange(Plans.Create(run.Revision, input.Aliases));
            run.Status = "Running";
            run.RecoveredAt = null;
            workspace.Initialize(run);
        });
        Cancel(id);
    }
    public async Task Approve(string id, string stageId, Decision input)
    {
        ValidateActor(input.Actor);
        if (input.Note is null || input.Note.Length > 2000) throw new ArgumentException("Provide an approval note of at most 2000 characters.");
        await store.Change(id, "HumanApproved", stageId, Json.Write(input), run =>
        {
            var stage = run.Current.SingleOrDefault(s => s.Id == stageId);
            if (run.Status is "SafeStopped" or "Completed" || run.Revision != input.Revision ||
                stage?.Status != StageStatus.WaitingApproval)
                throw new InvalidOperationException("Approval is stale or not requested.");
            if (input.Fingerprint != stage.Fingerprint || stage.Fingerprint != GateHash(run))
                throw new InvalidOperationException("Artifacts changed. Approval refused; revise or safe-stop the run.");
            stage.Status = StageStatus.Completed;
            stage.FinishedAt = DateTimeOffset.UtcNow;
            stage.Summary = $"Approved by {input.Actor}: {input.Note}";
            run.Status = "Running";
        });
    }
    public async Task Stop(string id, string reason)
    {
        Cancel(id);
        await store.Change(id, "SafeStopped", "", reason, run =>
        {
            if (run.Status is "Completed" or "SafeStopped") throw new InvalidOperationException("Run already terminal.");
            foreach (var s in run.Current.Where(s => s.Status != StageStatus.Completed))
                s.Status = StageStatus.Cancelled;
            TryRollback(run);
            run.Status = "SafeStopped";
            run.FinishedAt = DateTimeOffset.UtcNow;
        });
        var stopped = await store.Get(id);
        if (stopped.RollbackError is not null)
            await store.Change(id, "RollbackFailed", "", stopped.RollbackError, _ => { });
    }
    private void Cancel(string id)
    {
        foreach (var (key, token) in _running)
            if (key.StartsWith(id + "/", StringComparison.Ordinal))
            {
                try { token.Cancel(); }
                catch (ObjectDisposedException) { /* The owned stage completed while cancellation was requested. */ }
            }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.Initialize();
        foreach (var run in await store.All())
        {
            if (run.Status is "Completed" or "SafeStopped") continue;
            await store.Change(run.Id, "RestartRecovery", "", "Interrupted work is paused; operator must authorize resumption.", r =>
            {
                foreach (var stage in r.Current.Where(s => s.Status == StageStatus.Running))
                { stage.Status = StageStatus.Pending; stage.Summary = "Interrupted by process restart."; }
                if (r.Status != "AwaitingClarification") r.Status = "Paused";
            });
        }
        Ready = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var run in (await store.All()).Where(r => r.Status is "Running" or "WaitingApproval"))
                {
                    if (DateTimeOffset.UtcNow - run.CreatedAt > TimeSpan.FromHours(2))
                    { await Stop(run.Id, "Two-hour wall-clock execution budget exhausted."); continue; }
                    foreach (var stage in run.Current.Where(s => Plans.Ready(s, run)))
                    {
                        var key = run.Id + "/" + stage.Id;
                        if (_running.ContainsKey(key)) continue;
                        if (stage.IsApproval)
                        {
                            await store.Change(run.Id, "ApprovalRequested", stage.Id, "Approval binds requirement revision and artifact hashes.", r =>
                            {
                                var s = r.Stages.Single(n => n.Id == stage.Id);
                                if (r.Revision != stage.Revision || !Plans.Ready(s, r)) throw new InvalidOperationException("Stage became stale.");
                                s.Status = StageStatus.WaitingApproval;
                                s.Fingerprint = GateHash(r);
                                s.Summary = stage.Kind == "ExecutionApproval"
                                    ? "Review generated source before allowing local dotnet execution. Git directories are NOT a security sandbox."
                                    : "Review inputs and evidence before approving.";
                                r.Status = "WaitingApproval";
                            });
                        }
                        else if (_running.Count < 3)
                        {
                            var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            if (_running.TryAdd(key, linked)) _ = ExecuteStage(run.Id, stage.Id, linked, key);
                        }
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { logger.LogError(ex, "Scheduler tick failed; no stage was marked successful."); }
            try { await Task.Delay(150, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        foreach (var token in _running.Values) token.Cancel();
    }
    public Task Resume(string id) => store.Change(id, "HumanResumed", "", "Resume after restart; incomplete work reruns, approvals remain hash-bound.", r =>
    {
        if (r.Status != "Paused") throw new InvalidOperationException("Run is not paused.");
        r.Status = "Running";
    });
    private string GateHash(WorkflowRun r)
    {
        workspace.VerifyArtifacts(r);
        return Json.Hash($"{r.Revision}|{workspace.Fingerprint(r)}|" +
            Json.Write(r.Artifacts.Where(a => a.Revision == r.Revision).OrderBy(a => a.Name)));
    }

    private async Task ExecuteStage(string id, string stageId, CancellationTokenSource linked, string key)
    {
        await _slots.WaitAsync();
        int revision = 0;
        try
        {
            await store.Change(id, "StageStarted", stageId, "", r =>
            {
                var s = r.Stages.Single(s => s.Id == stageId);
                if (!Plans.Ready(s, r) || s.Revision != r.Revision || r.Status is "SafeStopped" or "Paused")
                    throw new OperationCanceledException("Stage is no longer eligible.");
                revision = r.Revision;
                s.Status = StageStatus.Running;
                s.Attempts++;
                s.StartedAt = DateTimeOffset.UtcNow;
            });
            var run = await store.Get(id);
            var stage = run.Stages.Single(s => s.Id == stageId);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            bound.CancelAfter(TimeSpan.FromMinutes(3));
            var cancellation = bound.Token;
            var artifacts = new List<Artifact>();
            string summary;
            if (stage.Kind == "Policy")
            {
                workspace.CheckPolicy(run);
                summary = "PASS: exact source-file allowlist, trusted scaffold hashes, restricted pure-rule tokens.";
                artifacts.Add(workspace.Save(run, stageId, $"policy-{run.Repairs}.txt", summary));
            }
            else if (stage.Kind == "Validation")
            {
                var approval = run.Current.Single(s => s.Kind == "ExecutionApproval");
                if (approval.Fingerprint != GateHash(run)) throw new InvalidDataException("Execution approval is stale.");
                var report = await validator.Validate(run, cancellation);
                artifacts.Add(workspace.Save(run, stageId, $"validation-{run.Repairs}.json", Json.Write(report)));
                if (!report.Passed)
                {
                    await HandleValidationFailure(run, stageId, artifacts, report);
                    return;
                }
                summary = $"Actual build exited {report.BuildExitCode}; {report.Checks.Count} external HTTP assertions passed.";
            }
            else if (stage.Kind == "Release")
            {
                if (run.Current.Single(s => s.Kind == "ReleaseApproval").Fingerprint != GateHash(run))
                    throw new InvalidDataException("Release approval is stale.");
                workspace.CheckPolicy(run);
                workspace.ApproveCheckpoint(run);
                workspace.Export(run, await store.Events(id));
                summary = "Local source/evidence ZIP exported. No deployment or public publication.";
            }
            else
            {
                if (run.AgentCalls >= 24) throw new InvalidOperationException("24-call run budget exhausted.");
                var context = string.Join("\n", run.Current.Where(s => s.Status == StageStatus.Completed)
                    .Select(s => $"{s.Id}: {s.Summary}"));
                if (stage.Kind == "Repair")
                {
                    var reportPath = Path.Combine(workspace.Evidence(run), $"validation-{run.Repairs - 1}.json");
                    context += "\nACTUAL VALIDATOR REPORT:\n" + File.ReadAllText(reportPath);
                }
                if (context.Length > 16000) context = context[..16000] + "\n[Context capped at 16000 characters]";
                await store.Change(id, "AgentRequested", stageId, Json.Write(new
                {
                    provider = run.Provider, role = stage.Kind, contextHash = Json.Hash(context),
                    inputArtifacts = run.Artifacts.Select(a => new { a.Name, a.Revision, a.Sha256 }),
                    toolsPermitted = false
                }), r =>
                {
                    RequireCurrent(r, revision);
                    if (r.AgentCalls >= 24) throw new InvalidOperationException("24-call run budget exhausted.");
                    r.AgentCalls++;
                });
                if (run.Fault == "provider-once" && stage.Kind == "Design" && stage.Attempts == 1)
                    throw new HttpRequestException("Controlled demo provider transient failure.");
                IAgentProvider provider = run.Provider == "demo" ? demo : copilot;
                bool defect = stage.Kind is "Implementation" or "Repair" &&
                    (run.Fault == "expiration-always" || (run.Fault == "expiration-once" && run.Repairs == 0));
                var response = await provider.Execute(new(stage.Kind, RolePrompts.For(stage.Kind) +
                    (stage.Kind == "Repair" ? "\nUse the actual validator report in context; do not weaken tests." : ""),
                    run.Requirements, workspace.Rules(run), defect, context), cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (response.Summary.Length > 20_000) throw new InvalidDataException("Agent output too large.");
                var name = $"{stage.Kind.ToLowerInvariant()}-{stage.Attempts}-{run.Repairs}.json";
                artifacts.Add(workspace.Save(run, stageId, name, Json.Write(response)));
                if (stage.Kind is "Implementation" or "Repair")
                {
                    await store.Change(id, "CandidateProposed", stageId,
                        "Agent output recorded before policy checks. This is not validated code.", r =>
                        {
                            RequireCurrent(r, revision);
                            r.Artifacts.AddRange(artifacts);
                        });
                    artifacts.Clear();
                    workspace.Materialize(run, response.Code ?? throw new InvalidDataException("Implementation returned no source."));
                }
                summary = response.Summary;
                if (stage.Kind == "Design")
                    artifacts.Add(workspace.Save(run, stageId, $"dependency-graph-{stage.Attempts}.json", Json.Write(run.Current)));
                if (stage.Kind == "Requirements")
                    artifacts.Add(workspace.Save(run, stageId, $"requirements-{stage.Attempts}.json", Json.Write(run.Requirements)));
                if (stage.Kind == "Analysis" && run.BaselineId is not null)
                    artifacts.Add(workspace.Save(run, stageId, $"impact-{stage.Attempts}.txt",
                        $"Baseline {run.BaselineId}; SHA256 {run.BaselineHash}. Impact: LinkRules.IsExpired -> redirect status -> click counting. " +
                        "Schema already reserves nullable ExpiresAt; no destructive migration. External expiry regression tests are mandatory."));
            }
            await store.Change(id, "StageCompleted", stageId, summary, r =>
            {
                RequireCurrent(r, revision);
                var s = r.Stages.Single(n => n.Id == stageId);
                if (s.Status != StageStatus.Running) throw new OperationCanceledException("Stage cancelled.");
                s.Status = StageStatus.Completed;
                s.FinishedAt = DateTimeOffset.UtcNow;
                s.Summary = summary;
                r.Artifacts.AddRange(artifacts);
                if (stage.Kind == "Validation" && r.FirstFailureAt is not null) r.RecoveredAt = DateTimeOffset.UtcNow;
                if (stage.Kind == "Release") { r.Status = "Completed"; r.FinishedAt = DateTimeOffset.UtcNow; }
            });
        }
        catch (OperationCanceledException ex)
        {
            await RecordFailure(id, stageId, revision, "Cancelled or timed out: " + ex.Message, false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stage {Stage} failed for run {Run}", stageId, id);
            await RecordFailure(id, stageId, revision, ex.Message, ex is HttpRequestException);
        }
        finally
        {
            _running.TryRemove(key, out _);
            linked.Dispose();
            _slots.Release();
        }
    }
    private async Task HandleValidationFailure(WorkflowRun run, string stageId, List<Artifact> artifacts, ValidationReport report)
    {
        await store.Change(run.Id, "ValidationFailed", stageId, Json.Write(report.Checks.Where(c => !c.Passed)), r =>
        {
            RequireCurrent(r, run.Revision);
            r.Artifacts.AddRange(artifacts);
            r.FirstFailureAt ??= DateTimeOffset.UtcNow;
            var validation = r.Current.Single(s => s.Kind == "Validation");
            validation.Status = StageStatus.Failed;
            validation.FinishedAt = DateTimeOffset.UtcNow;
            validation.Summary = "Actual validator failure. Approval and validation must be renewed after repair.";
            if (r.Repairs >= 1 || report.BuildExitCode != 0)
            {
                TryRollback(r);
                r.Status = "SafeStopped";
                r.FinishedAt = DateTimeOffset.UtcNow;
                foreach (var s in r.Current.Where(s => s.Status is StageStatus.Pending or StageStatus.WaitingApproval))
                    s.Status = StageStatus.Cancelled;
                return;
            }
            r.Repairs++;
            var repair = new Stage
            {
                Id = $"{r.Revision}:Repair-{r.Repairs}", Kind = "Repair", Revision = r.Revision,
                DependsOn = [r.Current.Single(s => s.Kind == "Implementation").Id]
            };
            r.Stages.Add(repair);
            foreach (var kind in new[] { "Policy", "ExecutionApproval", "Validation", "ReleaseApproval", "Release" })
            {
                var s = r.Current.Single(s => s.Kind == kind);
                s.Status = StageStatus.Pending;
                s.Fingerprint = "";
                s.Summary = "Invalidated by failed validation; repair path required.";
            }
            r.Current.Single(s => s.Kind == "Policy").DependsOn =
                [repair.Id, r.Current.Single(s => s.Kind == "Tests").Id, r.Current.Single(s => s.Kind == "Documentation").Id];
            r.Status = "Running";
        });
        var state = await store.Get(run.Id);
        await store.Change(run.Id, state.Status == "SafeStopped" ? "RollbackAndSafeStop" : "RepairPathPlanned", stageId,
            state.Status == "SafeStopped" ? state.RollbackError ?? "Repair/build bound reached; retained failed evidence and restored approved baseline if present."
                : "A new repair node and dependency edges were added; execution and release approvals revoked.", _ => { });
    }
    private async Task RecordFailure(string id, string stageId, int revision, string message, bool transient)
    {
        try
        {
            var state = await store.Get(id);
            if (state.Revision != revision || state.Status is "Completed" or "SafeStopped" || revision == 0) return;
            if (transient && state.ProviderRetries < 1)
            {
                await Task.Delay(700);
                await store.Change(id, "ProviderRetryScheduled", stageId, message, r =>
                {
                    RequireCurrent(r, revision);
                    r.ProviderRetries++;
                    r.Stages.Single(s => s.Id == stageId).Status = StageStatus.Pending;
                });
            }
            else
            {
                await store.Change(id, "StageFailed", stageId, message, r =>
                {
                    RequireCurrent(r, revision);
                    r.Stages.Single(s => s.Id == stageId).Status = StageStatus.Failed;
                    r.Stages.Single(s => s.Id == stageId).Summary = message;
                    r.FirstFailureAt ??= DateTimeOffset.UtcNow;
                });
                await Stop(id, "Fail-closed: " + message);
            }
        }
        catch (Exception ex) { logger.LogError(ex, "Could not record failure for {Run}/{Stage}", id, stageId); }
    }
    private static void RequireCurrent(WorkflowRun run, int revision)
    {
        if (run.Revision != revision || run.Status is "SafeStopped" or "Completed")
            throw new OperationCanceledException("Stale execution output discarded.");
    }
    private void TryRollback(WorkflowRun run)
    {
        if (run.BaselineId is null) return;
        try
        {
            workspace.Rollback(run);
            run.Rollbacks++;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            run.RollbackError = "ROLLBACK FAILED; candidate remains stopped and cannot release: " + ex.Message;
            logger.LogError(ex, "Rollback failed for stopped run {Run}", run.Id);
        }
    }
    private static void ValidateActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 80)
            throw new ArgumentException("Operator name must contain 1-80 characters.");
    }
}
