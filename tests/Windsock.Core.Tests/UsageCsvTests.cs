using System.Globalization;
using Windsock.Core.History;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class UsageCsvTests
{
    private static DateTimeOffset Noon { get; } = new DateTimeOffset(
        new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Local));

    private static string Export(UsageGranularity granularity, params UsageBucket[] buckets)
    {
        using StringWriter writer = new();
        UsageCsv.Write(writer, granularity, buckets);
        return writer.ToString();
    }

    private static UsageImport Import(string csv)
    {
        using StringReader reader = new(csv);
        return UsageCsv.Read(reader);
    }

    [Fact]
    public void WhatIsExported_CanBeImported()
    {
        string csv = Export(
            UsageGranularity.Minute,
            new UsageBucket(Noon, 4096, 512, 900, 90, 1),
            new UsageBucket(Noon.AddMinutes(1), 2048, 256, 400, 40, 1));

        UsageImport imported = Import(csv);

        Assert.Equal(0, imported.Skipped);
        Assert.Equal(2, imported.Minutes.Count);
        Assert.Equal(EpochMinute.From(Noon), imported.Minutes[0].Minute);
        Assert.Equal(4096, imported.Minutes[0].BytesDown);
        Assert.Equal(512, imported.Minutes[0].BytesUp);
        Assert.Equal(900, imported.Minutes[0].PeakDown);
        Assert.Equal(90, imported.Minutes[0].PeakUp);
        Assert.Equal(EpochMinute.From(Noon.AddMinutes(1)), imported.Minutes[1].Minute);
    }

    [Fact]
    public void TheFileStartsWithAHeader()
    {
        string csv = Export(UsageGranularity.Minute, new UsageBucket(Noon, 1, 2, 3, 4, 1));

        Assert.StartsWith(UsageCsv.Header, csv, StringComparison.Ordinal);
        Assert.Contains("bytes_down", UsageCsv.Header, StringComparison.Ordinal);
    }

    [Fact]
    public void NumbersAreWrittenTheSameWayInEveryLocale()
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("bn-BD");

            string csv = Export(
                UsageGranularity.Minute,
                new UsageBucket(Noon, 1234567, 890, 42, 7, 1));

            Assert.Contains("1234567", csv, StringComparison.Ordinal);
            Assert.Contains("2026-03-14T", csv, StringComparison.Ordinal);

            UsageImport imported = Import(csv);

            Assert.Equal(1234567, Assert.Single(imported.Minutes).BytesDown);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void RowsCoarserThanAMinute_AreNotImported()
    {
        string csv = Export(
            UsageGranularity.Hour,
            new UsageBucket(Noon, 4096, 512, 900, 90, 60));

        UsageImport imported = Import(csv);
        Assert.Empty(imported.Minutes);
        Assert.Equal(1, imported.Skipped);
    }

    [Fact]
    public void ColumnsAreFoundByName_NotByPosition()
    {
        string csv = """
            bytes_up,utc,bytes_down,granularity
            512,2026-03-14T09:00:00Z,4096,minute
            """;

        UsageMinute row = Assert.Single(Import(csv).Minutes);
        Assert.Equal(4096, row.BytesDown);
        Assert.Equal(512, row.BytesUp);
    }

    [Fact]
    public void QuotedFields_AreUnderstood()
    {
        string csv = """
            utc,bytes_down,bytes_up
            "2026-03-14T09:00:00Z","4096","512"
            """;

        Assert.Equal(4096, Assert.Single(Import(csv).Minutes).BytesDown);
    }

    [Fact]
    public void RowsThatCannotBeRead_AreCountedAndSkipped()
    {
        string csv = """
            utc,bytes_down,bytes_up
            2026-03-14T09:00:00Z,4096,512
            not-a-date,1,2
            2026-03-14T09:01:00Z,oops,2
            2026-03-14T09:02:00Z,7,8
            """;

        UsageImport imported = Import(csv);

        // One bad line should not cost the user the other thousand.
        Assert.Equal(2, imported.Minutes.Count);
        Assert.Equal(2, imported.Skipped);
    }

    [Fact]
    public void TheSameMinuteTwice_IsSummed()
    {
        string csv = """
            utc,bytes_down,bytes_up,peak_down_bytes_per_second,peak_up_bytes_per_second
            2026-03-14T09:00:00Z,100,10,500,50
            2026-03-14T09:00:00Z,200,20,300,80
            """;

        UsageMinute row = Assert.Single(Import(csv).Minutes);
        Assert.Equal(300, row.BytesDown);
        Assert.Equal(30, row.BytesUp);
        Assert.Equal(500, row.PeakDown);
        Assert.Equal(80, row.PeakUp);
    }

    [Fact]
    public void AByteOrderMark_DoesNotHideTheFirstColumn()
    {
        string csv = "﻿utc,bytes_down,bytes_up\n2026-03-14T09:00:00Z,4096,512\n";

        Assert.Equal(4096, Assert.Single(Import(csv).Minutes).BytesDown);
    }

    [Fact]
    public void AFileWithoutTheColumnsItNeeds_ImportsNothing()
    {
        Assert.Empty(Import("apples,pears\n1,2\n").Minutes);
        Assert.Empty(Import(string.Empty).Minutes);
    }
}
