namespace Windsock.Core.Networking;

/// <summary>
/// A fixed-capacity, in-memory ring of the most recent samples.
/// </summary>
public sealed class ThroughputHistory
{
    private readonly Lock _gate = new();
    private readonly ThroughputSample[] _buffer;
    private int _count;
    private int _next;

    public ThroughputHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buffer = new ThroughputSample[capacity];
    }

    /// <summary>Maximum number of samples retained.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Number of samples currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Appends a sample, evicting the oldest once at capacity.</summary>
    public void Add(in ThroughputSample sample)
    {
        lock (_gate)
        {
            _buffer[_next] = sample;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>
    /// Seeds the ring with <paramref name="count"/> zero samples ending just
    /// before <paramref name="endingAt"/>.
    /// </summary>
    public void Prefill(int count, DateTimeOffset endingAt, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        int capped = Math.Min(count, _buffer.Length);
        for (int i = capped; i > 0; i--)
        {
            Add(new ThroughputSample(endingAt - (interval * i), 0, 0));
        }
    }

    /// <summary>Removes every retained sample.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _count = 0;
            _next = 0;
        }
    }

    /// <summary>
    /// Copies the retained samples, oldest first, into <paramref name="destination"/>.
    /// </summary>
    public int CopyTo(Span<ThroughputSample> destination)
    {
        lock (_gate)
        {
            int written = Math.Min(_count, destination.Length);
            int start = (_next - _count + _buffer.Length) % _buffer.Length;

            // Skip the oldest samples when the destination cannot hold them all.
            start = (start + (_count - written)) % _buffer.Length;

            for (int i = 0; i < written; i++)
            {
                destination[i] = _buffer[(start + i) % _buffer.Length];
            }

            return written;
        }
    }
}
