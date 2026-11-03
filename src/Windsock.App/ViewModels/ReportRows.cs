using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.App.Controls;
using Windsock.Core.Apps;

namespace Windsock.App.ViewModels;

/// <summary>A tab of the Reports page.</summary>
public enum ReportView
{
    Report,
    Compare,
    Calendar,
    Week,
    Lifetime,
}

/// <summary>What a period is compared against.</summary>
public enum CompareWith
{
    PeriodBefore,
    SameLastYear,
}

/// <summary>How far back the week's heatmap looks.</summary>
public enum HeatSpan
{
    FourWeeks,
    TwelveWeeks,
    Year,
}

/// <summary>A tab, with its name.</summary>
public sealed record ReportViewOption(ReportView View, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One line of a list of figures: a name, an amount and a share.</summary>
/// <param name="Name">What it is.</param>
/// <param name="Amount">How much, written out.</param>
/// <param name="Share">Its share of the whole, 0 to 1.</param>
/// <param name="Note">A word more, or empty.</param>
public sealed record ShareRow(string Name, string Amount, double Share, string Note = "")
{
    public string ShareText => UsageText.Percent(Share);

    /// <summary>What a screen reader says for the row; a record's own would list its fields.</summary>
    public override string ToString() =>
        string.Join(", ", new[] { Name, Amount, ShareText, Note }.Where(part => part.Length > 0));
}

/// <summary>What a month's picture says: figures already written out, and no names of networks or places.</summary>
/// <param name="Month">"October 2026".</param>
/// <param name="Subtitle">What the figures count.</param>
/// <param name="Total">Everything, together.</param>
/// <param name="Change">The change from the month before, in words.</param>
/// <param name="Down">Downloaded.</param>
/// <param name="Up">Uploaded.</param>
/// <param name="Average">A day, on average.</param>
/// <param name="Days">A bar a day.</param>
/// <param name="Busiest">The busiest day and hour.</param>
/// <param name="Apps">The busiest few apps.</param>
/// <param name="Footer">Where the figures came from, and when.</param>
public sealed record MonthCardModel(
    string Month,
    string Subtitle,
    string Total,
    string Change,
    string Down,
    string Up,
    string Average,
    IReadOnlyList<ChartBar> Days,
    string Busiest,
    IReadOnlyList<ShareRow> Apps,
    string Footer);

/// <summary>One app in a report, with its icon once it is read.</summary>
public sealed partial class AppReportRow : ObservableObject
{
    public AppReportRow(AppDescription app, string amount, double share, string note)
    {
        ArgumentNullException.ThrowIfNull(app);
        App = app;
        Name = app.Name;
        Amount = amount;
        Share = share;
        Note = note;
    }

    public AppDescription App { get; }

    public string Name { get; }

    public string Amount { get; }

    public double Share { get; }

    /// <summary>Who counted it, or how it changed.</summary>
    public string Note { get; }

    public string ShareText => UsageText.Percent(Share);

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    /// <summary>What a screen reader says for the row: the tile's name, amount and note.</summary>
    public override string ToString() =>
        string.Join(", ", new[] { Name, Amount, Note }.Where(part => part.Length > 0));
}
