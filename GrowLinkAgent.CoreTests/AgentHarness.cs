using FlowMasterAgent.Models;
using FlowMasterAgent.Services;

namespace GrowLinkAgent.CoreTests;

/// <summary>An upload key held in memory (the app keeps it with Windows DPAPI).</summary>
public sealed class FixedCredentials(LogService log, string? key) : CredentialService(log)
{
    public string? Key { get; set; } = key;
    public override string? LoadKey() => Key;
    public override string? LoadRouteKey(string keySlot) => Key;
}

/// <summary>
/// The agent's real services wired as the app wires them, in a temporary folder, with short
/// retry delays so outages can be played out in seconds.
/// </summary>
public sealed class AgentHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "growlink-agent-test-" + Guid.NewGuid().ToString("N")[..8]);
    public string Folder => Path.Combine(Root, "Synopta Exports");
    public LogService Log { get; }
    public ConfigService Config { get; }
    public FixedCredentials Credentials { get; }
    public UploadService Upload { get; }
    public FolderWatcherService Watcher { get; }
    public List<string> Lines { get; } = new();

    public AgentHarness(string serverUrl, string? key, TimeSpan[]? backoff = null, TimeSpan[]? attemptDelays = null, TimeSpan? rescan = null)
    {
        Directory.CreateDirectory(Folder);
        Log = new LogService(Path.Combine(Root, "logs"));
        Log.OnLog += line => { lock (Lines) Lines.Add(line); };
        Config = new ConfigService(Log, Path.Combine(Root, "config"));
        Config.Save(new AppConfig
        {
            BlockSummaryFolder = Folder,
            UseRouteKeys = true,
            RouteConfigs = [new RouteConfig { RouteId = "block-summary", ServerUrl = serverUrl, Enabled = true }],
        });
        Credentials = new FixedCredentials(Log, key);
        Upload = new UploadService(Config, Credentials, Log,
            new RetrySchedule(backoff ?? [TimeSpan.FromMilliseconds(300)]),
            attemptDelays ?? [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)],
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        Watcher = new FolderWatcherService(Config, Upload, Log, rescan ?? TimeSpan.FromMilliseconds(500));
    }

    public string Drop(string sourcePath, string? name = null)
    {
        var dest = Path.Combine(Folder, name ?? Path.GetFileName(sourcePath));
        File.Copy(sourcePath, dest);
        return dest;
    }

    public string UploadedPath(string name) => Path.Combine(Folder, "Uploaded", DateTime.Now.ToString("yyyy-MM"), name);
    public string FailedPath(string name) => Path.Combine(Folder, "Failed", name);

    public static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    public void Dispose()
    {
        Watcher.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
