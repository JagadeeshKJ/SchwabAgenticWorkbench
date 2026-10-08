using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Workbench;

public sealed class Workspace
{
    public string Root { get; }
    private readonly string _templates;
    public static readonly string[] SourceFiles = ["Shortener.csproj", "Program.cs", "LinkStore.cs", "LinkRules.cs", "openapi.json"];
    public Workspace(string root, string templates)
    {
        Root = Path.GetFullPath(root);
        _templates = templates;
        Directory.CreateDirectory(Root);
    }
    public string RunRoot(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid run ID.");
        return Path.Combine(Root, id);
    }
    public string Candidate(WorkflowRun r) => Path.Combine(RunRoot(r.Id), $"revision-{r.Revision}", "candidate");
    public string Evidence(WorkflowRun r) => Path.Combine(RunRoot(r.Id), $"revision-{r.Revision}", "evidence");
    public string Checkpoint(string id) => Path.Combine(RunRoot(id), "approved-source");
    public string Restored(string id) => Path.Combine(RunRoot(id), "restored-checkpoint");
    public void Initialize(WorkflowRun run)
    {
        Directory.CreateDirectory(Candidate(run));
        Directory.CreateDirectory(Evidence(run));
        if (run.BaselineId is { } baseline)
        {
            var checkpoint = Checkpoint(baseline);
            foreach (var file in SourceFiles)
                File.Copy(Path.Combine(checkpoint, file), Path.Combine(Candidate(run), file), false);
        }
    }
    public void Materialize(WorkflowRun run, string rules)
    {
        CheckRules(rules);
        foreach (var name in SourceFiles.Where(f => f != "LinkRules.cs"))
        {
            var target = Path.Combine(Candidate(run), name);
            // Brownfield source is inherited; only the designated rules file changes.
            if (!File.Exists(target)) File.WriteAllText(target, File.ReadAllText(Path.Combine(_templates, name + ".txt")));
        }
        File.WriteAllText(Path.Combine(Candidate(run), "LinkRules.cs"), rules);
    }
    public string? Rules(WorkflowRun run)
    {
        var path = Path.Combine(Candidate(run), "LinkRules.cs");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
    public static void CheckRules(string code)
    {
        if (code.Length > 6000) throw new InvalidDataException("Generated rules exceed size limit.");
        var stripped = Regex.Replace(code, @"//[^\r\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
        var allowed = new HashSet<string>(("using System namespace Shortener public static class LinkRules bool " +
            "ExpirationEnabled AliasesEnabled true false IsExpired Link link DateTimeOffset now ExpiresAt HasValue Value return")
            .Split(' '));
        foreach (Match match in Regex.Matches(stripped, @"[A-Za-z_]\w*"))
            if (!allowed.Contains(match.Value)) throw new InvalidDataException($"Disallowed rules token: {match.Value}");
        if (Regex.IsMatch(stripped, @"[^A-Za-z_\s{}();,.?:<>=!&|]"))
            throw new InvalidDataException("Rules contain characters outside the restricted expression grammar.");
        if (!stripped.Contains("class LinkRules") || !stripped.Contains("IsExpired"))
            throw new InvalidDataException("Generated rules are missing the required contract.");
    }
    public string Fingerprint(WorkflowRun run)
    {
        var files = Directory.Exists(Candidate(run))
            ? Directory.GetFiles(Candidate(run), "*", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName).ToArray()
            : [];
        return Json.Hash(Json.Write(run.Requirements) + string.Join("\n", files.Select(p =>
            Path.GetFileName(p) + ":" + FileHash(p))));
    }
    public static string FileHash(string path) => Json.Hash(File.ReadAllText(path));
    public string SourceHash(string directory) => Json.Hash(string.Join("\n", SourceFiles.Order()
        .Select(f => f + ":" + FileHash(Path.Combine(directory, f)))));
    public Artifact Save(WorkflowRun run, string node, string name, string content)
    {
        if (!Regex.IsMatch(name, @"^[a-zA-Z0-9_.-]+$") || content.Length > 300_000)
            throw new InvalidDataException("Invalid artifact name/size.");
        Directory.CreateDirectory(Evidence(run));
        var path = Path.Combine(Evidence(run), name);
        if (File.Exists(path)) throw new InvalidOperationException("Artifacts are immutable; use a new attempt name.");
        File.WriteAllText(path, content);
        return new(name, Json.Hash(content), node, run.Revision,
            name.EndsWith(".json") ? "application/json" : "text/plain");
    }
    public void CheckPolicy(WorkflowRun run)
    {
        var root = Candidate(run);
        var files = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Order();
        if (!files.SequenceEqual(SourceFiles.Order())) throw new InvalidDataException("Candidate source file allowlist mismatch.");
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are forbidden.");
        foreach (var name in SourceFiles.Where(f => f != "LinkRules.cs"))
            if (File.ReadAllText(Path.Combine(root, name)) != File.ReadAllText(Path.Combine(_templates, name + ".txt")))
                throw new InvalidDataException($"Trusted scaffold was modified: {name}");
        CheckRules(File.ReadAllText(Path.Combine(root, "LinkRules.cs")));
    }
    public void ApproveCheckpoint(WorkflowRun run)
    {
        var target = Checkpoint(run.Id);
        Directory.CreateDirectory(target);
        foreach (var file in SourceFiles)
        {
            var source = Path.Combine(Candidate(run), file);
            var destination = Path.Combine(target, file);
            if (File.Exists(destination))
            {
                if (FileHash(source) != FileHash(destination))
                    throw new InvalidDataException("Existing checkpoint differs; overwrite refused.");
            }
            else File.Copy(source, destination, false);
        }
    }
    public void VerifyArtifacts(WorkflowRun run)
    {
        foreach (var a in run.Artifacts)
        {
            var path = Path.Combine(RunRoot(run.Id), $"revision-{a.Revision}", "evidence", a.Name);
            if (!File.Exists(path) || FileHash(path) != a.Sha256)
                throw new InvalidDataException($"Artifact integrity mismatch: revision {a.Revision}/{a.Name}");
        }
    }
    public void Rollback(WorkflowRun run)
    {
        if (run.BaselineId is not { } baseline) return;
        if (SourceHash(Checkpoint(baseline)) != run.BaselineHash)
            throw new InvalidDataException("Approved baseline hash mismatch; rollback refused.");
        Directory.CreateDirectory(Restored(run.Id));
        foreach (var file in SourceFiles)
            File.Copy(Path.Combine(Checkpoint(baseline), file), Path.Combine(Restored(run.Id), file), true);
    }
    public string Export(WorkflowRun run, IReadOnlyList<AuditEntry> audit)
    {
        var path = Path.Combine(RunRoot(run.Id), "release.zip");
        var pending = Path.Combine(RunRoot(run.Id), "release.pending");
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
        foreach (var file in SourceFiles)
            zip.CreateEntryFromFile(Path.Combine(Candidate(run), file), "shortener/" + file);
        foreach (var artifact in run.Artifacts)
        {
            var source = Path.Combine(RunRoot(run.Id), $"revision-{artifact.Revision}", "evidence", artifact.Name);
            if (FileHash(source) != artifact.Sha256) throw new InvalidDataException("Artifact integrity mismatch.");
            zip.CreateEntryFromFile(source, $"evidence/revision-{artifact.Revision}/{artifact.Name}");
        }
        using (var writer = new StreamWriter(zip.CreateEntry("workflow.json").Open())) writer.Write(Json.Write(run));
        using (var writer = new StreamWriter(zip.CreateEntry("audit.json").Open())) writer.Write(Json.Write(audit));
        using (var writer = new StreamWriter(zip.CreateEntry("engineering-summary.json").Open()))
            writer.Write(Json.Write(new
            {
                run.Id, run.Scenario, run.Provider, run.Requirements, run.BaselineId, run.BaselineHash,
                run.Revision, run.Repairs, run.ProviderRetries, run.Rollbacks,
                validation = run.Artifacts.Where(a => a.Name.StartsWith("validation-")),
                approval = "Release approval bound to current source and evidence hashes was obtained before export.",
                snapshotNote = "workflow.json and audit.json capture the state at export entry; the Release completion event is committed after ZIP creation.",
                limitations = new[] { "Local-only prototype; no deployment", "Demo mode is template-backed",
                    "Source-only rollback", "Restricted pure-rule generation, not arbitrary software synthesis",
                    "Operator-reviewed code execution; no OS-level sandbox", "Single-user aggregate analytics" }
            }));
        using (var writer = new StreamWriter(zip.CreateEntry("README.txt").Open()))
            writer.Write("Generated local prototype. Provider: " + run.Provider +
                "\nRun: cd shortener; dotnet run\nLoopback: http://127.0.0.1:5190\n" +
                "This is not deployed. Review source before executing. No credentials are included.");
        }
        File.Move(pending, path, overwrite: true);
        return path;
    }
}
