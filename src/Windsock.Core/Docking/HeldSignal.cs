namespace Windsock.Core.Docking;

/// <summary>
/// A yes-or-no reading that only changes once the new answer has held for a
/// while, so a flicker does not make the meter blink.
/// </summary>
/// <remarks>
/// Alt+Tab between a full-screen game and the desktop passes through a frame
/// or two of each, and hiding and showing the meter for each of them is worse
/// than either. The held answer is what the meter follows.
/// </remarks>
public sealed class HeldSignal(TimeSpan hold)
{
    private bool _seen;
    private TimeSpan _since;

    /// <summary>The answer that has held long enough to act on.</summary>
    public bool Value { get; private set; }

    /// <summary>
    /// Takes a fresh reading at <paramref name="now"/> and says whether
    /// <see cref="Value"/> changed.
    /// </summary>
    public bool Update(bool reading, TimeSpan now)
    {
        if (reading != _seen)
        {
            _seen = reading;
            _since = now;
        }

        if (_seen == Value || now - _since < hold)
        {
            return false;
        }

        Value = _seen;
        return true;
    }

    /// <summary>Forgets everything, as if nothing had been seen.</summary>
    public void Reset()
    {
        _seen = false;
        Value = false;
        _since = TimeSpan.Zero;
    }
}
