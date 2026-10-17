using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlowMasterAgent.Services;

/// <summary>
/// Stores the upload key using Windows DPAPI (DataProtectionScope.CurrentUser).
/// The key is tied to the current Windows user account and machine.
/// MVP note: this protects against casual inspection but is not a full HSM solution.
/// </summary>
public class CredentialService
{
    private static readonly string KeyFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GrowLinkAgent", "secure.dat");

    private static readonly string RouteKeyFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GrowLinkAgent", "secure-routes.dat");

    private readonly LogService _log;

    public CredentialService(LogService log) => _log = log;

    public bool SaveKey(string key)
    {
        try
        {
            var plaintext = Encoding.UTF8.GetBytes(key);
            var ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
            File.WriteAllBytes(KeyFile, ciphertext);
            Array.Clear(plaintext, 0, plaintext.Length);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save upload key: {ex.Message}");
            return false;
        }
    }

    public virtual string? LoadKey()
    {
        try
        {
            if (!File.Exists(KeyFile))
                return null;

            var ciphertext = File.ReadAllBytes(KeyFile);
            var plaintext = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(plaintext);
            Array.Clear(plaintext, 0, plaintext.Length);
            return key;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load upload key: {ex.Message}");
            return null;
        }
    }

    public bool SaveRouteKey(string keySlot, string key)
    {
        if (string.IsNullOrWhiteSpace(keySlot))
            return false;

        try
        {
            var routeKeys = LoadRouteKeyMap();
            if (string.IsNullOrWhiteSpace(key))
                routeKeys.Remove(keySlot);
            else
                routeKeys[keySlot] = key;

            SaveRouteKeyMap(routeKeys);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save route key for slot '{keySlot}': {ex.Message}");
            return false;
        }
    }

    public bool RemoveRouteKey(string keySlot)
    {
        if (string.IsNullOrWhiteSpace(keySlot))
            return false;

        try
        {
            var routeKeys = LoadRouteKeyMap();
            if (!routeKeys.Remove(keySlot))
                return true;

            SaveRouteKeyMap(routeKeys);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to remove route key for slot '{keySlot}': {ex.Message}");
            return false;
        }
    }

    public virtual string? LoadRouteKey(string keySlot)
    {
        if (string.IsNullOrWhiteSpace(keySlot))
            return null;

        try
        {
            var routeKeys = LoadRouteKeyMap();
            return routeKeys.TryGetValue(keySlot, out var key) ? key : null;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load route key for slot '{keySlot}': {ex.Message}");
            return null;
        }
    }

    public bool HasAnyRouteKeyConfigured()
    {
        try
        {
            return LoadRouteKeyMap().Any(kv => !string.IsNullOrWhiteSpace(kv.Value));
        }
        catch
        {
            return false;
        }
    }

    private Dictionary<string, string> LoadRouteKeyMap()
    {
        if (!File.Exists(RouteKeyFile))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var ciphertext = File.ReadAllBytes(RouteKeyFile);
        var plaintext = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
        try
        {
            var json = Encoding.UTF8.GetString(plaintext);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Array.Clear(plaintext, 0, plaintext.Length);
        }
    }

    private static void SaveRouteKeyMap(Dictionary<string, string> routeKeys)
    {
        var json = JsonSerializer.Serialize(routeKeys);
        var plaintext = Encoding.UTF8.GetBytes(json);
        try
        {
            var ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(RouteKeyFile)!);
            File.WriteAllBytes(RouteKeyFile, ciphertext);
        }
        finally
        {
            Array.Clear(plaintext, 0, plaintext.Length);
        }
    }
}
