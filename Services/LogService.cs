namespace FlowMasterAgent.Services;

public class LogService
{
    private readonly string LogDir;

    private readonly object _lock = new();

    /// <param name="logDir">Where the daily log files go (default %APPDATA%\GrowLinkAgent\logs).</param>
    public LogService(string? logDir = null) =>
        LogDir = logDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrowLinkAgent", "logs");

    public event Action<string>? OnLog;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";

        try { OnLog?.Invoke(line); } catch { }

        try
        {
            Directory.CreateDirectory(LogDir);
            var logFile = Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
            lock (_lock)
            {
                File.AppendAllText(logFile, line + Environment.NewLine);
            }
        }
        catch { }
    }
}
