using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;

namespace Workbench;

public sealed record CheckResult(string Name, bool Passed, string Detail);
public sealed record ValidationReport(bool Passed, int BuildExitCode, string BuildOutput,
    List<CheckResult> Checks, string CandidateHash, DateTimeOffset StartedAt, double DurationMs,
    string EvidenceSource = "actual child process exit code and external HTTP assertions");
public interface IValidationRunner
{
    Task<ValidationReport> Validate(WorkflowRun run, CancellationToken cancellation);
}
public sealed class ValidationRunner(Workspace workspace) : IValidationRunner
{
    public async Task<ValidationReport> Validate(WorkflowRun run, CancellationToken cancellation)
    {
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var fingerprint = workspace.Fingerprint(run);
        workspace.CheckPolicy(run);
        var (exit, output) = await Command(workspace.Candidate(run),
            ["build", "-c", "Release", "--nologo", "-v", "minimal"],
            TimeSpan.FromSeconds(120), cancellation);
        var checks = new List<CheckResult>();
        if (exit != 0) return new(false, exit, output, checks, fingerprint, started, watch.Elapsed.TotalMilliseconds);
        if (workspace.Fingerprint(run) != fingerprint) throw new InvalidDataException("Source changed during build.");
        int port;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var url = $"http://127.0.0.1:{port}";
        var dbFile = Path.Combine(workspace.Evidence(run), $"validation-{run.Repairs}.db");
        using var app = Create(workspace.Candidate(run),
            ["bin/Release/net10.0/Shortener.dll", "--urls", url]);
        app.StartInfo.Environment["SHORTENER_DB"] = dbFile;
        app.Start();
        var stdout = Drain(app.StandardOutput);
        var stderr = Drain(app.StandardError);
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };
            bool ready = false;
            for (int i = 0; i < 60; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (app.HasExited) throw new InvalidOperationException("Generated API exited before readiness.");
                try { using var health = await http.GetAsync("/health", cancellation); ready = health.IsSuccessStatusCode; }
                catch (HttpRequestException) { /* bounded startup retry */ }
                if (ready) break;
                await Task.Delay(200, cancellation);
            }
            if (!ready) throw new TimeoutException("Generated API did not become ready in 12 seconds.");
            async Task Check(string name, Func<Task<bool>> test)
            {
                try
                {
                    var passed = await test();
                    checks.Add(new(name, passed, passed ? "Expected response observed." : "Actual response violated contract."));
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
                { checks.Add(new(name, false, ex.Message)); }
            }
            await Check("reject non-HTTP destination", async () =>
            {
                using var r = await http.PostAsJsonAsync("/api/links", new { url = "file:///etc/passwd" }, cancellation);
                return r.StatusCode == HttpStatusCode.BadRequest;
            });
            await Check("unknown code is 404", async () =>
            {
                using var r = await http.GetAsync("/r/missing", cancellation);
                return r.StatusCode == HttpStatusCode.NotFound;
            });
            using var create = await http.PostAsJsonAsync("/api/links", new { url = "https://example.com/path" }, cancellation);
            checks.Add(new("create link is 201", create.StatusCode == HttpStatusCode.Created, ((int)create.StatusCode).ToString()));
            var link = await create.Content.ReadFromJsonAsync<ProductLink>(cancellation);
            if (link is null) throw new InvalidOperationException("Create response had no link.");
            await Check("redirect returns destination and no-store", async () =>
            {
                using var r = await http.GetAsync("/r/" + link.Code, cancellation);
                return r.StatusCode == HttpStatusCode.Found && r.Headers.Location?.AbsoluteUri == "https://example.com/path"
                    && r.Headers.CacheControl?.NoStore == true;
            });
            await Check("analytics count successful redirect", async () =>
                (await http.GetFromJsonAsync<ProductLink>($"/api/links/{link.Code}/stats", cancellation))?.Clicks == 1);
            await Check("alias policy", async () =>
            {
                using var r = await http.PostAsJsonAsync("/api/links", new { url = "https://example.com/", alias = "brand-one" }, cancellation);
                if (!run.Requirements.Aliases) return r.StatusCode == HttpStatusCode.BadRequest;
                using var duplicate = await http.PostAsJsonAsync("/api/links", new { url = "https://example.org/", alias = "brand-one" }, cancellation);
                return r.StatusCode == HttpStatusCode.Created && duplicate.StatusCode == HttpStatusCode.Conflict;
            });
            await Check("expiry creation policy", async () =>
            {
                using var r = await http.PostAsJsonAsync("/api/links", new
                { url = "https://example.com/", expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, cancellation);
                return r.StatusCode == HttpStatusCode.BadRequest;
            });
            if (run.Requirements.Expiration)
            {
                // A real clock boundary and real HTTP requests detect the injected expiration defect.
                using var r = await http.PostAsJsonAsync("/api/links", new
                { url = "https://example.com/expiry", expiresAt = DateTimeOffset.UtcNow.AddSeconds(1) }, cancellation);
                var expiring = await r.Content.ReadFromJsonAsync<ProductLink>(cancellation)
                    ?? throw new InvalidOperationException("Expiration link missing.");
                await Task.Delay(1300, cancellation);
                await Check("expired link returns 410", async () =>
                {
                    using var expired = await http.GetAsync("/r/" + expiring.Code, cancellation);
                    return expired.StatusCode == HttpStatusCode.Gone;
                });
                await Check("expired redirect does not increment analytics", async () =>
                    (await http.GetFromJsonAsync<ProductLink>($"/api/links/{expiring.Code}/stats", cancellation))?.Clicks == 0);
            }
            if (workspace.Fingerprint(run) != fingerprint) throw new InvalidDataException("Source changed during validation.");
        }
        finally
        {
            if (!app.HasExited) app.Kill(entireProcessTree: true);
            await app.WaitForExitAsync(CancellationToken.None);
            var logs = await stdout + "\n" + await stderr;
            File.WriteAllText(Path.Combine(workspace.Evidence(run), $"api-{run.Repairs}.log"), logs);
        }
        return new(checks.All(c => c.Passed), exit, output, checks, fingerprint, started, watch.Elapsed.TotalMilliseconds);
    }
    public static Process Create(string directory, string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
            "PROGRAMFILES", "PROGRAMFILES(X86)", "DOTNET_ROOT", "NUGET_PACKAGES", "USER", "LANG" };
        foreach (var key in info.Environment.Keys.ToArray())
            if (!keep.Contains(key)) info.Environment.Remove(key);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return new Process { StartInfo = info };
    }
    public static async Task<(int Exit, string Output)> Command(string directory, string[] args,
        TimeSpan timeout, CancellationToken cancellation)
    {
        using var process = Create(directory, args);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        process.Start();
        var stdout = Drain(process.StandardOutput);
        var stderr = Drain(process.StandardError);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        return (process.ExitCode, await stdout + "\n" + await stderr);
    }
    private static async Task<string> Drain(StreamReader reader)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            if (output.Length < 100_000) output.Append(buffer, 0, Math.Min(read, 100_000 - output.Length));
        return output.ToString();
    }
    private sealed record ProductLink(string Code, string Url, int Clicks);
}
