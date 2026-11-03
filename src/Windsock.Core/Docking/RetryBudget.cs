namespace Windsock.Core.Docking;

/// <summary>
/// Counts how often something had to be done again in a short span, and says
/// when it is time to stop: a fight with another program is lost by both.
/// </summary>
/// <remarks>
/// Narrowing a classic taskbar's task band is undone by Explorer whenever it
/// lays the bar out again, which is fine now and then. If the band is put
/// back every time it is narrowed, the two would fight every patrol for good;
/// this notices that and calls it off.
/// </remarks>
public sealed class RetryBudget(int most, TimeSpan span)
{
    private readonly Queue<TimeSpan> _tries = new();

    /// <summary>Whether the budget was spent and nothing more should be tried.</summary>
    public bool IsSpent { get; private set; }

    /// <summary>
    /// Counts one more try at <paramref name="now"/>, and says whether it may
    /// go ahead.
    /// </summary>
    public bool Try(TimeSpan now)
    {
        if (IsSpent)
        {
            return false;
        }

        while (_tries.Count > 0 && now - _tries.Peek() > span)
        {
            _tries.Dequeue();
        }

        _tries.Enqueue(now);

        if (_tries.Count > most)
        {
            IsSpent = true;
            return false;
        }

        return true;
    }

    /// <summary>Starts counting afresh.</summary>
    public void Reset()
    {
        _tries.Clear();
        IsSpent = false;
    }
}
