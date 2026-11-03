using System.Globalization;
using System.Text;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;

namespace Windsock.Core.Meter;

/// <summary>An app and what it moved, for the hover card's busiest apps.</summary>
/// <param name="Name">The app's name.</param>
/// <param name="Bytes">Bytes it moved, both ways, over the span.</param>
public readonly record struct BusyApp(string Name, long Bytes);

/// <summary>
/// The sentences on the meter's hover card: the connection, Wi-Fi and the
/// busiest apps, each in a line.
/// </summary>
public static class CardWords
{
    /// <summary>
    /// The connection in a line: "Online · router 2 ms · internet 14 ms",
    /// "Internet unreachable since 21:04", "Sign-in needed", or "Checking the
    /// connection" before anything is settled.
    /// </summary>
    public static string Connection(ConnectionState state, ConnectionSample? latest)
    {
        if (latest is { Facts.Reach: WindowsReach.SignInRequired })
        {
            return "Sign-in needed";
        }

        if (state.IsOffline)
        {
            string headline = OutageWords.Headline(state.Cause ?? OutageCause.Unknown);

            return state.Since is { } since
                ? string.Create(CultureInfo.CurrentCulture, $"{headline} since {ClockFormat.Time(since.ToLocalTime())}")
                : headline;
        }

        if (state.Status != ConnectionStatus.Online)
        {
            return "Checking the connection";
        }

        StringBuilder text = new("Online");

        if (latest is { Router: { } router })
        {
            text.Append(" · router ").Append(Milliseconds(router));
        }

        if (latest is { InternetChecked: true, Internet: { } internet })
        {
            text.Append(" · internet ").Append(Milliseconds(internet));
        }

        return text.ToString();
    }

    /// <summary>
    /// The Wi-Fi link in a line: "78% · 5 GHz · Wi-Fi 6 · 866 Mbps", each part
    /// left out where it is not known.
    /// </summary>
    public static string Wifi(double? percent, string? band, string? generation, double? receiveMbps)
    {
        List<string> parts = [];

        if (percent is { } signal)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{Math.Round(signal):0}%"));
        }

        if (band is { Length: > 0 })
        {
            parts.Add(band);
        }

        if (generation is { Length: > 0 })
        {
            parts.Add(generation);
        }

        if (receiveMbps is { } speed && speed > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{Math.Round(speed):0} Mbps"));
        }

        return parts.Count == 0 ? "Connected" : string.Join(" · ", parts);
    }

    /// <summary>The busiest apps, most first, leaving out any that moved nothing.</summary>
    public static IReadOnlyList<BusyApp> Busiest(IEnumerable<BusyApp> apps, int count)
    {
        ArgumentNullException.ThrowIfNull(apps);

        return
        [
            .. apps
                .Where(app => app.Bytes > 0 && app.Name.Length > 0)
                .OrderByDescending(app => app.Bytes)
                .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(Math.Max(0, count)),
        ];
    }

    /// <summary>
    /// A minute of samples taken every <paramref name="interval"/>, as one
    /// value a second - the fastest of each second - newest last.
    /// </summary>
    public static int Seconds(ReadOnlySpan<double> samples, TimeSpan interval, Span<double> into)
    {
        if (samples.IsEmpty || into.IsEmpty || interval <= TimeSpan.Zero)
        {
            return 0;
        }

        int each = Math.Max(1, (int)Math.Round(TimeSpan.FromSeconds(1) / interval));
        int seconds = Math.Min(into.Length, (samples.Length + each - 1) / each);
        int end = samples.Length;

        for (int second = seconds - 1; second >= 0; second--)
        {
            int start = Math.Max(0, end - each);
            double most = double.NaN;

            for (int at = start; at < end; at++)
            {
                if (double.IsFinite(samples[at]) && (double.IsNaN(most) || samples[at] > most))
                {
                    most = samples[at];
                }
            }

            into[second] = most;
            end = start;
        }

        return seconds;
    }

    private static string Milliseconds(TimeSpan taken) =>
        taken.TotalMilliseconds < 1
            ? "<1 ms"
            : string.Create(CultureInfo.CurrentCulture, $"{Math.Round(taken.TotalMilliseconds):0} ms");
}
