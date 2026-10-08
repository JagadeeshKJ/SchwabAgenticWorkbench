using GitHub.Copilot;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
await using var client = new CopilotClient(new CopilotClientOptions
{
    Connection = RuntimeConnection.ForStdio(args[0]),
    WorkingDirectory = AppContext.BaseDirectory
});
await client.StartAsync(timeout.Token);
var auth = await client.GetAuthStatusAsync(timeout.Token);
Console.WriteLine("Authenticated: " + auth.IsAuthenticated);
if (!auth.IsAuthenticated) return 2;
var models = await client.ListModelsAsync(timeout.Token);
Console.WriteLine("Accessible model IDs: " + string.Join(", ", models.Select(m => m.Id)));
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    AvailableTools = [],
    ExcludedTools = ["builtin:*", "mcp:*", "custom:*"]
}, timeout.Token);
var response = await session.SendAndWaitAsync(
    "Return only the JSON object {\"status\":\"ok\"}. Do not use tools.",
    TimeSpan.FromSeconds(25), timeout.Token);
Console.WriteLine("Probe response: " + response?.Data.Content);
return response?.Data.Content.Contains("\"ok\"") == true ? 0 : 3;
