using Windsock.Core.Formatting;
using Windsock.Core.Hardware;
using Windsock.Core.Meter;
using Windsock.Core.Power;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The readings step 3 added to the meter, the tray's figures, the windsock's
/// pace and the graphs' minute.
/// </summary>
public sealed class MeterExtrasTests
{
    private static MeterReading With(MeterExtras extras) => new(0, 0, SystemSnapshot.Empty) { Extras = extras };

    private static MeterLine Line(MeterField field, MeterReading reading, MeterSlot? slot = null) =>
        MeterReadout.Compose(slot ?? new MeterSlot { Field = field }, field, reading, new TaskbarSettings());

    [Theory]
    [InlineData(3.4, "3", "ms")]
    [InlineData(0.4, "<1", "ms")]
    [InlineData(118, "118", "ms")]
    public void TheRoutersResponseTime_IsInMilliseconds(double taken, string value, string unit)
    {
        MeterLine line = Line(MeterField.RouterPing, With(new MeterExtras { RouterMilliseconds = taken }));

        Assert.Equal(value, line.Value);
        Assert.Equal(unit, line.Unit);
    }

    /// <summary>
    /// Nothing public is pinged unless the person chose an address, so the
    /// internet's response time is a dash until they do.
    /// </summary>
    [Fact]
    public void TheInternetsResponseTime_IsADashUnlessAnAddressIsPinged()
    {
        Assert.Equal(MeterReadout.Unknown, Line(MeterField.InternetPing, With(new MeterExtras { InternetMilliseconds = 20 })).Value);
        Assert.Equal("20", Line(MeterField.InternetPing, With(new MeterExtras { InternetMilliseconds = 20, InternetChecked = true })).Value);
        Assert.Equal(MeterReadout.Unknown, Line(MeterField.RouterPing, With(MeterExtras.None)).Value);
    }

    [Fact]
    public void TodaysAndThisMonthsTotals_AreSizes()
    {
        MeterReading reading = With(new MeterExtras { TodayBytes = 3L * 1024 * 1024 * 1024, MonthBytes = 512L * 1024 * 1024 });

        Assert.Equal(("3.00", "GB"), (Line(MeterField.TodayTotal, reading).Value, Line(MeterField.TodayTotal, reading).Unit));
        Assert.Equal(("512", "MB"), (Line(MeterField.MonthTotal, reading).Value, Line(MeterField.MonthTotal, reading).Unit));
        Assert.Equal(MeterReadout.Unknown, Line(MeterField.TodayTotal, With(MeterExtras.None)).Value);
    }

    [Fact]
    public void ThePublicAddress_CarriesItsCountry()
    {
        MeterLine line = Line(MeterField.PublicAddress, With(new MeterExtras { PublicAddress = "203.0.113.7", PublicCountry = "BD" }));

        Assert.Equal("203.0.113.7", line.Value);
        Assert.Equal("BD", line.Unit);
        Assert.Equal(MeterReadout.Unknown, Line(MeterField.PublicAddress, With(MeterExtras.None)).Value);
    }

    [Fact]
    public void ThePlan_IsWhatIsLeftAndForHowLong()
    {
        MeterLine line = Line(MeterField.Plan, With(new MeterExtras { PlanLeft = "41 GB left · 17 days" }));

        Assert.Equal("41 GB left", line.Value);
        Assert.Equal("17 days", line.Unit);
    }

    /// <summary>
    /// No plan covering the network in use is a dash, never an empty place;
    /// a plan four fifths used goes amber and one used up goes red.
    /// </summary>
    [Fact]
    public void ThePlan_IsADashWithoutOne_AndColouredByWhatIsUsed()
    {
        MeterSlot slot = new() { Field = MeterField.Plan };
        MeterReading none = With(MeterExtras.None);
        MeterReading most = With(new MeterExtras { PlanLeft = "4 GB left · 3 days", PlanUsedPercent = 86 });
        MeterReading over = With(new MeterExtras { PlanLeft = "Over by 2 GB · 3 days", PlanUsedPercent = 104 });
        MeterReading early = With(new MeterExtras { PlanLeft = "41 GB left · 17 days", PlanUsedPercent = 18 });

        Assert.Equal(MeterReadout.Unknown, Line(MeterField.Plan, none).Value);
        Assert.Null(MeterReadout.Number(MeterField.Plan, slot, none));

        IReadOnlyList<MeterLevel> levels = MeterLevels.For(slot, MeterField.Plan);

        Assert.Null(MeterLevels.Colour(levels, MeterReadout.Number(MeterField.Plan, slot, early)));
        Assert.Equal(MeterLevels.Fair, MeterLevels.Colour(levels, MeterReadout.Number(MeterField.Plan, slot, most)));
        Assert.Equal(MeterLevels.Slow, MeterLevels.Colour(levels, MeterReadout.Number(MeterField.Plan, slot, over)));
    }

    [Fact]
    public void WiFi_IsItsSignalAndBand_MarkedWithItsBars()
    {
        MeterReading reading = With(new MeterExtras { Wifi = new WifiFigures(78, 3, "5 GHz") });

        Assert.Equal("78", Line(MeterField.WifiSignal, reading).Value);
        Assert.Equal("% · 5 GHz", Line(MeterField.WifiSignal, reading).Unit);
        Assert.Equal(MeterMark.WifiThree, MeterReadout.MarkOf(MeterField.WifiSignal, reading));
        Assert.Equal(MeterMark.Wifi, MeterReadout.MarkOf(MeterField.WifiSignal, With(MeterExtras.None)));
    }

    [Fact]
    public void AJsonPlace_ShowsItsValueAndUnit()
    {
        MeterFeed feed = new() { Address = "https://example.com/a.json", Path = "price", Unit = "$", Label = "BTC" };
        MeterSlot slot = new() { Field = MeterField.Feed, Feed = feed };
        MeterReading reading = With(new MeterExtras
        {
            Feeds = new Dictionary<string, FieldReading> { [MeterReadout.FeedKey(feed)] = new FieldReading("64123", 64123, null) },
        });

        MeterLine line = Line(MeterField.Feed, reading, slot);

        Assert.Equal("BTC", line.Label);
        Assert.Equal("64123", line.Value);
        Assert.Equal("$", line.Unit);
        Assert.Equal(64123, MeterReadout.Number(MeterField.Feed, slot, reading));
        Assert.Equal(MeterReadout.Unknown, Line(MeterField.Feed, With(MeterExtras.None), slot).Value);
    }

    [Fact]
    public void Temperatures_FollowTheScaleChosen()
    {
        SystemSnapshot machine = SystemSnapshot.Empty with { BoardCelsius = 40 };
        MeterReading celsius = new(0, 0, machine);
        MeterReading fahrenheit = celsius with { Extras = new MeterExtras { Temperature = TemperatureUnit.Fahrenheit } };

        Assert.Equal(("40", "°C"), (Line(MeterField.BoardTemperature, celsius).Value, Line(MeterField.BoardTemperature, celsius).Unit));
        Assert.Equal(("104", "°F"), (Line(MeterField.BoardTemperature, fahrenheit).Value, Line(MeterField.BoardTemperature, fahrenheit).Unit));
        Assert.Equal("°F", MeterReadout.Unit(MeterField.GraphicsTemperature, RateFamily.Bytes, RateScale.Auto, TemperatureUnit.Fahrenheit));
    }

    /// <summary>From 38 °C a temperature in Fahrenheit has three digits, so room is kept for three.</summary>
    [Fact]
    public void Fahrenheit_KeepsRoomForThreeDigitsFromTheStart()
    {
        Assert.Equal("000", MeterReserve.Template(MeterField.ProcessorTemperature, RateScale.Auto, "99", null, TemperatureUnit.Fahrenheit));
        Assert.Equal("00", MeterReserve.Template(MeterField.ProcessorTemperature, RateScale.Auto, "64", null));
    }

    [Fact]
    public void TheBattery_IsItsChargeAndTime()
    {
        BatteryReading discharging = new(84, BatteryState.Discharging, false, false, null, TimeSpan.FromMinutes(135), null, null, null, null, null, null, []);
        BatteryReading charging = discharging with { State = BatteryState.Charging, TimeLeft = null, TimeToFull = TimeSpan.FromMinutes(42) };
        MeterReading away = new(0, 0, SystemSnapshot.Empty with { Battery = discharging });
        MeterReading plugged = new(0, 0, SystemSnapshot.Empty with { Battery = charging });

        Assert.Equal(("84", "%"), (Line(MeterField.BatteryCharge, away).Value, Line(MeterField.BatteryCharge, away).Unit));
        Assert.Equal("2:15", Line(MeterField.BatteryTime, away).Value);
        Assert.Equal("0:42", Line(MeterField.BatteryTime, plugged).Value);
        Assert.Equal(MeterMark.BatteryCharging, MeterReadout.MarkOf(MeterField.BatteryCharge, plugged));
        Assert.Equal(MeterMark.Battery, MeterReadout.MarkOf(MeterField.BatteryCharge, away));
        Assert.Equal(135, MeterReadout.Number(MeterField.BatteryTime, new MeterSlot(), away));
        Assert.Equal(MeterReadout.Unknown, Line(MeterField.BatteryCharge, MeterReading.Empty).Value);
    }

    [Fact]
    public void GraphicsCards_CanBeChosenOneByOneOrAllTogether()
    {
        GraphicsReading Card(string name, double busy) => new(name, busy, 60, null, null, null, null, 0, 0, [], []);
        SystemSnapshot machine = SystemSnapshot.Empty with { Taken = DateTimeOffset.Now, Graphics = [Card("Intel Arc", 12), Card("NVIDIA RTX", 54)] };
        MeterReading reading = new(0, 0, machine);

        Assert.Equal("54", Line(MeterField.GraphicsLoad, reading, new MeterSlot { Field = MeterField.GraphicsLoad }).Value);
        Assert.Equal("12", Line(MeterField.GraphicsLoad, reading, new MeterSlot { Field = MeterField.GraphicsLoad, Card = "Intel Arc" }).Value);
        Assert.Equal("12 · 54", Line(MeterField.GraphicsLoad, reading, new MeterSlot { Field = MeterField.GraphicsLoad, Card = MeterSlot.EveryCard }).Value);
        Assert.Equal(MeterReadout.Unknown, Line(MeterField.GraphicsLoad, reading, new MeterSlot { Field = MeterField.GraphicsLoad, Card = "Gone" }).Value);
    }

    [Fact]
    public void ARotatingPlace_NamesEachOfItsReadingsInTheTooltip()
    {
        MeterSlot slot = new() { Field = MeterField.RouterPing, Rotate = [MeterField.TodayTotal] };
        string tooltip = MeterReadout.Tooltip([slot], With(new MeterExtras { RouterMilliseconds = 4, TodayBytes = 1024 }), new TaskbarSettings());

        Assert.Contains("Response time, router  4 ms", tooltip, StringComparison.Ordinal);
        Assert.Contains("Used today  1.00 KB", tooltip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MeterField.RouterPing, false)]
    [InlineData(MeterField.TodayTotal, false)]
    [InlineData(MeterField.Feed, false)]
    [InlineData(MeterField.WifiSignal, false)]
    [InlineData(MeterField.Pacer, false)]
    [InlineData(MeterField.BatteryCharge, true)]
    [InlineData(MeterField.NpuLoad, true)]
    public void OnlyTheMachinesReadings_StartTheMachine(MeterField field, bool needs)
    {
        Assert.Equal(needs, MeterReadout.NeedsMachine(field));
    }

    [Theory]
    [InlineData(0, RateFamily.Bytes, "0")]
    [InlineData(512, RateFamily.Bytes, "512")]
    [InlineData(999, RateFamily.Bytes, "999")]
    [InlineData(1000, RateFamily.Bytes, "1.0K")]
    [InlineData(1230, RateFamily.Bytes, "1.2K")]
    [InlineData(12 * 1024, RateFamily.Bytes, "12K")]
    [InlineData(123 * 1024, RateFamily.Bytes, "123K")]
    [InlineData(1023.9 * 1024, RateFamily.Bytes, "1.0M")]
    [InlineData(5.5 * 1024 * 1024, RateFamily.Bytes, "5.5M")]
    [InlineData(2.0 * 1024 * 1024 * 1024, RateFamily.Bytes, "2.0G")]
    [InlineData(125, RateFamily.Bits, "1.0k")]
    [InlineData(125_000, RateFamily.Bits, "1.0M")]
    [InlineData(double.NaN, RateFamily.Bytes, "0")]
    public void TheTraysFigures_FitInFourCharacters(double bytes, RateFamily family, string expected)
    {
        string text = TrayFigures.Compact(bytes, family);

        Assert.Equal(expected, text);
        Assert.True(text.Length <= 4);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1000, 0)]
    [InlineData(1024, 1)]
    [InlineData(10 * 1024, 2.5)]
    [InlineData(1024 * 1000, 5.5)]
    [InlineData(1e12, Pace.Fastest)]
    public void TheWindsock_FluttersHarderAsMoreMoves(double bytes, double flaps)
    {
        Assert.Equal(flaps, Pace.FlutterPerSecond(bytes), 2);
    }

    [Fact]
    public void TheWindsock_HangsLimpWhenNothingMoves()
    {
        Assert.Equal(1, Pace.Droop(0));
        Assert.Equal(0, Pace.Droop(1e12));
    }

    [Fact]
    public void AGraphsMinute_TakesOneValueASecond_OldestFirst()
    {
        MeterHistory history = new();
        Span<double> into = stackalloc double[MeterHistory.Span];

        Assert.True(history.Add("down", 1, TimeSpan.FromSeconds(0)));
        Assert.False(history.Add("down", 2, TimeSpan.FromSeconds(0.5)));
        Assert.True(history.Add("down", 3, TimeSpan.FromSeconds(1)));

        Assert.Equal(2, history.CopyTo("down", into));
        Assert.Equal(1, into[0]);
        Assert.Equal(3, into[1]);
    }

    [Fact]
    public void AGraphsMinute_KeepsOnlyTheLastSixty()
    {
        MeterHistory history = new();
        double[] into = new double[MeterHistory.Span];

        for (int each = 0; each < 90; each++)
        {
            history.Add("cpu", each, TimeSpan.FromSeconds(each));
        }

        Assert.Equal(MeterHistory.Span, history.CopyTo("cpu", into));
        Assert.Equal(30, into[0]);
        Assert.Equal(89, into[^1]);
    }

    [Fact]
    public void AGraphsMinute_KeepsGapsAndForgetsWhatIsNoLongerShown()
    {
        MeterHistory history = new();
        double[] into = new double[2];

        history.Add("ping", double.PositiveInfinity, TimeSpan.Zero);
        history.Add("cpu", 5, TimeSpan.Zero);
        history.Keep(["ping"]);

        Assert.Equal(1, history.CopyTo("ping", into));
        Assert.True(double.IsNaN(into[0]));
        Assert.Equal(0, history.CopyTo("cpu", into));
    }
}
