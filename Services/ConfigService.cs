using System.Text.Json;
using FlowMasterAgent.Models;

namespace FlowMasterAgent.Services;

public class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GrowLinkAgent");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private readonly LogService _log;
    private AppConfig _config = new();

    public ConfigService(LogService log)
    {
        _log = log;
        Load();
    }

    public AppConfig Config => _config;

    private void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return;

            var json = File.ReadAllText(ConfigPath);
            _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load config: {ex.Message}");
        }
    }

    public string? GetRouteServerUrl(string routeId)
    {
        var found = _config.RouteConfigs.FirstOrDefault(r =>
            string.Equals(r.RouteId, routeId, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(found?.ServerUrl) ? null : found!.ServerUrl.Trim();
    }

    public void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            _config = config;
            _log.Info("Configuration saved.");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save config: {ex.Message}");
            throw;
        }
    }
}
