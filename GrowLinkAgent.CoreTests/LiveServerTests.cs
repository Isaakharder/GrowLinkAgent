
namespace GrowLinkAgent.CoreTests;

/// <summary>
/// The agent against a real climate server (Plants staging), with real exports — run only when
/// pointed at one. Nothing here is committed: the URL, key and files come from the environment.
///
///   PLANTS_CLIMATE_URL      e.g. https://&lt;staging-ref&gt;.supabase.co/functions/v1/climate
///   PLANTS_CLIMATE_KEY      a staging upload key (plk_…)
///   CLIMATE_SAMPLES_DIR     a folder of Block Summary exports, kept outside Git
///   FAULT_PROXY_URL         optional: a local proxy in front of the server (…/functions/v1/climate)
///                           whose /__mode endpoint switches pass / 503 / 429
///   FAULT_PROXY_COMMAND     optional: a command that starts that proxy (for the outage test,
///                           which starts it only after the agent has been failing for a while)
/// </summary>
public class LiveServerTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("PLANTS_CLIMATE_URL");
    private static readonly string? Key = Environment.GetEnvironmentVariable("PLANTS_CLIMATE_KEY");
    private static readonly string? Samples = Environment.GetEnvironmentVariable("CLIMATE_SAMPLES_DIR");
    private static readonly string? Proxy = Environment.GetEnvironmentVariable("FAULT_PROXY_URL");
    private static readonly string? ProxyCommand = Environment.GetEnvironmentVariable("FAULT_PROXY_COMMAND");
    private static bool Configured => Url is not null && Key is not null && Samples is not null;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    private static void Log(string line) => File.AppendAllText(Path.Combine(Path.GetTempPath(), "growlink-live-tests.log"), $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");

    [SkippableFact]
    public async Task RealExports_UploadAndMoveToUploaded_ThenRetriesAreDuplicates()
    {
        Skip.IfNot(Configured);
        var files = Directory.GetFiles(Samples!, "*.csv").OrderBy(f => f).ToArray();
        using var agent = new AgentHarness(Url!, Key);
        foreach (var f in files) agent.Drop(f);
        agent.Watcher.Start();
        Assert.True(await AgentHarness.WaitUntil(() => files.All(f => File.Exists(agent.UploadedPath(Path.GetFileName(f)))), Wait), string.Join("\n", agent.Lines));
        foreach (var f in files) Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(agent.UploadedPath(Path.GetFileName(f))));
        lock (agent.Lines) foreach (var l in agent.Lines.Where(l => l.Contains("accepted"))) Log(l);

        // The same files again, under new names (a re-export or a manual copy): stored once, answered as duplicates.
        using var again = new AgentHarness(Url!, Key);
        foreach (var f in files) again.Drop(f, "copy of " + Path.GetFileName(f));
        again.Watcher.Start();
        Assert.True(await AgentHarness.WaitUntil(() => files.All(f => File.Exists(again.UploadedPath("copy of " + Path.GetFileName(f)))), Wait));
        lock (again.Lines) Assert.Equal(files.Length, again.Lines.Count(l => l.Contains("Duplicate (already stored)")));
    }

    [SkippableFact]
    public async Task WrongKey_IsRefused_FileKept_ThenUploadsWithTheRightKey()
    {
        Skip.IfNot(Configured);
        var file = Directory.GetFiles(Samples!, "*.csv").OrderBy(f => f).First();
        using var agent = new AgentHarness(Url!, "plk_0000000000_" + new string('0', 64));
        var src = agent.Drop(file, "auth test " + Path.GetFileName(file));
        agent.Watcher.Start();
        Assert.True(await AgentHarness.WaitUntil(() => agent.Upload.Summary.PendingRetry == 1, Wait));
        Assert.True(File.Exists(src));
        Assert.Contains("upload key refused (401)", agent.Upload.Summary.LastProblem);
        Log($"wrong key: {agent.Upload.Summary.LastProblem}");

        agent.Credentials.Key = Key;
        Assert.True(await AgentHarness.WaitUntil(() => File.Exists(agent.UploadedPath("auth test " + Path.GetFileName(file))), Wait));
    }

    [SkippableFact]
    public async Task ThroughTheFaultProxy_ServerErrorsAndRateLimits_AreRetriedUntilAccepted()
    {
        Skip.IfNot(Configured && Proxy is not null);
        using var http = new HttpClient();
        var mode = new Uri(new Uri(Proxy!), "/__mode");
        var file = Directory.GetFiles(Samples!, "*.csv").OrderBy(f => f).Last();
        // A plain string body (Content-Length set): the proxy doesn't read chunked bodies.
        (await http.PostAsync(mode, new StringContent("{\"mode\":\"503\",\"count\":4}"))).EnsureSuccessStatusCode();
        (await http.PostAsync(mode, new StringContent("{\"mode\":\"429\",\"count\":1,\"retry_after\":2}"))).EnsureSuccessStatusCode();

        using var agent = new AgentHarness(Proxy!, Key);
        agent.Drop(file, "proxy test " + Path.GetFileName(file));
        agent.Watcher.Start();
        Assert.True(await AgentHarness.WaitUntil(() => File.Exists(agent.UploadedPath("proxy test " + Path.GetFileName(file))), Wait), string.Join("\n", agent.Lines));
        lock (agent.Lines)
        {
            Assert.Contains(agent.Lines, l => l.Contains("HTTP 503"));
            Assert.Contains(agent.Lines, l => l.Contains("HTTP 429"));
            foreach (var l in agent.Lines.Where(l => l.Contains("HTTP 5") || l.Contains("HTTP 429") || l.Contains("accepted") || l.Contains("Kept"))) Log(l);
        }
    }

    [SkippableFact]
    public async Task ProlongedOutage_FilesWait_ThenAllUploadWhenTheServerIsReachableAgain()
    {
        Skip.IfNot(Configured && Proxy is not null && ProxyCommand is not null);
        var files = Directory.GetFiles(Samples!, "*.csv").OrderBy(f => f).Take(3).ToArray();
        // The proxy isn't running yet: every attempt is "connection refused".
        using var agent = new AgentHarness(Proxy!, Key, backoff: [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)]);
        foreach (var f in files) agent.Drop(f, "outage test " + Path.GetFileName(f));
        agent.Watcher.Start();
        Assert.True(await AgentHarness.WaitUntil(() => agent.Upload.Summary.PendingRetry == files.Length, Wait));
        await Task.Delay(TimeSpan.FromSeconds(75)); // the outage goes on; several retries fail
        Assert.All(files, f => Assert.True(File.Exists(Path.Combine(agent.Folder, "outage test " + Path.GetFileName(f)))));
        Assert.False(Directory.Exists(Path.Combine(agent.Folder, "Failed")));
        int refusals;
        lock (agent.Lines) refusals = agent.Lines.Count(l => l.Contains("no answer from the server"));
        Log($"outage: {refusals} refused attempts over ~80 s, {agent.Upload.Summary.PendingRetry} files waiting");

        var parts = ProxyCommand!.Split(' ', 2);
        using var proxy = System.Diagnostics.Process.Start(parts[0], parts.Length > 1 ? parts[1] : "")!;
        try
        {
            var back = DateTime.Now;
            Assert.True(await AgentHarness.WaitUntil(() => files.All(f => File.Exists(agent.UploadedPath("outage test " + Path.GetFileName(f)))), Wait), string.Join("\n", agent.Lines));
            Log($"outage: all {files.Length} files uploaded {(DateTime.Now - back).TotalSeconds:0} s after the server came back");
        }
        finally
        {
            try { proxy.Kill(); } catch { }
        }
    }
}
