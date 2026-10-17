using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FlowMasterAgent.Services;

public sealed class BlockSummaryCsvParser
{
    private sealed record ColumnMapping(string MetricName, string Unit);

    public sealed record ClimateReading(
        string ZoneLabel,
        string MeasuredAt,
        string MetricName,
        double Value,
        string Unit);

    public sealed record ParseResult(
        string FileHash,
        string Filename,
        IReadOnlyList<ClimateReading> Readings);

    /// <summary>"sha256-" + lowercase hex of the bytes (the form the servers expect).</summary>
    public static string HashTag(byte[] bytes) => "sha256-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static readonly Regex ZoneColumnPattern = new(@"^zone\s*\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ParseResult Parse(string filePath) => ParseBytes(File.ReadAllBytes(filePath), Path.GetFileName(filePath));

    /// <summary>
    /// Parses an export already read into memory, so the bytes that are hashed,
    /// parsed and uploaded are the very same bytes.
    /// </summary>
    public ParseResult ParseBytes(byte[] rawBytes, string fileName)
    {
        var fileHash = HashTag(rawBytes);

        // Same decoding as File.ReadAllLines(path, Encoding.UTF8): a byte-order mark is
        // honoured and dropped, invalid bytes (e.g. Windows-1252 "°") become U+FFFD.
        string text;
        using (var reader = new StreamReader(new MemoryStream(rawBytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();
        var lines = text.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count < 2)
            throw new InvalidDataException($"Block summary CSV '{fileName}' has no data rows.");

        var splitLines = lines.Select(SplitCsv).ToList();
        var zoneHeaderRowIdx = FindZoneHeaderRow(splitLines);
        if (zoneHeaderRowIdx >= 0)
        {
            var wideReadings = ParseWideFormat(splitLines, zoneHeaderRowIdx, fileName);
            return new ParseResult(fileHash, fileName, wideReadings);
        }

        var headers = splitLines[0];

        // Detect timestamp columns
        int dateTimeColIdx = -1, dateColIdx = -1, timeColIdx = -1;
        for (int i = 0; i < headers.Count; i++)
        {
            var h = headers[i].Trim().ToLowerInvariant();
            if (h is "datetime" or "date/time" or "date time" or "timestamp")
            {
                dateTimeColIdx = i;
                break;
            }
        }
        if (dateTimeColIdx < 0)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                var h = headers[i].Trim().ToLowerInvariant();
                if (h.Contains("date") && !h.Contains("update") && dateColIdx < 0)
                    dateColIdx = i;
                else if (h.Contains("time") && timeColIdx < 0)
                    timeColIdx = i;
            }
        }

        if (dateTimeColIdx < 0 && dateColIdx < 0)
            throw new InvalidDataException(
                $"Block summary CSV '{fileName}': no date/time column found. " +
                "Expected a column named 'Date', 'Time', 'DateTime', or 'Timestamp'.");

        // Detect zone/block column
        int zoneColIdx = -1;
        for (int i = 0; i < headers.Count; i++)
        {
            var h = headers[i].Trim().ToLowerInvariant();
            if (h.Contains("zone") || h.Contains("block"))
            {
                zoneColIdx = i;
                break;
            }
        }
        if (zoneColIdx < 0)
            throw new InvalidDataException(
                $"Block summary CSV '{fileName}': no zone/block column found. " +
                "Expected a column header containing 'Zone' or 'Block'.");

        // Detect metric columns (first match per metric wins)
        var metricColumns = new List<(int Index, ColumnMapping Mapping)>();
        var assignedMetrics = new HashSet<string>();
        for (int i = 0; i < headers.Count; i++)
        {
            if (i == dateTimeColIdx || i == dateColIdx || i == timeColIdx || i == zoneColIdx)
                continue;
            var mapping = DetectMetric(headers[i]);
            if (mapping != null && assignedMetrics.Add(mapping.MetricName))
                metricColumns.Add((i, mapping));
        }

        // Parse data rows
        var readings = new List<ClimateReading>();
        for (int rowIdx = 1; rowIdx < splitLines.Count; rowIdx++)
        {
            var fields = splitLines[rowIdx];

            var zone = GetField(fields, zoneColIdx)?.Trim();
            if (string.IsNullOrEmpty(zone))
                throw new InvalidDataException(
                    $"Block summary CSV '{fileName}': row {rowIdx + 1} has an empty zone/block label.");

            string measuredAt;
            if (dateTimeColIdx >= 0)
            {
                var raw = GetField(fields, dateTimeColIdx)?.Trim();
                if (string.IsNullOrEmpty(raw))
                    throw new InvalidDataException(
                        $"Block summary CSV '{fileName}': row {rowIdx + 1} has an empty timestamp.");
                measuredAt = ParseTimestamp(raw, fileName, rowIdx + 1);
            }
            else
            {
                var dateRaw = GetField(fields, dateColIdx)?.Trim();
                if (string.IsNullOrEmpty(dateRaw))
                    throw new InvalidDataException(
                        $"Block summary CSV '{fileName}': row {rowIdx + 1} has an empty date.");
                var timeRaw = timeColIdx >= 0 ? GetField(fields, timeColIdx)?.Trim() : null;
                var combined = string.IsNullOrEmpty(timeRaw) ? dateRaw : $"{dateRaw} {timeRaw}";
                measuredAt = ParseTimestamp(combined, fileName, rowIdx + 1);
            }

            foreach (var (colIdx, mapping) in metricColumns)
            {
                var raw = GetField(fields, colIdx)?.Trim();
                if (string.IsNullOrEmpty(raw)) continue;
                if (!double.TryParse(raw, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                    continue;
                readings.Add(new ClimateReading(zone, measuredAt, mapping.MetricName, value, mapping.Unit));
            }
        }

        return new ParseResult(fileHash, fileName, readings);
    }

    private static int FindZoneHeaderRow(IReadOnlyList<List<string>> splitLines)
    {
        var bestIdx = -1;
        var bestCount = 0;
        for (int i = 0; i < splitLines.Count; i++)
        {
            var count = splitLines[i].Count(f => ZoneColumnPattern.IsMatch(f.Trim()));
            if (count >= 2 && count > bestCount)
            {
                bestCount = count;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    // Handles Synopta/Priva-style "Block Summary" exports: metrics run down the rows
    // (one label per row, e.g. "Air temperature [°C]") and zones run across the columns
    // (e.g. "Zone 1".."Zone 22"), with the reading timestamp embedded as data in
    // "System date" / "System time" rows rather than as its own column.
    private static List<ClimateReading> ParseWideFormat(
        IReadOnlyList<List<string>> splitLines, int zoneHeaderRowIdx, string fileName)
    {
        var zoneHeader = splitLines[zoneHeaderRowIdx];
        var zoneColumns = new List<(int Index, string Label)>();
        for (int i = 0; i < zoneHeader.Count; i++)
        {
            var label = zoneHeader[i].Trim();
            if (ZoneColumnPattern.IsMatch(label))
                zoneColumns.Add((i, label));
        }

        if (zoneColumns.Count == 0)
            throw new InvalidDataException(
                $"Block summary CSV '{fileName}': zone header row detected but no zone columns found.");

        string? dateValue = null;
        string? timeValue = null;
        foreach (var fields in splitLines)
        {
            var label = (GetField(fields, 0) ?? string.Empty).Trim().ToLowerInvariant();
            if (label.Contains("system date"))
                dateValue = GetField(fields, 1)?.Trim();
            else if (label.Contains("system time"))
                timeValue = GetField(fields, 1)?.Trim();
        }

        if (string.IsNullOrEmpty(dateValue))
            throw new InvalidDataException(
                $"Block summary CSV '{fileName}': no 'System date' row found to determine reading timestamp.");

        var cleanedTime = CleanRealTimeValue(timeValue);
        var combined = string.IsNullOrEmpty(cleanedTime) ? dateValue : $"{dateValue} {cleanedTime}";
        var measuredAt = ParseTimestamp(combined, fileName, zoneHeaderRowIdx + 1);

        var readings = new List<ClimateReading>();
        foreach (var fields in splitLines)
        {
            var label = GetField(fields, 0)?.Trim();
            if (string.IsNullOrEmpty(label))
                continue;

            var mapping = DetectMetric(label);
            if (mapping is null)
                continue;

            foreach (var (colIdx, zoneLabel) in zoneColumns)
            {
                var raw = GetField(fields, colIdx)?.Trim();
                if (string.IsNullOrEmpty(raw)) continue;
                if (!double.TryParse(raw, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                    continue;

                readings.Add(new ClimateReading(zoneLabel, measuredAt, mapping.MetricName, value, mapping.Unit));
            }
        }

        return readings;
    }

    // "System time [hh:mm]" rows are formatted like "Real time - 08:00", or
    // "Real time - --:--" when the value is unavailable.
    private static string? CleanRealTimeValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var dashIdx = raw.IndexOf('-');
        var value = dashIdx >= 0 ? raw[(dashIdx + 1)..].Trim() : raw.Trim();
        return value.Contains("--") ? null : value;
    }

    // The climate computer's "System date"/"System time" values are greenhouse-local wall-clock
    // time, not UTC. CropLink's other ingestion path (the manual CSV importer) converts using
    // America/Toronto's real DST rules, so this must too — a fixed +/-4h offset would be wrong
    // for half the year and would silently misalign every reading against that path.
    public static readonly TimeZoneInfo GreenhouseTimeZone = ResolveGreenhouseTimeZone();

    private static TimeZoneInfo ResolveGreenhouseTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Toronto"); }
        catch (TimeZoneNotFoundException)
        {
            // Windows-native ID fallback for environments without IANA tzdb/ICU data.
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    private static string ParseTimestamp(string raw, string fileName, int rowNumber)
    {
        DateTime dt;
        var parsed = DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out dt);

        if (!parsed)
        {
            var formats = new[]
            {
                "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
                "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy",
                "MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy HH:mm", "MM/dd/yyyy",
                "d/M/yyyy H:mm:ss",    "d/M/yyyy H:mm",    "d/M/yyyy",
            };
            foreach (var fmt in formats)
            {
                if (DateTime.TryParseExact(raw, fmt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out dt))
                {
                    parsed = true;
                    break;
                }
            }
        }

        if (!parsed)
            throw new InvalidDataException(
                $"Block summary CSV '{fileName}': row {rowNumber} has unparseable timestamp '{raw}'.");

        try
        {
            var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, GreenhouseTimeZone);
            return utc.ToString("yyyy-MM-ddTHH:mm:ss'Z'");
        }
        catch (ArgumentException)
        {
            // Only thrown for the ~1-hour "spring forward" gap that never occurred in local time.
            throw new InvalidDataException(
                $"Block summary CSV '{fileName}': row {rowNumber} has timestamp '{raw}' which falls in the " +
                $"{GreenhouseTimeZone.Id} spring-forward DST gap and does not correspond to a real local time.");
        }
    }

    private static ColumnMapping? DetectMetric(string header)
    {
        var h = header.ToLowerInvariant();

        if (h.Contains("drain"))                                               return new("drain_water_pct",       "%");
        if (h.Contains("feed water") || h.Contains("feed vol"))                return new("feed_water_volume_ml",  "ml");
        if (h.Contains("radiation"))                                           return new("radiation_sum_j_cm2",   "J/cm²");
        if (h.Contains("relative humidity"))                                   return new("relative_humidity_pct", "%");
        if (IsWord(h, "rh"))                                                   return new("relative_humidity_pct", "%");
        if (h.Contains("temperature") || h.Contains("temp"))                  return new("temperature_c",         "°C");
        if (h.Contains("co2") || h.Contains("carbon dioxide"))                return new("co2_ppm",               "ppm");
        if (IsWord(h, "ec") || h.Contains("electrical conductivity"))         return new("ec",                    "mS/cm");
        if (IsWord(h, "ph") || h.Contains("potential hydrogen"))              return new("ph",                    "pH");

        return null;
    }

    private static bool IsWord(string text, string word)
    {
        int idx = 0;
        while (true)
        {
            idx = text.IndexOf(word, idx, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            bool before = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            bool after  = idx + word.Length >= text.Length || !char.IsLetterOrDigit(text[idx + word.Length]);
            if (before && after) return true;
            idx += word.Length;
        }
    }

    private static string? GetField(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count ? fields[index] : null;

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        foreach (var ch in line)
        {
            if (ch == '"')
                inQuotes = !inQuotes;
            else if (ch == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
                sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }
}
