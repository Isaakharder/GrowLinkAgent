using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FlowMasterAgent.Services;

namespace GrowLinkAgent.CoreTests;

public class ClimateUploadTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "SampleFiles", name);
    private const string F08 = "Block Summary_20260717_080003.csv";
    private const string F09 = "Block Summary_20260717_090003.csv";
    private const string Key = "plk_0123456789_" + "ab0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    // ── Request body ───────────────────────────────────────────────────────────

    [Fact]
    public void Body_CarriesTheOriginalBytes_TheirHash_TheVersion_AndTheParsedReadings()
    {
        var raw = File.ReadAllBytes(Sample(F08));
        var parsed = new BlockSummaryCsvParser().ParseBytes(raw, F08);
        using var doc = JsonDocument.Parse(ClimateUpload.BuildBody(raw, F08, parsed));
        var root = doc.RootElement;

        Assert.Equal(raw, Convert.FromBase64String(root.GetProperty("raw_csv_base64").GetString()!));
        Assert.Equal("sha256-" + Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(), root.GetProperty("file_hash").GetString());
        Assert.Equal(F08, root.GetProperty("filename").GetString());
        Assert.Equal("1.1.0", root.GetProperty("agent_version").GetString());
        Assert.Equal(114, root.GetProperty("readings").GetArrayLength());
        Assert.Equal("Zone 1", root.GetProperty("readings")[0].GetProperty("zone_label").GetString());
    }

    [Fact]
    public void ParseBytes_MatchesParsingTheFile()
    {
        var a = new BlockSummaryCsvParser().Parse(Sample(F09));
        var b = new BlockSummaryCsvParser().ParseBytes(File.ReadAllBytes(Sample(F09)), F09);
        Assert.Equal(a.FileHash, b.FileHash);
        Assert.Equal(a.Readings, b.Readings);
    }

    [Theory]
    [InlineData(201, UploadOutcome.Accepted)]
    [InlineData(200, UploadOutcome.Accepted)]
    [InlineData(408, UploadOutcome.Transient)]
    [InlineData(429, UploadOutcome.Transient)]
    [InlineData(500, UploadOutcome.Transient)]
    [InlineData(502, UploadOutcome.Transient)]
    [InlineData(503, UploadOutcome.Transient)]
    [InlineData(401, UploadOutcome.Unauthorized)]
    [InlineData(403, UploadOutcome.Unauthorized)]
    [InlineData(400, UploadOutcome.Rejected)]
    [InlineData(413, UploadOutcome.Rejected)]
    [InlineData(415, UploadOutcome.Rejected)]
    [InlineData(422, UploadOutcome.Rejected)]
    public void StatusCodes_AreClassified(int status, UploadOutcome expected) =>
        Assert.Equal(expected, ClimateUpload.Classify((HttpStatusCode)status));

    [Theory]
    [InlineData("https://qaopjmaiqezvrskpnrsx.supabase.co/functions/v1/climate", DestinationKind.PlantsProduction, false)]
    [InlineData("https://rngfabuorwpthcokjncf.supabase.co/functions/v1/climate/", DestinationKind.PlantsStaging, false)]
    [InlineData("https://qaopjmaiqezvrskpnrsx.supabase.co", DestinationKind.PlantsProduction, true)]
    [InlineData("https://croplink.example.com", DestinationKind.CropLinkLegacy, false)]
    [InlineData("http://croplink.example.com", DestinationKind.CropLinkLegacy, true)]
    [InlineData("https://example.com/x", DestinationKind.Unknown, false)]
    [InlineData("not a url", DestinationKind.Invalid, false)]
    [InlineData("", DestinationKind.NotSet, false)]
    public void Destination_IsNamed(string url, DestinationKind kind, bool warns)
    {
        var d = Destination.Describe(url);
        Assert.Equal(kind, d.Kind);
        Assert.Equal(warns, d.Warning is not null);
    }

    [Fact]
    public void RetrySchedule_BacksOff_AndRespectsRetryAfter()
    {
        var s = new RetrySchedule([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)]);
        var t0 = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(t0.AddMinutes(1), s.RecordFailure("a", "x", t0));
        Assert.Equal(t0.AddMinutes(5), s.RecordFailure("a", "x", t0));
        Assert.Equal(t0.AddMinutes(5), s.RecordFailure("a", "x", t0)); // capped at the last step
        Assert.Equal(t0.AddMinutes(10), s.RecordFailure("b", "x", t0, TimeSpan.FromMinutes(10)));
        Assert.False(s.IsDue("a", t0.AddMinutes(4)));
        Assert.True(s.IsDue("a", t0.AddMinutes(5)));
        Assert.True(s.IsDue("never-failed", t0));
        s.MakeAllDue(t0);
        Assert.True(s.IsDue("b", t0));
    }

    // ── The whole flow: watcher → upload → Uploaded / kept / Failed ────────────

    [Fact]
    public async Task Accepted_MovesToUploaded_OnlyAfterTheServerAnswered()
    {
        using var server = new TestServer().Start();
        using var agent = new AgentHarness(server.BaseUrl, Key);
        var src = agent.Drop(Sample(F08));
        agent.Watcher.Start();

        Assert.True(await AgentHarness.WaitUntil(() => File.Exists(agent.UploadedPath(F08)), Wait));
        Assert.False(File.Exists(src));
        var req = Assert.Single(server.Requests);
        Assert.Equal("/functions/v1/climate/api/v1/climate/imports", req.Path);
        Assert.Equal($"Bearer {Key}", req.Authorization);
        using var doc = JsonDocument.Parse(req.Body);
        Assert.Equal(File.ReadAllBytes(Sample(F08)), Convert.FromBase64String(doc.RootElement.GetProperty("raw_csv_base64").GetString()!));
        Assert.Equal(File.ReadAllBytes(Sample(F08)), File.ReadAllBytes(agent.UploadedPath(F08))); // the file itself is untouched
    }

    [Fact]
    public async Task ServerStoredItAsDuplicateConflictOrQuarantined_StillMovesToUploaded()
    {
        using var server = new TestServer().Start();
        server.Script(new TestServer.Reply(200, """{"status":"duplicate","import_id":"x"}"""),
                      new TestServer.Reply(200, """{"status":"quarantined","import_id":"y"}"""));
        using var agent = new AgentHarness(server.BaseUrl, Key);
        agent.Drop(Sample(F08));
        agent.Drop(Sample(F09));
        agent.Watcher.Start();
        Assert.True(await AgentHarness.WaitUntil(() => File.Exists(agent.UploadedPath(F08)) && File.Exists(agent.UploadedPath(F09)), Wait));
        Assert.Equal(0, agent.Upload.Summary.PendingRetry);
    }

    [Fact]
    public async Task ShortOutage_RetriedWithinTheSameAttempt()
    {
        using var server = new TestServer().Start();
        server.Script(new TestServer.Reply(503), new TestServer.Reply(429, RetryAfterSeconds: 1));
        using var agent = new AgentHarness(server.BaseUrl, Key);
        agent.Drop(Sample(F08));
        var started = DateTime.UtcNow;
        agent.Watcher.Start();
        Assert.True(await AgentHarness.WaitUntil(() => File.Exists(agent.UploadedPath(F08)), Wait));
        Assert.Equal(3, server.Requests.Count);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(1), "Retry-After was not honoured");
        Assert.False(Directory.Exists(Path.Combine(agent.Folder, "Failed")));
    }

    [Fact]
    public async Task ServerErrors_KeepTheFile_ThenItRecoversByItself()
    {
        using var server = new TestServer().Start();
        server.Default = new TestServer.Reply(503, """{"error":"server_error"}""");
        using var agent = new AgentHarness(server.BaseUrl, Key);
        var src = agent.Drop(Sample(F08));
        agent.Watcher.Start();

        Assert.True(await AgentHarness.WaitUntil(() => agent.Upload.Summary.PendingRetry == 1, Wait));
        Assert.True(File.Exists(src), "a file must stay in the folder while the server fails");
        Assert.False(Directory.Exists(Path.Combine(agent.Folder, "Failed")));

        server.Default = new TestServer.Reply(201, """{"status":"created","import_id":"z"}""");
        Assert.True(await AgentHarness.WaitUntil(() => File.Exists(agent.UploadedPath(F08)), Wait));
        Assert.True(await AgentHarness.WaitUntil(() => agent.Upload.Summary.PendingRetry == 0, Wait));
    }

    [Fact]
    public async Task ProlongedOutage_NoServerAtAll_EveryFileRecoversWhenItReturns()
    {
        var port = TestServer.FreePort();
        using var agent = new AgentHarness($"http://127.0.0.1:{port}/functions/v1/climate", Key);
        var files = new[] { F08, F09, "Block Summary_20260717_100005.csv", "Block Summary_20260717_110006.csv" };
        foreach (var f in files) agent.Drop(Sample(f));
        agent.Watcher.Start();

        // Nothing is listening: connection refused, again and again.
        Assert.True(await AgentHarness.WaitUntil(() => agent.Upload.Summary.PendingRetry == files.Length, Wait));
        await Task.Delay(1500); // several rescans during the outage
        Assert.All(files, f => Assert.True(File.Exists(Path.Combine(agent.Folder, f))));
        Assert.False(Directory.Exists(Path.Combine(agent.Folder, "Failed")));
        // A file exported during the outage waits too.
        agent.Drop(Sample(F08), "Block Summary_20260717_120004.csv");

        using var server = new TestServer(port).Start();
        Assert.True(await AgentHarness.WaitUntil(() => files.Append("Block Summary_20260717_120004.csv").All(f => File.Exists(agent.UploadedPath(f))), Wait));
        Assert.True(await AgentHarness.WaitUntil(() => agent.Upload.Summary.PendingRetry == 0, Wait));
    }

    [Fact]
    public async Task WrongKey_KeepsTheFile_AndSaysSo_ThenUploadsOnceFixed()
    {
        using var server = new TestServer().Start();
        server.Default = new TestServer.Reply(401, """{"error":"unauthorized"}""");
        using var agent = new AgentHarness(server.BaseUrl, "plk_wrong");
        var src = agent.Drop(Sample(F08));
        agent.Watcher.Start();

        Assert.True(await AgentHarness.WaitUntil(() => agent.Upload.Summary.PendingRetry == 1, Wait));
        Assert.True(File.Exists(src));
        Assert.Single(server.Requests); // an auth failure isn't hammered with quick retries
        Assert.Contains("upload key refused", agent.Upload.Summary.LastProblem);

        agent.Credentials.Key = Key;
        server.Default = new TestServer.Reply(201, """{"status":"created"}""");
        Assert.True(await AgentHarness.WaitUntil(() => File.Exists(agent.UploadedPath(F08)), Wait));
    }

    [Fact]
    public async Task PermanentRejection_GoesToFailedWithTheReason_AndCanBeRetried()
    {
        using var server = new TestServer().Start();
        server.Script(new TestServer.Reply(422, """{"error":"raw_csv_required"}"""));
        using var agent = new AgentHarness(server.BaseUrl, Key);
        agent.Drop(Sample(F08));
        agent.Watcher.Start();

        Assert.True(await AgentHarness.WaitUntil(() => File.Exists(agent.FailedPath(F08)), Wait));
        Assert.Contains("422", File.ReadAllText(agent.FailedPath(F08) + ".error.txt"));
        Assert.Equal(1, agent.Upload.Summary.Failed);
        Assert.Single(server.Requests); // not retried: retrying wouldn't change the answer

        Assert.Equal(1, agent.Upload.RequeueFailedBlockSummaryFiles());
        Assert.False(File.Exists(agent.FailedPath(F08) + ".error.txt"));
        Assert.True(await AgentHarness.WaitUntil(() => File.Exists(agent.UploadedPath(F08)), Wait));
    }

    [Fact]
    public async Task NotABlockSummary_IsLeftAlone_AndNotReReadEveryScan()
    {
        using var server = new TestServer().Start();
        using var agent = new AgentHarness(server.BaseUrl, Key);
        var other = Path.Combine(agent.Folder, "notes.csv");
        File.WriteAllText(other, "a,b\n1,2\n");
        agent.Watcher.Start();
        await Task.Delay(2500); // several rescans
        Assert.True(File.Exists(other));
        Assert.Empty(server.Requests);
        lock (agent.Lines)
            Assert.Single(agent.Lines, l => l.Contains("'notes.csv' is not a valid Block Summary CSV"));
    }

    [Fact]
    public async Task RetryNow_TriesAWaitingFileImmediately()
    {
        using var server = new TestServer().Start();
        server.Default = new TestServer.Reply(503);
        using var agent = new AgentHarness(server.BaseUrl, Key, backoff: [TimeSpan.FromHours(1)]);
        agent.Drop(Sample(F08));
        agent.Watcher.Start();
        Assert.True(await AgentHarness.WaitUntil(() => agent.Upload.Summary.PendingRetry == 1, Wait));
        server.Default = new TestServer.Reply(201, """{"status":"created"}""");
        await Task.Delay(1500);
        Assert.False(File.Exists(agent.UploadedPath(F08))); // still waiting out the hour

        agent.Upload.RetryNow();
        agent.Watcher.Rescan();
        Assert.True(await AgentHarness.WaitUntil(() => File.Exists(agent.UploadedPath(F08)), Wait));
    }
}
