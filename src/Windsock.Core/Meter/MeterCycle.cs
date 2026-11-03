namespace Windsock.Core.Meter;

/// <summary>
/// Which reading a rotating place shows now, holding still while the pointer
/// is over the meter so the reading can be read.
/// </summary>
/// <remarks>
/// Time is counted only while nobody is holding the place, so a reading
/// pointed at stays put, and the rotation carries on from where it stopped
/// rather than jumping to wherever the clock has got to.
/// </remarks>
public sealed class MeterCycle
{
    private TimeSpan _counted;
    private TimeSpan _last;
    private bool _started;

    /// <summary>The reading in the rotation shown now, from zero.</summary>
    public int Index { get; private set; }

    /// <summary>
    /// Moves the rotation on to <paramref name="now"/> and says which of
    /// <paramref name="count"/> readings to show, and whether that changed.
    /// </summary>
    /// <param name="count">How many readings the place rotates through.</param>
    /// <param name="each">How long each stays.</param>
    /// <param name="now">A steadily increasing clock.</param>
    /// <param name="held">Whether the pointer is over the meter.</param>
    public bool Advance(int count, TimeSpan each, TimeSpan now, bool held)
    {
        if (!_started)
        {
            _started = true;
            _last = now;
        }

        if (!held && now > _last)
        {
            _counted += now - _last;
        }

        _last = now;

        int wanted = count <= 1 || each <= TimeSpan.Zero
            ? 0
            : (int)(_counted.Ticks / each.Ticks % count);

        if (wanted == Index)
        {
            return false;
        }

        Index = wanted;
        return true;
    }

    /// <summary>Starts the rotation again from its first reading.</summary>
    public void Restart()
    {
        _counted = TimeSpan.Zero;
        _started = false;
        Index = 0;
    }
}
