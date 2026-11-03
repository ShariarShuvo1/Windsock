using System.Diagnostics;

namespace Windsock.Core.Hardware;

/// <summary>
/// Turns a processor's running energy counter into watts.
/// </summary>
/// <remarks>
/// <para>
/// Intel's <c>MSR_PKG_ENERGY_STATUS</c> (0x611) and AMD's
/// <c>MSR_PKG_ENERGY_STAT</c> (0xC001029B) both count energy in the low 32
/// bits, in units the chip states in bits 12:8 of its power-unit register
/// (Intel 0x606, AMD 0xC0010299): a count is 1/2^n joules. Typical client
/// chips use n = 14 (61 microjoules) on Intel and 16 on AMD.
/// </para>
/// <para>
/// Watts are the counter's difference over the time between two readings.
/// The difference is taken in 32 bits, so a counter that wrapped once
/// between them still reads right; one that could have wrapped twice cannot
/// be told from a quiet chip, so after a gap longer than
/// <see cref="LongestGap"/> - a sampling pause, nobody watching - the reading
/// starts again rather than reports. At 61 microjoules the counter wraps
/// after 262 kJ: 17 minutes at 250 W, so a minute is safely inside it.
/// </para>
/// <para>
/// Read-only. Only the reading functions of PawnIO's modules are called;
/// nothing here writes a register.
/// </para>
/// </remarks>
internal sealed class CpuEnergyMeter
{
    /// <summary>The longest gap between readings that still gives watts.</summary>
    public static readonly TimeSpan LongestGap = TimeSpan.FromMinutes(1);

    /// <summary>Readings closer together than this are too short to divide by.</summary>
    public static readonly TimeSpan ShortestGap = TimeSpan.FromMilliseconds(10);

    /// <summary>Nothing above this is a client processor; it is a misread register.</summary>
    public const double MostWatts = 1000;

    private readonly double _joulesPerCount;
    private uint _last;
    private long _lastAt;
    private bool _hasLast;
    private double? _watts;

    public CpuEnergyMeter(double joulesPerCount) => _joulesPerCount = joulesPerCount;

    /// <summary>
    /// What a count is worth, from the power-unit register, or nothing where
    /// the register makes no sense. Bits 12:8 are the energy status unit.
    /// </summary>
    public static double? JoulesPerCount(long powerUnit)
    {
        int unit = (int)((powerUnit >> 8) & 0x1F);

        // Zero would be a joule a count, which no chip uses: an empty register.
        return unit == 0 ? null : 1.0 / (1L << unit);
    }

    /// <summary>
    /// The difference between two readings of a 32-bit counter, across one wrap.
    /// </summary>
    public static uint Difference(long earlier, long later) => unchecked((uint)later - (uint)earlier);

    /// <summary>
    /// Takes a reading of the energy register at a <see cref="Stopwatch"/>
    /// timestamp, and answers the watts since the last one.
    /// </summary>
    public double? Next(long register, long timestamp)
    {
        uint now = unchecked((uint)register);

        if (!_hasLast)
        {
            Remember(now, timestamp);
            return null;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastAt, timestamp);

        if (elapsed < ShortestGap && elapsed >= TimeSpan.Zero)
        {
            return _watts;
        }

        if (elapsed > LongestGap || elapsed < TimeSpan.Zero)
        {
            Remember(now, timestamp);
            _watts = null;
            return null;
        }

        uint counts = Difference(_last, now);
        double watts = counts * _joulesPerCount / elapsed.TotalSeconds;
        Remember(now, timestamp);

        // A counter that did not move is a chip that does not count, not a chip
        // drawing nothing: real packages draw watts even asleep.
        _watts = watts is > 0 and <= MostWatts ? watts : null;
        return _watts;
    }

    /// <summary>Forgets the last reading, so the next one starts again.</summary>
    public void Reset()
    {
        _hasLast = false;
        _watts = null;
    }

    private void Remember(uint now, long timestamp)
    {
        _last = now;
        _lastAt = timestamp;
        _hasLast = true;
    }
}
