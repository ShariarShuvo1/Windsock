namespace Windsock.Core.Meter;

/// <summary>
/// The last minute of each reading a graph on the meter draws, one value a
/// second.
/// </summary>
/// <remarks>
/// Kept by the meter itself rather than read from each monitor's own history,
/// because a graph can be of anything a place shows - a temperature, a
/// response time - and each monitor keeps a different span at a different
/// pace, or none. A reading is added at most once a second whatever the pace
/// readings arrive at, so the graph's geometry changes once a second.
/// Missing readings are kept as NaN, which a graph leaves a gap for.
/// </remarks>
public sealed class MeterHistory
{
    /// <summary>How many values a graph holds: one minute.</summary>
    public const int Span = 60;

    private readonly Dictionary<string, Ring> _rings = new(StringComparer.Ordinal);
    private readonly List<string> _gone = [];

    /// <summary>
    /// Adds a reading for <paramref name="key"/> at <paramref name="now"/>,
    /// unless one was added less than a second ago. Says whether it was added.
    /// </summary>
    public bool Add(string key, double value, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!_rings.TryGetValue(key, out Ring? ring))
        {
            ring = new Ring();
            _rings[key] = ring;
        }

        if (ring.Count > 0 && now - ring.LastAt < TimeSpan.FromSeconds(1) && now >= ring.LastAt)
        {
            return false;
        }

        ring.Add(double.IsFinite(value) ? value : double.NaN, now);
        return true;
    }

    /// <summary>
    /// Copies the readings kept for <paramref name="key"/>, oldest first, into
    /// <paramref name="into"/>, and says how many there were.
    /// </summary>
    public int CopyTo(string key, Span<double> into) =>
        _rings.TryGetValue(key, out Ring? ring) ? ring.CopyTo(into) : 0;

    /// <summary>
    /// Forgets every reading not in <paramref name="keep"/>. Called every
    /// reading, so it allocates nothing when nothing goes.
    /// </summary>
    public void Keep(IReadOnlyCollection<string> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);

        _gone.Clear();

        foreach (string key in _rings.Keys)
        {
            if (!keep.Contains(key))
            {
                _gone.Add(key);
            }
        }

        foreach (string key in _gone)
        {
            _rings.Remove(key);
        }

        _gone.Clear();
    }

    /// <summary>Forgets everything.</summary>
    public void Clear() => _rings.Clear();

    private sealed class Ring
    {
        private readonly double[] _values = new double[Span];
        private int _next;

        public int Count { get; private set; }

        public TimeSpan LastAt { get; private set; }

        public void Add(double value, TimeSpan at)
        {
            _values[_next] = value;
            _next = (_next + 1) % Span;
            Count = Math.Min(Span, Count + 1);
            LastAt = at;
        }

        public int CopyTo(Span<double> into)
        {
            int count = Math.Min(Count, into.Length);
            int start = (_next - count + Span) % Span;

            for (int each = 0; each < count; each++)
            {
                into[each] = _values[(start + each) % Span];
            }

            return count;
        }
    }
}
