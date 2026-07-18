namespace FlowMasterAgent.Services;

public class LogService
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GrowLinkAgent", "logs");

    private readonly object _lock = new();

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
