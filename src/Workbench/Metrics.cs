namespace Workbench;

public static class Metrics
{
    public static object ForRun(WorkflowRun run, IReadOnlyList<AuditEntry> events)
    {
        var attempts = events.Count(e => e.Type == "StageStarted");
        var successful = events.Count(e => e.Type == "StageCompleted");
        var validationPassed = events.Count(e => e.Type == "StageCompleted" && e.Node.EndsWith(":Validation"));
        var validationFailed = events.Count(e => e.Type == "ValidationFailed");
        var activeMs = 0.0;
        var starts = new Dictionary<string, DateTimeOffset>();
        foreach (var e in events)
        {
            if (e.Type == "StageStarted") starts[e.Node] = e.At;
            if (e.Type is "StageCompleted" or "StageFailed" or "ValidationFailed" or "ProviderRetryScheduled")
                if (starts.Remove(e.Node, out var at)) activeMs += (e.At - at).TotalMilliseconds;
        }
        return new
        {
            run.AgentCalls, run.ProviderRetries, repairAttempts = run.Repairs, run.Rollbacks,
            stageSuccessRate = attempts == 0 ? 0 : (double)successful / attempts,
            validationSuccessRate = validationPassed + validationFailed == 0 ? 0 :
                (double)validationPassed / (validationPassed + validationFailed),
            endToEndMs = ((run.FinishedAt ?? DateTimeOffset.UtcNow) - run.CreatedAt).TotalMilliseconds,
            cumulativeStageMs = activeMs,
            recoveryMs = run.FirstFailureAt.HasValue && run.RecoveredAt.HasValue
                ? (double?)(run.RecoveredAt.Value - run.FirstFailureAt.Value).TotalMilliseconds : null,
            auditChainValid = RunStore.VerifyAudit(events),
            note = "Stage success = completion events / start events, including failed attempts. " +
                "End-to-end/recovery include human waits; current unfinished attempts excluded from cumulativeStageMs. " +
                "Local hash chain is not external tamper-proof storage."
        };
    }
    public static object Overall(IReadOnlyList<WorkflowRun> runs)
    {
        var terminal = runs.Where(r => r.Status is "Completed" or "SafeStopped").ToArray();
        var recovered = runs.Where(r => r.FirstFailureAt.HasValue && r.RecoveredAt.HasValue)
            .Select(r => (r.RecoveredAt!.Value - r.FirstFailureAt!.Value).TotalMilliseconds).ToArray();
        return new
        {
            totalRuns = runs.Count, terminalRuns = terminal.Length,
            successRate = terminal.Length == 0 ? 0 : (double)terminal.Count(r => r.Status == "Completed") / terminal.Length,
            retryFrequency = runs.Count == 0 ? 0 : (double)runs.Count(r => r.ProviderRetries + r.Repairs > 0) / runs.Count,
            rollbackFrequency = runs.Count == 0 ? 0 : (double)runs.Count(r => r.Rollbacks > 0) / runs.Count,
            mttrMs = recovered.Length == 0 ? (double?)null : recovered.Average(),
            meanEndToEndMs = terminal.Length == 0 ? (double?)null :
                terminal.Average(r => (r.FinishedAt!.Value - r.CreatedAt).TotalMilliseconds),
            note = "MTTR = first failed validation to successful revalidation for recovered runs only, including approval waits. " +
                "Intentional failure demonstrations are included in these local metrics; they are not a production SLA."
        };
    }
}
