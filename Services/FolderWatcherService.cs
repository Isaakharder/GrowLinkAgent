namespace FlowMasterAgent.Services;

public class FolderWatcherService : IDisposable
{
    private static readonly string[] SupportedExtensions = [".pdf", ".csv"];

    private readonly ConfigService _config;
    private readonly UploadService _upload;
    private readonly LogService _log;

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public event Action<string>? StatusChanged;
    public bool IsWatching { get; private set; }

    public FolderWatcherService(ConfigService config, UploadService upload, LogService log)
    {
        _config = config;
        _upload = upload;
        _log = log;
    }

    public void Start()
    {
        Stop();

        var folders = new List<string>();
        var watchFolder = _config.Config.WatchFolder?.Trim();
        if (!string.IsNullOrEmpty(watchFolder))
            folders.Add(watchFolder);

        var blockSummaryFolder = _config.Config.BlockSummaryFolder?.Trim();
        if (!string.IsNullOrEmpty(blockSummaryFolder) &&
            !folders.Any(f => PathsEqual(f, blockSummaryFolder)))
        {
            folders.Add(blockSummaryFolder);
        }

        if (folders.Count == 0)
        {
            _log.Warn("No watch folders are configured.");
            NotifyStatus("Not watching — no folders configured.");
            return;
        }

        var started = new List<string>();

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                _log.Warn($"Watch folder does not exist: {folder}");
                continue;
            }

            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            watcher.Created += OnCreated;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatcherError;

            _watchers.Add(watcher);
            started.Add(folder);
            _log.Info($"Watching: {folder}");

            var folderToScan = folder;
            _ = Task.Run(() => ScanExistingAsync(folderToScan));
        }

        IsWatching = started.Count > 0;

        if (started.Count == 0)
        {
            NotifyStatus("Not watching — folder(s) not found.");
            return;
        }

        NotifyStatus(started.Count == 1
            ? $"Watching: {started[0]}"
            : $"Watching {started.Count} folders: {string.Join(", ", started)}");
    }

    public void Stop()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        IsWatching = false;
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

    private void OnCreated(object _, FileSystemEventArgs e) => Enqueue(e.FullPath, "watcher-created");

    private void OnRenamed(object _, RenamedEventArgs e)
    {
        Enqueue(e.FullPath, "watcher-renamed");
    }

    private void OnWatcherError(object _, ErrorEventArgs e) =>
        _log.Error($"Watcher error: {e.GetException().Message}");

    private void Enqueue(string filePath, string source)
    {
        var containsUploaded = PathContainsSegment(filePath, "Uploaded");
        var containsFailed = PathContainsSegment(filePath, "Failed");
        _log.Info($"Discovered ({source}): path='{filePath}', containsUploaded={containsUploaded}, containsFailed={containsFailed}");

        if (containsUploaded || containsFailed)
        {
            _log.Info($"Excluded ({source}): path='{filePath}' is under Uploaded/Failed.");
            return;
        }

        if (!IsSupportedFile(filePath))
        {
            _log.Info($"Ignored ({source}): path='{filePath}' unsupported extension.");
            return;
        }

        lock (_lock)
        {
            if (_inFlight.Contains(filePath))
            {
                _log.Info($"Ignored ({source}): path='{filePath}' already in flight.");
                return;
            }
            _inFlight.Add(filePath);
        }

        _ = Task.Run(async () =>
        {
            try { await _upload.ProcessFileAsync(filePath); }
            finally { lock (_lock) _inFlight.Remove(filePath); }
        });
    }

    private async Task ScanExistingAsync(string folder)
    {
        try
        {
            var files = Directory
                .EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .ToArray();
            if (files.Length == 0)
            {
                _log.Info("Startup scan: no files found in watch root.");
                return;
            }

            _log.Info($"Startup scan: {files.Length} file(s) found in watch root.");
            foreach (var f in files)
                Enqueue(f, "startup-scan");
        }
        catch (Exception ex)
        {
            _log.Error($"Startup scan failed: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private void NotifyStatus(string status) => StatusChanged?.Invoke(status);

    private static bool IsSupportedFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static bool PathContainsSegment(string filePath, string segment)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var parts = filePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => string.Equals(p, segment, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
