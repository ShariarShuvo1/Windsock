using Windsock.Core.Connectivity;
using Windsock.Core.Networking;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>Jitter, loss and the verdicts drawn from them, and the quarter-hour sums kept for reports.</summary>
public sealed class ConnectionQualityTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MeanAbsoluteDifference_IsTheMeanOfConsecutiveGaps()
    {
        Assert.Equal(20 / 3d, SteadinessMath.MeanAbsoluteDifference([10, 20, 15, 10])!.Value, 9);
        Assert.Null(SteadinessMath.MeanAbsoluteDifference([10]));
    }

    [Fact]
    public void Smoothed_FollowsRfc3550sGainOfASixteenth()
    {
        // One step of 16 ms from rest: J = 0 + (16 - 0) / 16 = 1.
        Assert.Equal(1, SteadinessMath.Smoothed([0, 16]));

        // A second step of 16: J = 1 + (16 - 1) / 16.
        Assert.Equal(1 + (15 / 16d), SteadinessMath.Smoothed([0, 16, 0])!.Value, 9);
    }

    [Fact]
    public void Steadiness_CountsLossAndSkipsItForJitter()
    {
        List<LatencyPoint> points = [];

        for (int each = 0; each < 25; each++)
        {
            double? internet = each % 5 == 4 ? null : 20 + (each % 2 * 10);
            points.Add(new LatencyPoint(Start.AddSeconds(each * 5), 1, internet, RouterTried: true, InternetTried: true, Offline: false));
        }

        Steadiness internetFigures = SteadinessMath.Of(points, CheckTarget.Internet);
        Steadiness routerFigures = SteadinessMath.Of(points, CheckTarget.Router);

        Assert.Equal(25, internetFigures.Sent);
        Assert.Equal(20, internetFigures.Answered);
        Assert.Equal(0.2, internetFigures.Loss!.Value, 9);
        Assert.NotNull(internetFigures.JitterMs);
        Assert.Equal(0, routerFigures.Loss);
        Assert.Equal(0, routerFigures.JitterMs);
    }

    [Fact]
    public void Steadiness_FewChecks_GivesNoLossOrJitterYet()
    {
        LatencyPoint[] points = [.. Enumerable.Range(0, 5).Select(each => new LatencyPoint(Start.AddSeconds(each), 1, 30, true, true, false))];

        Steadiness figures = SteadinessMath.Of(points, CheckTarget.Internet);

        Assert.Null(figures.Loss);
        Assert.Null(figures.JitterMs);
        Assert.Equal(30, figures.MedianMs);
    }

    [Fact]
    public void Steadiness_ChecksDuringAnOutage_AreLeftOut()
    {
        LatencyPoint[] points = [.. Enumerable.Range(0, 30).Select(each => new LatencyPoint(Start.AddSeconds(each), null, null, true, true, Offline: each < 10))];

        Assert.Equal(20, SteadinessMath.Of(points, CheckTarget.Internet).Sent);
    }

    [Theory]
    [InlineData(QualityUse.Calls, QualityMeasure.ResponseTime, 99, Verdict.Good)]
    [InlineData(QualityUse.Calls, QualityMeasure.ResponseTime, 100, Verdict.Fair)]
    [InlineData(QualityUse.Calls, QualityMeasure.ResponseTime, 300, Verdict.Poor)]
    [InlineData(QualityUse.Calls, QualityMeasure.Jitter, 25, Verdict.Fair)]
    [InlineData(QualityUse.Calls, QualityMeasure.Jitter, 30, Verdict.Poor)]
    [InlineData(QualityUse.Calls, QualityMeasure.Loss, 0.005, Verdict.Good)]
    [InlineData(QualityUse.Calls, QualityMeasure.Loss, 0.05, Verdict.Fair)]
    [InlineData(QualityUse.Calls, QualityMeasure.Loss, 0.10, Verdict.Poor)]
    [InlineData(QualityUse.Games, QualityMeasure.ResponseTime, 49, Verdict.Good)]
    [InlineData(QualityUse.Games, QualityMeasure.ResponseTime, 60, Verdict.Fair)]
    [InlineData(QualityUse.Games, QualityMeasure.ResponseTime, 100, Verdict.Poor)]
    [InlineData(QualityUse.Games, QualityMeasure.Loss, 0.05, Verdict.Poor)]
    [InlineData(QualityUse.Games, QualityMeasure.LatencyUnderLoad, 20, Verdict.Good)]
    [InlineData(QualityUse.Games, QualityMeasure.LatencyUnderLoad, 150, Verdict.Poor)]
    public void Lines_FallWhereTheSourcesPutThem(QualityUse use, QualityMeasure measure, double value, Verdict verdict)
    {
        Assert.Equal(verdict, QualityGrader.GradeOne(use, measure, value).Verdict);
    }

    [Fact]
    public void Grade_IsTheWorstFigure_AndNamesIt()
    {
        Steadiness figures = new(CheckTarget.Internet, 100, 97, 40, 90, 24, 20);

        QualityGrade calls = QualityGrader.Grade(QualityUse.Calls, figures);

        Assert.Equal(Verdict.Fair, calls.Verdict);
        Assert.Equal(QualityMeasure.Loss, calls.Cost!.Measure);
        Assert.False(calls.HomeNetworkOnly);
        Assert.Equal("Fair for calls: 3% loss", calls.Describe());
    }

    [Fact]
    public void Grade_RouterOnly_SaysSo()
    {
        Steadiness figures = new(CheckTarget.Router, 100, 100, 2, 5, 1, 1);

        QualityGrade games = QualityGrader.Grade(QualityUse.Games, figures);

        Assert.Equal(Verdict.Good, games.Verdict);
        Assert.True(games.HomeNetworkOnly);
        Assert.Null(games.Cost);
        Assert.Equal("Good for games (home network only)", games.Describe());
    }

    [Fact]
    public void Grade_TakesTheSpeedTestsLoadedLatency()
    {
        Steadiness figures = new(CheckTarget.Internet, 100, 100, 20, 30, 3, 3);

        QualityGrade games = QualityGrader.Grade(QualityUse.Games, figures, loadedIncreaseMs: 240);

        Assert.Equal(Verdict.Poor, games.Verdict);
        Assert.Equal(QualityMeasure.LatencyUnderLoad, games.Cost!.Measure);
    }

    [Fact]
    public void Grade_OnResponseTimeAlone_IsUnknown_UnlessAlreadyPoor()
    {
        Steadiness quick = new(CheckTarget.Router, 12, 12, 3, 4, null, null);
        Steadiness slow = new(CheckTarget.Internet, 12, 12, 400, 420, null, null);

        Assert.Equal(Verdict.Unknown, QualityGrader.Grade(QualityUse.Calls, quick).Verdict);
        Assert.Equal(Verdict.Unknown, QualityGrader.Grade(QualityUse.Games, quick, loadedIncreaseMs: 5).Verdict);
        Assert.Equal(Verdict.Poor, QualityGrader.Grade(QualityUse.Calls, slow).Verdict);
    }

    [Fact]
    public void Grade_WaitsForAFewAnsweredChecks()
    {
        Steadiness onePing = new(CheckTarget.Internet, 1, 1, 400, 400, null, null);
        Steadiness none = new(CheckTarget.Internet, 0, 0, null, null, null, null);

        Assert.Equal(Verdict.Unknown, QualityGrader.Grade(QualityUse.Calls, onePing).Verdict);
        Assert.Equal(Verdict.Unknown, QualityGrader.Grade(QualityUse.Games, none, loadedIncreaseMs: 240).Verdict);
    }

    [Fact]
    public void Grade_WithNothing_IsUnknown()
    {
        Assert.Equal(Verdict.Unknown, QualityGrader.Grade(QualityUse.Calls, new Steadiness(CheckTarget.Internet, 0, 0, null, null, null, null)).Verdict);
    }

    [Fact]
    public void Tally_SumsByQuarterAndCountsConsecutiveAnswersForJitter()
    {
        QualityTally tally = new();
        TimeSpan gap = TimeSpan.FromSeconds(30);

        tally.Add(Sample(Start, router: 2, internet: 20), offline: false, gap);
        tally.Add(Sample(Start.AddSeconds(5), router: 4, internet: 30), offline: false, gap);
        tally.Add(Sample(Start.AddSeconds(10), router: 3, internet: null), offline: false, gap);
        tally.Add(Sample(Start.AddSeconds(15), router: 3, internet: 25), offline: false, gap);

        // Across the quarter boundary, and after a long gap: counted, but no jitter pair.
        tally.Add(Sample(Start.AddMinutes(16), router: 9, internet: 40), offline: false, gap);

        IReadOnlyList<QualityQuarter> quarters = tally.Take();
        QualityQuarter internet = quarters.Single(each => each.Target == CheckTarget.Internet && each.Start == Start);
        QualityQuarter router = quarters.Single(each => each.Target == CheckTarget.Router && each.Start == Start);
        QualityQuarter later = quarters.Single(each => each.Target == CheckTarget.Internet && each.Start == Start.AddMinutes(15));

        Assert.Equal(4, internet.Sent);
        Assert.Equal(3, internet.Answered);
        Assert.Equal(75, internet.SumMs, 9);
        Assert.Equal(30, internet.WorstMs, 9);
        Assert.Equal(15, internet.JitterSumMs, 9);   // |30 - 20| and |25 - 30|, the lost check skipped
        Assert.Equal(2, internet.JitterPairs);
        Assert.Equal(3, router.JitterPairs);
        Assert.Equal(0, later.JitterPairs);
        Assert.Empty(tally.Take());
    }

    [Fact]
    public void Tally_IgnoresChecksDuringAnOutage()
    {
        QualityTally tally = new();
        tally.Add(Sample(Start, null, null), offline: true, TimeSpan.FromSeconds(30));

        Assert.Empty(tally.Take());
    }

    [Fact]
    public void Summary_AddsQuartersUp()
    {
        QualitySummary summary = QualitySummary.Of(
        [
            new QualityQuarter(Start, CheckTarget.Internet, 180, 170, 3400, 90, 500, 160),
            new QualityQuarter(Start.AddMinutes(15), CheckTarget.Internet, 20, 10, 300, 120, 60, 9),
        ]);

        Assert.Equal(200, summary.Sent);
        Assert.Equal(180, summary.Answered);
        Assert.Equal(0.1, summary.Loss!.Value, 9);
        Assert.Equal(3700 / 180d, summary.MeanMs!.Value, 9);
        Assert.Equal(120, summary.WorstMs);
        Assert.Equal(560 / 169d, summary.JitterMs!.Value, 9);
        Assert.Equal(QualitySummary.Empty, QualitySummary.Of([]));
    }

    [Fact]
    public void Quarters_AreUtcQuarterHours()
    {
        DateTimeOffset local = new(2026, 10, 5, 15, 44, 59, TimeSpan.FromHours(6));

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.Zero), QualityQuarter.QuarterOf(local));
    }

    [Fact]
    public void Catalogue_StartsWithEverythingOff_AndMLabCannotBeSwitchedOnFromAList()
    {
        Settings.WindsockSettings settings = new();

        Assert.All(OutsideContactCatalogue.All, contact => Assert.False(contact.IsAllowed(settings)));
        Assert.All(OutsideContactCatalogue.All, contact => Assert.False(string.IsNullOrWhiteSpace(contact.Description)));
        Assert.Equal(OutsideContactCatalogue.All.Count, OutsideContactCatalogue.All.Select(each => each.Key).Distinct().Count());

        Assert.False(OutsideContactCatalogue.MeasurementLab.CanAllowHere);
        Assert.False(OutsideContactCatalogue.LibreSpeed.CanAllowHere);
        Assert.True(OutsideContactCatalogue.PublicAddress.CanAllowHere);
        OutsideContactCatalogue.MeasurementLab.Allow(settings, true);
        Assert.False(OutsideContactCatalogue.MeasurementLab.IsAllowed(settings));

        settings.ConnectionTools.MeasurementLabConsentAt = Start;
        settings.ConnectionTools.ScheduledSpeedTests = true;
        OutsideContactCatalogue.MeasurementLab.Allow(settings, false);
        Assert.False(settings.ConnectionTools.MeasurementLabAgreed);
        Assert.False(settings.ConnectionTools.ScheduledSpeedTests);

        OutsideContactCatalogue.PublicAddress.Allow(settings, true);
        Assert.True(settings.ConnectionTools.PublicAddressLookups);
        Assert.Equal("api.ipify.org and api6.ipify.org", OutsideContactCatalogue.PublicAddress.Destination(settings));
    }

    [Fact]
    public void ImportedSettings_KeepThisMachinesMLabAgreement()
    {
        Settings.WindsockSettings here = new();
        here.ConnectionTools.MeasurementLabConsentAt = Start;

        Settings.WindsockSettings imported = new();
        imported.ConnectionTools.PublicAddressLookups = true;
        imported.ConnectionTools.ScheduledSpeedTests = true;
        imported.ConnectionTools.PlanDownloadMbps = 100;

        here.CopyFrom(imported);

        // Looking the public address up is a contact from this PC: never switched on by a file.
        Assert.False(here.ConnectionTools.PublicAddressLookups);
        Assert.Equal(100, here.ConnectionTools.PlanDownloadMbps);
        Assert.Equal(Start, here.ConnectionTools.MeasurementLabConsentAt);
        Assert.False(here.ConnectionTools.ScheduledSpeedTests);
    }

    private static ConnectionSample Sample(DateTimeOffset at, double? router, double? internet) => new(
        at,
        new ConnectionFacts(WindowsReach.Internet, new NetworkIdentity("Home", NetworkKind.WiFi), "192.168.0.1", false),
        router is { } r ? TimeSpan.FromMilliseconds(r) : null,
        internet is { } i ? TimeSpan.FromMilliseconds(i) : null,
        InternetChecked: true);
}
