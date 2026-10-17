using System.Net;
using System.Reflection;
using System.Text.Json;

namespace FlowMasterAgent.Services;

/// <summary>How an upload attempt ended, and therefore what happens to the file.</summary>
public enum UploadOutcome
{
    /// <summary>The server stored the file (created, duplicate, conflict or quarantined): move to Uploaded.</summary>
    Accepted,
    /// <summary>Network error, timeout, 408/425/429 or 5xx: keep the file and retry later.</summary>
    Transient,
    /// <summary>401/403: the key is missing, wrong, revoked or expired. Keep the file; it uploads once the key is fixed.</summary>
    Unauthorized,
    /// <summary>Any other 4xx: this file can't be accepted as sent. Move to Failed (recoverable with "Retry failed files").</summary>
    Rejected,
}

/// <summary>
/// The Block Summary upload request and the reading of its response. Independent of
/// Windows Forms and DPAPI so it can be tested anywhere.
/// </summary>
public static class ClimateUpload
{
    public static string AgentVersion { get; } =
        typeof(ClimateUpload).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(ClimateUpload).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    /// <summary>
    /// The JSON body: the original file's bytes (base64) and the agent version, plus the
    /// existing fields (filename, hash of those same bytes, the agent's parsed readings).
    /// </summary>
    public static string BuildBody(byte[] rawBytes, string filename, BlockSummaryCsvParser.ParseResult parsed)
    {
        var body = new
        {
            file_hash = BlockSummaryCsvParser.HashTag(rawBytes),
            filename,
            raw_csv_base64 = Convert.ToBase64String(rawBytes),
            agent_version = AgentVersion,
            readings = parsed.Readings.Select(r => new
            {
                zone_label = r.ZoneLabel,
                measured_at = r.MeasuredAt,
                metric_name = r.MetricName,
                value = r.Value,
                unit = r.Unit,
            }).ToArray(),
        };
        return JsonSerializer.Serialize(body);
    }

    public static UploadOutcome Classify(HttpStatusCode status)
    {
        var code = (int)status;
        if (code >= 200 && code < 300) return UploadOutcome.Accepted;
        if (code is 408 or 425 or 429 || code >= 500) return UploadOutcome.Transient;
        if (code is 401 or 403) return UploadOutcome.Unauthorized;
        return UploadOutcome.Rejected;
    }

    /// <summary>Exceptions from sending the request that mean "try again later".</summary>
    public static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException;

    /// <summary>The server's Retry-After (seconds or a date), capped; null when absent.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, TimeSpan cap)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        TimeSpan? wait = ra.Delta ?? (ra.Date.HasValue ? ra.Date.Value - DateTimeOffset.UtcNow : null);
        if (wait is null) return null;
        if (wait < TimeSpan.Zero) return TimeSpan.Zero;
        return wait > cap ? cap : wait;
    }

    /// <summary>"created" / "duplicate" / "conflict" / "quarantined" and the import id from the server's answer.</summary>
    public static (string Label, string? ImportId) ReadAcceptance(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var importId = root.TryGetProperty("import_id", out var i) ? i.GetString() : null;
            var label = status?.ToLowerInvariant() switch
            {
                "duplicate" => "Duplicate (already stored)",
                "conflict" => "Stored — conflict, review in Plants",
                "quarantined" => "Stored — quarantined, review in Plants",
                _ => "Created",
            };
            return (label, importId);
        }
        catch
        {
            return ("Created", null);
        }
    }
}
