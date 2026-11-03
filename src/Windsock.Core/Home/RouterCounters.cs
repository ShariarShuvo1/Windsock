using System.Globalization;

namespace Windsock.Core.Home;

/// <summary>
/// Turns two readings of a router's byte counter into the bytes between them.
/// </summary>
/// <remarks>
/// <para>
/// WANCommonInterfaceConfig:1 declares TotalBytesReceived and TotalBytesSent as
/// ui4 and says nothing about what happens at the top, so they wrap at 2^32,
/// which is 4 GiB: about six minutes at 100 Mb/s, half a minute at 1 Gb/s. The
/// development machine's router read 2,524,692,590 received, well on its way.
/// A counter above 2^32 is a router keeping 64 bits, and never wraps here.
/// </para>
/// <para>
/// A fall is either a wrap or a reset - the router restarted, or its internet
/// connection did - and the two look alike. A wrap is only possible when the
/// previous reading was near the top: within what the fastest believable
/// connection (1 Gb/s) could have moved since. Anything else is a reset, and
/// the reading is dropped rather than turned into a burst of gigabytes. The
/// cost of guessing wrong that way is one missing reading; the other way, a
/// spike nobody could believe.
/// </para>
/// </remarks>
public static class CounterDelta
{
    /// <summary>The fastest a household connection is believed to move, in bytes a second: 1 Gb/s.</summary>
    public const double FastestBelievable = 125_000_000;

    private const ulong Wrap = 1UL << 32;

    /// <summary>The bytes between two readings, or nothing when the counter was reset between them.</summary>
    public static ulong? Between(ulong previous, ulong current, TimeSpan elapsed)
    {
        if (current >= previous)
        {
            return current - previous;
        }

        if (previous >= Wrap || current >= Wrap || elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        ulong wrapped = (Wrap - previous) + current;
        return wrapped <= FastestBelievable * elapsed.TotalSeconds ? wrapped : null;
    }

    /// <summary>A counter's value from the router's answer: digits only, as ui4 or a 64-bit extension.</summary>
    public static ulong? Parse(string? value) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed) ? parsed : null;
}

/// <summary>The whole household's traffic, from the router's own counters.</summary>
/// <param name="At">When the second of the two readings it is measured between was taken.</param>
/// <param name="DownBytesPerSecond">Received from the internet, by every device together; nothing when not measured.</param>
/// <param name="UpBytesPerSecond">Sent to the internet, likewise.</param>
public readonly record struct HouseholdRate(DateTimeOffset At, double? DownBytesPerSecond, double? UpBytesPerSecond);

/// <summary>Where reading the router's counters stands.</summary>
public enum HouseholdState
{
    /// <summary>Not switched on.</summary>
    Off,

    /// <summary>Switched on, but nobody is looking, so the router is not asked.</summary>
    Idle,

    /// <summary>Not on a home network with a router.</summary>
    NotHome,

    /// <summary>Asking the router.</summary>
    Starting,

    /// <summary>Reading the counters.</summary>
    Reading,

    /// <summary>The router does not report its counters over UPnP, or does not answer UPnP.</summary>
    NotOffered,
}
