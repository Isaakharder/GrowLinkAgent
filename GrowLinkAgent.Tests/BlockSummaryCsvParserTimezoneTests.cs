using FlowMasterAgent.Services;

namespace GrowLinkAgent.Tests;

public class BlockSummaryCsvParserTimezoneTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    // Minimal 2-zone wide-format CSV matching the real Synopta export shape closely
    // enough for BlockSummaryCsvParser: a zone header row, one metric row, and the
    // System date/time rows the timestamp is derived from.
    private string WriteTempCsv(string systemDate, string systemTime)
    {
        var content =
            "\"\",\"Zone 1\",\"Zone 2\"\n" +
            "\"Air temperature [°C]\",\"20.0\",\"21.0\"\n" +
            $"\"System time [hh:mm]\",\"{systemTime}\",\"\"\n" +
            $"\"System date [dd/mm/yyyy]\",\"{systemDate}\",\"\"\n";

        var path = Path.Combine(Path.GetTempPath(), $"blocksummary-tz-test-{Guid.NewGuid()}.csv");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SummerDate_ConvertsFromEdtToUtc()
    {
        // July 4, 2026 14:00 America/Toronto is EDT (UTC-4) -> 18:00 UTC.
        var path = WriteTempCsv("7/4/2026", "Real time - 14:00");

        var result = new BlockSummaryCsvParser().Parse(path);

        var reading = Assert.Single(result.Readings, r => r.ZoneLabel == "Zone 1" && r.MetricName == "temperature_c");
        Assert.Equal("2026-07-04T18:00:00Z", reading.MeasuredAt);
    }

    [Fact]
    public void WinterDate_ConvertsFromEstToUtc()
    {
        // January 15, 2026 14:00 America/Toronto is EST (UTC-5) -> 19:00 UTC.
        var path = WriteTempCsv("1/15/2026", "Real time - 14:00");

        var result = new BlockSummaryCsvParser().Parse(path);

        var reading = Assert.Single(result.Readings, r => r.ZoneLabel == "Zone 1" && r.MetricName == "temperature_c");
        Assert.Equal("2026-01-15T19:00:00Z", reading.MeasuredAt);
    }

    [Fact]
    public void MissingTime_FallsBackToLocalMidnight_StillConvertedToUtc()
    {
        // "--:--" means the climate computer had no time value; the parser falls back
        // to date-only (local midnight), which must still go through the same
        // local->UTC conversion rather than being left as a naive UTC-looking string.
        var path = WriteTempCsv("7/4/2026", "Real time - --:--");

        var result = new BlockSummaryCsvParser().Parse(path);

        var reading = Assert.Single(result.Readings, r => r.ZoneLabel == "Zone 1" && r.MetricName == "temperature_c");
        // Local midnight EDT (UTC-4) -> 04:00 UTC.
        Assert.Equal("2026-07-04T04:00:00Z", reading.MeasuredAt);
    }

    [Fact]
    public void InvalidDate_ThrowsInvalidDataException()
    {
        var path = WriteTempCsv("not-a-date", "Real time - 14:00");

        var ex = Assert.Throws<InvalidDataException>(() => new BlockSummaryCsvParser().Parse(path));
        Assert.Contains("unparseable timestamp", ex.Message);
    }

    public static IEnumerable<object[]> SampleFiles => new[]
    {
        new object[] { "Block Summary_20260717_080003.csv", "2026-07-17T12:00:00Z" }, // 08:00 EDT -> 12:00 UTC
        new object[] { "Block Summary_20260717_090003.csv", "2026-07-17T13:00:00Z" }, // 09:00 EDT -> 13:00 UTC
        new object[] { "Block Summary_20260717_100005.csv", "2026-07-17T14:00:00Z" }, // 10:00 EDT -> 14:00 UTC
        new object[] { "Block Summary_20260717_110006.csv", "2026-07-17T15:00:00Z" }, // 11:00 EDT -> 15:00 UTC
    };

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void RealSampleFiles_ProduceCorrectedUtcTimestamp(string filename, string expectedUtc)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SampleFiles", filename);
        Assert.True(File.Exists(path), $"Sample fixture not found: {path}");

        var result = new BlockSummaryCsvParser().Parse(path);

        Assert.Equal(114, result.Readings.Count);
        Assert.All(result.Readings, r => Assert.Equal(expectedUtc, r.MeasuredAt));
    }
}
