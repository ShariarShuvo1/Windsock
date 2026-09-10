namespace Windsock.Core.Processes;

/// <summary>One process's contribution at a moment in time.</summary>
public readonly record struct ProcessShare(int ProcessId, string Name, double BytesPerSecond);

/// <summary>
/// Remembers which processes were behind the traffic, so a point on the chart
/// can say who caused it.
/// </summary>
public sealed class ProcessAttributionHistory
{
    private readonly Lock _gate = new();
    private readonly DateTimeOffset[] _stamps;
    private readonly ProcessShare[] _shares;
    private readonly int[] _counts;
    private int _head;
    private int _count;

    public ProcessAttributionHistory(int capacity, int width, TimeSpan resolution = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);

        Capacity = capacity;
        Width = width;
        Resolution = resolution <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : resolution;
        _stamps = new DateTimeOffset[capacity];
        _shares = new ProcessShare[capacity * width];
        _counts = new int[capacity];
    }

    /// <summary>How many snapshots are kept.</summary>
    public int Capacity { get; }

    /// <summary>How many processes are kept from each snapshot.</summary>
    public int Width { get; }

    /// <summary>
    /// How far apart snapshots are taken, which is how precisely a moment can
    /// be attributed at all.
    /// </summary>
    public TimeSpan Resolution { get; }

    /// <summary>Records the busiest processes of one moment.</summary>
    public void Add(DateTimeOffset at, IReadOnlyList<ProcessUsage> rows)
    {
        lock (_gate)
        {
            int slot = _head;
            int start = slot * Width;
            int kept = 0;

            foreach (ProcessUsage row in rows)
            {
                double rate = row.TotalBytesPerSecond;

                if (rate <= 0)
                {
                    continue;
                }
                int position;

                if (kept < Width)
                {
                    position = kept;
                    kept++;
                }
                else if (rate > _shares[start + Width - 1].BytesPerSecond)
                {
                    position = Width - 1;
                }
                else
                {
                    continue;
                }

                while (position > 0 && _shares[start + position - 1].BytesPerSecond < rate)
                {
                    _shares[start + position] = _shares[start + position - 1];
                    position--;
                }

                _shares[start + position] = new ProcessShare(row.ProcessId, row.Name, rate);
            }

            _stamps[slot] = at;
            _counts[slot] = kept;

            _head = (_head + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
        }
    }

    /// <summary>Copies the snapshot nearest <paramref name="at"/> into <paramref name="destination"/>.</summary>
    public int Find(DateTimeOffset at, TimeSpan tolerance, Span<ProcessShare> destination)
    {
        lock (_gate)
        {
            int best = -1;
            TimeSpan closest = TimeSpan.MaxValue;

            for (int offset = _count - 1; offset >= 0; offset--)
            {
                int index = Index(offset);
                TimeSpan gap = _stamps[index] - at;
                TimeSpan distance = gap < TimeSpan.Zero ? -gap : gap;

                if (distance < closest)
                {
                    closest = distance;
                    best = index;
                    continue;
                }
                if (gap < TimeSpan.Zero)
                {
                    break;
                }
            }

            if (best < 0 || closest > tolerance)
            {
                return 0;
            }

            int count = Math.Min(_counts[best], destination.Length);
            _shares.AsSpan(best * Width, count).CopyTo(destination);
            return count;
        }
    }

    /// <summary>Discards everything recorded so far.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _head = 0;
            _count = 0;
        }
    }

    private int Index(int offset) => (_head - _count + offset + Capacity) % Capacity;
}
