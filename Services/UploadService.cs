using System.Text.Json;
using System.Collections.Concurrent;
using FlowMasterAgent.Models;

namespace FlowMasterAgent.Services;

public class UploadService
{
    private record UploadResult(bool Success, bool IsSkipped, decimal KgImported = 0, int VarietiesCount = 0);
    private sealed record FileReadyCheckResult(bool IsReady, bool ExistsAtStart, string? LastLockError = null);
    public sealed record RoutePingResult(string RouteId, string RouteName, bool Configured, bool Success, string Message, string? OrganizationName = null);

    private const int MaxRetries = 3;
    private const int RetryDelayMs = 5_000;
    private const int FileReadyTimeoutMs = 15_000;

    /// <summary>Waits between the quick attempts at one Block Summary file (then it goes on the retry schedule).</summary>
    public static readonly TimeSpan[] DefaultClimateAttemptDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(15);

    /// <summary>The end of one Block Summary upload attempt.</summary>
    private sealed record ClimateAttempt(UploadOutcome Outcome, int? HttpStatus, string Detail, TimeSpan? RetryAfter = null);

    private readonly ConfigService _config;
    private readonly CredentialService _credentials;
    private readonly LogService _log;
    private readonly FileRoutingService _routing;
    private readonly HttpClient _http;
    private readonly RetrySchedule _schedule;
    private readonly TimeSpan[] _climateAttemptDelays;
    // Files that matched no route, or sit in the Block Summary folder without being Block Summary
    // exports: not looked at again on every rescan unless they change (path → length/last-write).
    private readonly ConcurrentDictionary<string, (long Length, DateTime LastWriteUtc)> _ignoredUntilChanged = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _summaryLock = new();
    private readonly ConcurrentDictionary<string, byte> _activeFiles = new(StringComparer.OrdinalIgnoreCase);

    public BatchSummary Summary { get; } = new();
    public event Action<BatchSummary>? SummaryUpdated;

    public UploadService(ConfigService config, CredentialService credentials, LogService log,
        RetrySchedule? schedule = null, TimeSpan[]? climateAttemptDelays = null, HttpClient? http = null)
    {
        _config = config;
        _credentials = credentials;
        _log = log;
        _routing = new FileRoutingService();
        _schedule = schedule ?? new RetrySchedule();
        _climateAttemptDelays = climateAttemptDelays ?? DefaultClimateAttemptDelays;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>
    /// Whether a rescan should pick this file up now: not waiting out a retry delay, and not a
    /// non-Block-Summary file that hasn't changed since it was looked at.
    /// </summary>
    public bool IsDue(string path)
    {
        if (!_schedule.IsDue(path, DateTime.UtcNow)) return false;
        if (_ignoredUntilChanged.TryGetValue(path, out var seen))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length == seen.Length && info.LastWriteTimeUtc == seen.LastWriteUtc) return false;
            }
            catch { /* fall through: look again */ }
            _ignoredUntilChanged.TryRemove(path, out _);
        }
        return true;
    }

    /// <summary>Forgets files that have gone and refreshes the waiting count.</summary>
    public void PruneRetries()
    {
        _schedule.Prune();
        UpdatePending();
    }

    /// <summary>"Retry now": every file waiting for a retry is tried at the next rescan.</summary>
    public void RetryNow()
    {
        _schedule.MakeAllDue(DateTime.UtcNow);
        _log.Info("Retry now: waiting files will be tried at once.");
    }

    /// <summary>
    /// "Retry failed files": moves Block Summary files from the Failed folder back into the
    /// Block Summary folder so they are uploaded again (the server recognises duplicates).
    /// </summary>
    public int RequeueFailedBlockSummaryFiles()
    {
        var folder = _config.Config.BlockSummaryFolder?.Trim();
        if (string.IsNullOrEmpty(folder)) return 0;
        var failed = Path.Combine(folder, "Failed");
        if (!Directory.Exists(failed)) return 0;
        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(failed, "*.csv", SearchOption.TopDirectoryOnly))
        {
            var dest = Path.Combine(folder, Path.GetFileName(file));
            if (File.Exists(dest))
            {
                _log.Warn($"Retry failed files: '{Path.GetFileName(file)}' is already in the folder; left in Failed.");
                continue;
            }
            try
            {
                File.Move(file, dest);
                var note = file + ".error.txt";
                if (File.Exists(note)) File.Delete(note);
                moved++;
            }
            catch (Exception ex)
            {
                _log.Error($"Retry failed files: couldn't move '{file}': {ex.Message}");
            }
        }
        _log.Info($"Retry failed files: {moved} file(s) moved back for upload.");
        return moved;
    }

    private void IgnoreUntilChanged(string path)
    {
        try
        {
            var info = new FileInfo(path);
            _ignoredUntilChanged[path] = (info.Length, info.LastWriteTimeUtc);
        }
        catch { /* it will simply be looked at again */ }
    }

    private void UpdatePending()
    {
        lock (_summaryLock)
        {
            Summary.PendingRetry = _schedule.Count;
            Summary.NextRetryUtc = _schedule.Next?.NextUtc;
        }
        SummaryUpdated?.Invoke(Summary);
    }

    public async Task<bool> PingAsync()
    {
        var serverUrl = _config.Config.ServerUrl?.Trim();
        if (string.IsNullOrEmpty(serverUrl))
        {
            _log.Warn("Cannot ping: server URL is not configured.");
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{serverUrl.TrimEnd('/')}/api/agent/ping");

            var key = _credentials.LoadKey();
            if (!string.IsNullOrEmpty(key))
                request.Headers.TryAddWithoutValidation("X-Upload-Key", key);

            var response = await _http.SendAsync(request);
            _log.Info($"Ping → {(int)response.StatusCode} {response.ReasonPhrase}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.Error($"Ping failed: {ex.Message}");
            return false;
        }
    }

    public async Task<IReadOnlyList<RoutePingResult>> PingRouteKeysAsync(IEnumerable<UploadRoute> routes)
    {
        var results = new List<RoutePingResult>();
        foreach (var route in routes)
        {
            var routeServerUrl = _config.GetRouteServerUrl(route.Id);
            if (string.IsNullOrWhiteSpace(routeServerUrl))
            {
                results.Add(new RoutePingResult(route.Id, route.Name, false, false, "No server URL configured"));
                continue;
            }

            var slot = string.IsNullOrWhiteSpace(route.KeySlot) ? route.Id : route.KeySlot;
            var key = _credentials.LoadRouteKey(slot);
            if (string.IsNullOrWhiteSpace(key))
            {
                results.Add(new RoutePingResult(route.Id, route.Name, false, false, "No key configured"));
                continue;
            }

            var result = string.Equals(route.Id, "block-summary", StringComparison.OrdinalIgnoreCase)
                ? await PingCropLinkAsync(route, routeServerUrl, key, slot)
                : await PingGrowLinkAsync(route, routeServerUrl, key, slot);
            results.Add(result);
        }

        return results;
    }

    // FlowMaster/GrowLink: unchanged legacy ping — POST /api/agent/ping with X-Upload-Key.
    private async Task<RoutePingResult> PingGrowLinkAsync(UploadRoute route, string routeServerUrl, string key, string slot)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{routeServerUrl.TrimEnd('/')}/api/agent/ping");
            request.Headers.TryAddWithoutValidation("X-Upload-Key", key);

            var response = await _http.SendAsync(request);
            var ok = response.IsSuccessStatusCode;
            var message = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            _log.Info($"Route ping [{route.Name}] ({slot}) -> {message}");
            return new RoutePingResult(route.Id, route.Name, true, ok, message);
        }
        catch (Exception ex)
        {
            _log.Error($"Route ping failed [{route.Name}] ({slot}): {ex.Message}");
            return new RoutePingResult(route.Id, route.Name, true, false, ex.Message);
        }
    }

    // Synopta/CropLink: GET /api/v1/climate/ping with Authorization: Bearer <key>.
    private async Task<RoutePingResult> PingCropLinkAsync(UploadRoute route, string routeServerUrl, string key, string slot)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{routeServerUrl.TrimEnd('/')}/api/v1/climate/ping");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            var message = $"{(int)response.StatusCode} {response.ReasonPhrase}";

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"Route ping [{route.Name}] ({slot}) -> {message}: {body}");
                return new RoutePingResult(route.Id, route.Name, true, false, message);
            }

            string? orgName = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("organization_name", out var el))
                    orgName = el.GetString();
            }
            catch { /* leave orgName null if the body is unparseable */ }

            _log.Info($"Route ping [{route.Name}] ({slot}) -> {message} (organization: {orgName})");
            return new RoutePingResult(route.Id, route.Name, true, true, message, orgName);
        }
        catch (Exception ex)
        {
            _log.Error($"Route ping failed [{route.Name}] ({slot}): {ex.Message}");
            return new RoutePingResult(route.Id, route.Name, true, false, ex.Message);
        }
    }

    public async Task ProcessFileAsync(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var processingId = Guid.NewGuid();

        if (!_activeFiles.TryAdd(fullPath, 0))
        {
            _log.Warn($"Duplicate processing prevented: id={processingId}, path='{fullPath}'");
            return;
        }

        try
        {
            _log.Info($"Processing start: id={processingId}, path='{fullPath}'");

            if (!File.Exists(fullPath))
            {
                _log.Warn($"Processing abort (file missing at start): id={processingId}, path='{fullPath}'");
                return;
            }

            lock (_summaryLock) Summary.Found++;
            var fileName = Path.GetFileName(fullPath);
            _log.Info($"Queued: {fileName}");
            _log.Info($"Queued path: {fullPath}");

            var routeMode = _config.Config.UseRouteKeys;
            var route = _routing.MatchRoute(fullPath);

            if (route is null)
            {
                var (folderRoute, rejected) = TryQualifyBlockSummaryFolder(fullPath, fileName);
                if (rejected)
                {
                    lock (_summaryLock)
                    {
                        Summary.Skipped++;
                        Summary.LastSync = DateTime.Now;
                    }
                    SummaryUpdated?.Invoke(Summary);
                    return; // Not a valid Block Summary CSV — leave file in place, do not move or retry.
                }
                route = folderRoute;
            }

            if (route is null)
                _log.Info($"Routing: filename='{fileName}' -> No matching route");
            else
                _log.Info($"Routing: filename='{fileName}' -> matched route name='{route.Name}', id='{route.Id}', slot='{route.KeySlot}'");

            _log.Info($"Path consistency: routing and readiness use full path='{fullPath}'");

            string? keyOverride = null;
            string? serverUrlOverride = null;
            if (routeMode)
            {
                if (route is null)
                {
                    IgnoreUntilChanged(fullPath);
                    return;
                }

                var slot = string.IsNullOrWhiteSpace(route.KeySlot) ? route.Id : route.KeySlot;
                keyOverride = _credentials.LoadRouteKey(slot);
                if (string.IsNullOrWhiteSpace(keyOverride))
                {
                    _log.Error($"Matched route '{route.Name}' ({route.Id}) has no configured key in slot '{slot}'. Skipping upload and leaving file in place.");
                    return;
                }

                serverUrlOverride = _config.GetRouteServerUrl(route.Id);
                if (string.IsNullOrWhiteSpace(serverUrlOverride))
                {
                    _log.Error($"Matched route '{route.Name}' ({route.Id}) has no server URL configured. Leaving file in place.");
                    return;
                }
            }

            if (route?.Id == "block-summary")
            {
                await ProcessBlockSummaryAsync(fullPath, fileName, keyOverride, serverUrlOverride);
                return;
            }

            bool wasDuplicate = false;
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                var existsBeforeReady = File.Exists(fullPath);
                _log.Info($"Readiness pre-check: id={processingId}, path='{fullPath}', exists={existsBeforeReady}");

                var ready = await WaitForFileReady(fullPath);
                if (!ready.IsReady)
                {
                    var detail = string.IsNullOrWhiteSpace(ready.LastLockError)
                        ? "no lock exception captured"
                        : ready.LastLockError;

                    _log.Warn($"File not ready after {FileReadyTimeoutMs / 1000}s, skipping: {fileName}; id={processingId}; path='{fullPath}'; existsAtStart={ready.ExistsAtStart}; lockDetail='{detail}'");

                    lock (_summaryLock)
                    {
                        Summary.Skipped++;
                        Summary.LastSync = DateTime.Now;
                    }
                    SummaryUpdated?.Invoke(Summary);

                    // Not-ready files must remain in place for later processing.
                    return;
                }

                try
                {
                    var result = await UploadFileAsync(fullPath, keyOverride, serverUrlOverride);
                    if (result.Success)
                    {
                        lock (_summaryLock)
                        {
                            Summary.Succeeded++;
                            Summary.KgImported += result.KgImported;
                            Summary.VarietiesImported += result.VarietiesCount;
                            Summary.LastSync = DateTime.Now;
                        }
                        SummaryUpdated?.Invoke(Summary);
                        MoveFile(fullPath, UploadedFolder(fullPath));
                        return;
                    }
                    if (result.IsSkipped)
                        wasDuplicate = true;
                }
                catch (Exception ex)
                {
                    _log.Error($"Attempt {attempt}/{MaxRetries} failed for {fileName}: {ex.Message}");
                }

                if (attempt < MaxRetries)
                {
                    _log.Info($"Retrying {fileName} in {RetryDelayMs / 1000}s…");
                    await Task.Delay(RetryDelayMs);
                }
            }

            lock (_summaryLock)
            {
                if (wasDuplicate) Summary.Skipped++;
                else Summary.Failed++;
                Summary.LastSync = DateTime.Now;
            }
            SummaryUpdated?.Invoke(Summary);
            MoveFile(fullPath, FailedFolder(fullPath));
        }
        finally
        {
            _activeFiles.TryRemove(fullPath, out _);
            _log.Info($"Processing end: id={processingId}, path='{fullPath}'");
        }
    }

    // Qualifies a .csv file for the Block Summary/CropLink route by *location* rather than
    // filename: the climate computer's Synopta exports use generic numeric names (e.g.
    // "080003.csv") that must not be globally treated as Block Summary data. Only files sitting
    // directly in the configured Block Summary folder are speculatively parsed; the parser's
    // acceptance (not the filename) is what actually qualifies the file for upload.
    private (UploadRoute? Route, bool Rejected) TryQualifyBlockSummaryFolder(string fullPath, string fileName)
    {
        var configuredFolder = _config.Config.BlockSummaryFolder?.Trim();
        if (string.IsNullOrEmpty(configuredFolder))
            return (null, false);

        if (!string.Equals(Path.GetExtension(fullPath), ".csv", StringComparison.OrdinalIgnoreCase))
            return (null, false);

        var fileDir = Path.GetDirectoryName(fullPath);
        if (fileDir is null || !PathsEqual(fileDir, configuredFolder))
            return (null, false);

        try
        {
            var parsed = new BlockSummaryCsvParser().Parse(fullPath);
            _log.Info($"Block Summary folder: '{fileName}' parsed successfully ({parsed.Readings.Count} reading(s)) — qualifying for CropLink upload.");
            var route = _routing.GetDefaultRoutes().FirstOrDefault(r => r.Id == "block-summary");
            return (route, false);
        }
        catch (InvalidDataException ex)
        {
            _log.Warn($"Block Summary folder: '{fileName}' is not a valid Block Summary CSV ({ex.Message}) — leaving file in place, not uploading.");
            IgnoreUntilChanged(fullPath);
            return (null, true);
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<UploadResult> UploadFileAsync(string filePath, string? keyOverride = null, string? serverUrlOverride = null)
    {
        var serverUrl = serverUrlOverride ?? _config.Config.ServerUrl?.Trim();
        if (string.IsNullOrEmpty(serverUrl))
        {
            _log.Error("Upload aborted: server URL is not configured.");
            return new UploadResult(false, false);
        }

        var key = keyOverride ?? _credentials.LoadKey();
        if (string.IsNullOrEmpty(key))
        {
            _log.Error("Upload aborted: upload key is not configured.");
            return new UploadResult(false, false);
        }

        using var content = new MultipartFormDataContent();
        await using var stream = File.OpenRead(filePath);
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GetContentType(filePath));
        content.Add(fileContent, "files", Path.GetFileName(filePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl.TrimEnd('/')}/api/agent/pdf-import")
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("X-Upload-Key", key);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            _log.Info($"Uploaded: {Path.GetFileName(filePath)}");
            var (kg, varieties) = TryParseServerSummary(body);
            return new UploadResult(true, false, kg, varieties);
        }

        if ((int)response.StatusCode == 409)
        {
            _log.Warn($"Duplicate skipped: {Path.GetFileName(filePath)}");
            return new UploadResult(false, true);
        }

        _log.Error($"Upload rejected ({(int)response.StatusCode}): {Path.GetFileName(filePath)} — {body[..Math.Min(200, body.Length)]}");
        return new UploadResult(false, false);
    }

    // Block Summary (climate) files. A file moves to Uploaded only after the server answered 2xx,
    // which the climate server sends only once the file's original bytes and status are committed.
    // Temporary problems (network, timeouts, 408/425/429, 5xx) and key problems (401/403) never
    // move a file: it stays in the folder and is retried with growing delays, so it recovers on its
    // own after an outage or once the key is fixed. Any other 4xx means this file can't be accepted
    // as sent: it moves to Failed with a note, and "Retry failed files" brings it back.
    private async Task ProcessBlockSummaryAsync(string fullPath, string fileName, string? keyOverride, string? serverUrlOverride)
    {
        var ready = await WaitForFileReady(fullPath);
        if (!ready.IsReady)
        {
            _log.Warn($"File not ready after {FileReadyTimeoutMs / 1000}s; left in place for the next scan: {fileName}");
            lock (_summaryLock) { Summary.Skipped++; Summary.LastSync = DateTime.Now; }
            SummaryUpdated?.Invoke(Summary);
            return;
        }

        ClimateAttempt attempt = new(UploadOutcome.Transient, null, "not attempted");
        for (var i = 0; ; i++)
        {
            attempt = await UploadBlockSummaryAsync(fullPath, keyOverride, serverUrlOverride);
            if (attempt.Outcome != UploadOutcome.Transient || i >= _climateAttemptDelays.Length) break;
            var wait = attempt.RetryAfter is { } ra && ra > _climateAttemptDelays[i] ? ra : _climateAttemptDelays[i];
            _log.Warn($"{fileName}: {attempt.Detail} — trying again in {wait.TotalSeconds:0}s (attempt {i + 2}/{_climateAttemptDelays.Length + 1}).");
            await Task.Delay(wait);
        }

        switch (attempt.Outcome)
        {
            case UploadOutcome.Accepted:
                _schedule.Clear(fullPath);
                lock (_summaryLock) { Summary.Succeeded++; Summary.LastSync = DateTime.Now; Summary.LastProblem = null; }
                MoveFile(fullPath, UploadedFolder(fullPath));
                break;

            case UploadOutcome.Transient:
            case UploadOutcome.Unauthorized:
            {
                var next = _schedule.RecordFailure(fullPath, attempt.Detail, DateTime.UtcNow, attempt.RetryAfter);
                var why = attempt.Outcome == UploadOutcome.Unauthorized
                    ? $"upload key refused ({attempt.HttpStatus}) — check the key in the Synopta tab"
                    : attempt.Detail;
                _log.Warn($"{fileName}: {why}. Kept in the folder; next try at {next.ToLocalTime():HH:mm:ss}.");
                lock (_summaryLock) { Summary.LastSync = DateTime.Now; Summary.LastProblem = $"{DateTime.Now:HH:mm} {why}"; }
                break;
            }

            case UploadOutcome.Rejected:
            {
                _schedule.Clear(fullPath);
                _log.Error($"{fileName}: refused by the server ({attempt.HttpStatus}): {attempt.Detail}. Moved to Failed; use \"Retry failed files\" after fixing the cause.");
                lock (_summaryLock) { Summary.Failed++; Summary.LastSync = DateTime.Now; Summary.LastProblem = $"{DateTime.Now:HH:mm} {fileName} refused ({attempt.HttpStatus})"; }
                var failedDir = FailedFolder(fullPath);
                MoveFile(fullPath, failedDir);
                try
                {
                    File.WriteAllText(Path.Combine(failedDir, fileName + ".error.txt"),
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} HTTP {attempt.HttpStatus}: {attempt.Detail}{Environment.NewLine}");
                }
                catch { /* the log has it */ }
                break;
            }
        }
        UpdatePending();
    }

    private async Task<ClimateAttempt> UploadBlockSummaryAsync(string filePath, string? keyOverride, string? serverUrlOverride)
    {
        var filename = Path.GetFileName(filePath);

        var serverUrl = serverUrlOverride ?? _config.Config.ServerUrl?.Trim();
        if (string.IsNullOrEmpty(serverUrl))
            return new ClimateAttempt(UploadOutcome.Unauthorized, null, "server URL is not configured");

        var key = keyOverride ?? _credentials.LoadKey();
        if (string.IsNullOrEmpty(key))
            return new ClimateAttempt(UploadOutcome.Unauthorized, null, "upload key is not configured");

        // Read once: these exact bytes are hashed, parsed and sent.
        byte[] raw;
        try
        {
            raw = await File.ReadAllBytesAsync(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClimateAttempt(UploadOutcome.Transient, null, $"couldn't read the file ({ex.Message})");
        }

        BlockSummaryCsvParser.ParseResult parsed;
        try
        {
            parsed = new BlockSummaryCsvParser().ParseBytes(raw, filename);
        }
        catch (InvalidDataException ex)
        {
            RecordCropLinkImport(filename, null, "Failed", null, null);
            return new ClimateAttempt(UploadOutcome.Rejected, null, $"not a readable Block Summary export ({ex.Message})");
        }

        var destination = Destination.Describe(serverUrl);
        _log.Info($"Climate upload to {destination.Label}: '{filename}' ({raw.Length} bytes, {parsed.Readings.Count} parsed readings, {BlockSummaryCsvParser.HashTag(raw)[..19]}…)");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl.TrimEnd('/')}/api/v1/climate/imports")
        {
            Content = new StringContent(ClimateUpload.BuildBody(raw, filename, parsed), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

        HttpResponseMessage response;
        string responseBody;
        try
        {
            response = await _http.SendAsync(request);
            responseBody = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ClimateUpload.IsTransient(ex))
        {
            RecordCropLinkImport(filename, parsed.Readings.Count, "Waiting to retry", null, null);
            return new ClimateAttempt(UploadOutcome.Transient, null, $"no answer from the server ({ex.GetType().Name}: {ex.Message})");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var outcome = ClimateUpload.Classify(response.StatusCode);
            if (outcome == UploadOutcome.Accepted)
            {
                var (label, importId) = ClimateUpload.ReadAcceptance(responseBody);
                _log.Info($"Climate upload accepted: '{filename}' — {status} {label} (import_id={importId})");
                RecordCropLinkImport(filename, parsed.Readings.Count, label, status, importId);
                return new ClimateAttempt(UploadOutcome.Accepted, status, label);
            }

            var snippet = responseBody[..Math.Min(300, responseBody.Length)];
            RecordCropLinkImport(filename, parsed.Readings.Count, outcome == UploadOutcome.Rejected ? "Failed" : "Waiting to retry", status, null);
            return new ClimateAttempt(outcome, status, $"HTTP {status} {snippet}", ClimateUpload.RetryAfter(response, MaxRetryAfter));
        }
    }

    // Records the outcome of an actual CropLink upload attempt for the Synopta tab.
    // Must only be called from UploadBlockSummaryAsync — never from the ping/connection test.
    private void RecordCropLinkImport(string filename, int? readingsParsed, string result, int? httpStatus, string? importId)
    {
        lock (_summaryLock)
        {
            Summary.LastCropLinkFilename = filename;
            Summary.LastCropLinkReadingsParsed = readingsParsed;
            Summary.LastCropLinkResult = result;
            Summary.LastCropLinkHttpStatus = httpStatus;
            Summary.LastCropLinkImportId = importId;
            Summary.LastCropLinkTime = DateTime.Now;
        }
        SummaryUpdated?.Invoke(Summary);
    }

    private static (decimal Kg, int Varieties) TryParseServerSummary(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            decimal kg = 0;
            int varieties = 0;

            foreach (var name in new[] { "kg_imported", "total_kg", "kgImported", "totalKg" })
                if (root.TryGetProperty(name, out var el) && el.TryGetDecimal(out var v)) { kg = v; break; }

            foreach (var name in new[] { "varieties_imported", "varieties_count", "varietiesImported", "varietiesCount" })
                if (root.TryGetProperty(name, out var el) && el.TryGetInt32(out var v)) { varieties = v; break; }

            return (kg, varieties);
        }
        catch { return (0, 0); }
    }

    private async Task<FileReadyCheckResult> WaitForFileReady(string filePath)
    {
        var existsAtStart = File.Exists(filePath);
        var extension = Path.GetExtension(filePath);
        var isCsv = string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase);
        string? lastLockError = null;
        var deadline = DateTime.UtcNow.AddMilliseconds(FileReadyTimeoutMs);

        if (isCsv)
        {
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var existsBeforeSize1 = File.Exists(filePath);
                    _log.Info($"CSV exists check before size#1: path='{filePath}', exists={existsBeforeSize1}");
                    if (!existsBeforeSize1)
                    {
                        _log.Warn($"CSV readiness decision: not ready; file missing during check. path='{filePath}'");
                        LogMissingFileDiagnostics(filePath, "before-size-1");
                        return new FileReadyCheckResult(false, existsAtStart, lastLockError);
                    }

                    var info = new FileInfo(filePath);
                    var size1 = info.Length;
                    _log.Info($"CSV readiness size check 1: path='{filePath}', size={size1}");
                    if (size1 == 0)
                    {
                        _log.Info($"CSV readiness decision: not ready yet; first size is 0. path='{filePath}'");
                        await Task.Delay(1000);
                        continue;
                    }

                    // CSV exporters may keep a handle open briefly. Allow shared read access.
                    using (File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }

                    await Task.Delay(1000);

                    var existsBeforeSize2 = File.Exists(filePath);
                    _log.Info($"CSV exists check before size#2: path='{filePath}', exists={existsBeforeSize2}");
                    if (!existsBeforeSize2)
                    {
                        _log.Warn($"CSV readiness decision: not ready; file missing before second size check. path='{filePath}'");
                        LogMissingFileDiagnostics(filePath, "before-size-2");
                        return new FileReadyCheckResult(false, existsAtStart, lastLockError);
                    }

                    info.Refresh();
                    if (!info.Exists)
                    {
                        _log.Warn($"CSV readiness decision: not ready; file missing before second size check. path='{filePath}'");
                        LogMissingFileDiagnostics(filePath, "after-refresh-before-size-2");
                        return new FileReadyCheckResult(false, existsAtStart, lastLockError);
                    }

                    var size2 = info.Length;
                    _log.Info($"CSV readiness size check 2: path='{filePath}', size={size2}");
                    var existsAfterSize2 = File.Exists(filePath);
                    _log.Info($"CSV exists check after size#2: path='{filePath}', exists={existsAfterSize2}");
                    if (!existsAfterSize2)
                        LogMissingFileDiagnostics(filePath, "after-size-2");

                    if (size1 == size2 && size2 > 0)
                    {
                        _log.Info($"CSV readiness decision: ready; size stable across checks ({size1} bytes). path='{filePath}'");
                        return new FileReadyCheckResult(true, existsAtStart, lastLockError);
                    }

                    _log.Info($"CSV readiness decision: not ready yet; size changed from {size1} to {size2}. path='{filePath}'");
                }
                catch (IOException ex)
                {
                    lastLockError = ex.Message;
                    _log.Warn($"CSV readiness lock/read error: path='{filePath}' -> {ex.Message}");
                }

                await Task.Delay(500);
            }

            _log.Warn($"CSV readiness decision: timeout after {FileReadyTimeoutMs / 1000}s. path='{filePath}'");
            return new FileReadyCheckResult(false, existsAtStart, lastLockError);
        }

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists)
                {
                    _log.Warn($"PDF readiness decision: not ready; file missing during check. path='{filePath}'");
                    return new FileReadyCheckResult(false, existsAtStart, lastLockError);
                }
                if (info.Length == 0)
                {
                    _log.Info($"PDF readiness decision: not ready yet; size is 0. path='{filePath}'");
                    await Task.Delay(500);
                    continue;
                }
                // Try to open exclusively — if it succeeds, the writer has finished.
                using var s = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                _log.Info($"PDF readiness decision: ready; exclusive lock acquired. path='{filePath}'");
                return new FileReadyCheckResult(true, existsAtStart, lastLockError);
            }
            catch (IOException ex)
            {
                lastLockError = ex.Message;
                _log.Warn($"PDF readiness lock error: path='{filePath}' -> {ex.Message}");
                await Task.Delay(500);
            }
        }
        _log.Warn($"PDF readiness decision: timeout after {FileReadyTimeoutMs / 1000}s. path='{filePath}'");
        return new FileReadyCheckResult(false, existsAtStart, lastLockError);
    }

    private void LogMissingFileDiagnostics(string filePath, string phase)
    {
        try
        {
            var watchFolder = _config.Config.WatchFolder?.Trim();
            var sourceDir = Path.GetDirectoryName(filePath) ?? string.Empty;
            var fileName = Path.GetFileName(filePath);
            var watchRoot = string.IsNullOrWhiteSpace(watchFolder) ? sourceDir : watchFolder;

            var watchRootPath = Path.Combine(watchRoot, fileName);
            var failedPath = Path.Combine(watchRoot, "Failed", fileName);
            var uploadedCurrentMonthPath = Path.Combine(watchRoot, "Uploaded", DateTime.Now.ToString("yyyy-MM"), fileName);

            _log.Warn($"Missing-file diagnostics ({phase}): sourcePath='{filePath}', watchRootPath='{watchRootPath}', failedPath='{failedPath}', uploadedCurrentMonthPath='{uploadedCurrentMonthPath}'");
            _log.Warn($"Missing-file diagnostics ({phase}): exists sourcePath={File.Exists(filePath)}, exists watchRootPath={File.Exists(watchRootPath)}, exists failedPath={File.Exists(failedPath)}, exists uploadedCurrentMonthPath={File.Exists(uploadedCurrentMonthPath)}");

            if (Directory.Exists(watchRoot))
            {
                var rootFiles = Directory.GetFiles(watchRoot);
                _log.Warn($"Missing-file diagnostics ({phase}): Directory.GetFiles('{watchRoot}') => [{string.Join("; ", rootFiles)}]");

                var uploadedDir = Path.Combine(watchRoot, "Uploaded");
                var failedDir = Path.Combine(watchRoot, "Failed");

                var uploadedMatches = Directory.Exists(uploadedDir)
                    ? Directory.EnumerateFiles(uploadedDir, fileName, SearchOption.AllDirectories).ToArray()
                    : Array.Empty<string>();
                var failedMatches = Directory.Exists(failedDir)
                    ? Directory.EnumerateFiles(failedDir, fileName, SearchOption.AllDirectories).ToArray()
                    : Array.Empty<string>();

                _log.Warn($"Missing-file diagnostics ({phase}): Uploaded matches => [{string.Join("; ", uploadedMatches)}]");
                _log.Warn($"Missing-file diagnostics ({phase}): Failed matches => [{string.Join("; ", failedMatches)}]");
            }
            else
            {
                _log.Warn($"Missing-file diagnostics ({phase}): watch root does not exist: '{watchRoot}'");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Missing-file diagnostics failed ({phase}) for '{filePath}': {ex.Message}");
        }
    }

    private void MoveFile(string source, string destDir)
    {
        try
        {
            Directory.CreateDirectory(destDir);
            _log.Info($"Move attempt: source='{source}', sourceExists={File.Exists(source)}, destDir='{destDir}'");
            var dest = UniqueDestPath(destDir, Path.GetFileName(source)!);
            _log.Info($"Move attempt destination: '{dest}'");
            File.Move(source, dest);
            _log.Info($"Moved → {Path.GetRelativePath(Path.GetDirectoryName(source)!, dest)}");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to move '{source}': {ex.Message}");
        }
    }

    private static string UploadedFolder(string filePath) =>
        Path.Combine(Path.GetDirectoryName(filePath)!, "Uploaded", DateTime.Now.ToString("yyyy-MM"));

    private static string FailedFolder(string filePath) =>
        Path.Combine(Path.GetDirectoryName(filePath)!, "Failed");

    private static string UniqueDestPath(string dir, string fileName)
    {
        var dest = Path.Combine(dir, fileName);
        if (!File.Exists(dest)) return dest;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; ; i++)
        {
            dest = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(dest)) return dest;
        }
    }

    private static string GetContentType(string filePath)
    {
        return Path.GetExtension(filePath).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? "text/csv"
            : "application/pdf";
    }
}
