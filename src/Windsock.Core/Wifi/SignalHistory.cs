namespace Windsock.Core.Wifi;

/// <summary>One reading of the Wi-Fi signal, as a chart draws it.</summary>
/// <param name="At">When it was read.</param>
/// <param name="Rssi">The signal in dBm, or nothing when Wi-Fi was not connected.</param>
/// <param name="ReceiveMbps">The receive link rate, or nothing.</param>
/// <param name="TransmitMbps">The transmit link rate, or nothing.</param>
/// <param name="Band">The band at the time.</param>
public readonly record struct SignalPoint(DateTimeOffset At, int? Rssi, double? ReceiveMbps, double? TransmitMbps, WifiBand Band);

/// <summary>
/// The recent Wi-Fi readings, oldest first, in a fixed amount of memory, and
/// the means to line them up with the throughput chart's samples.
/// </summary>
public sealed class SignalHistory
{
    private readonly SignalPoint[] _points;
    private readonly Lock _gate = new();
    private int _next;
    private int _count;

    public SignalHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _points = new SignalPoint[capacity];
    }

    /// <summary>How many readings it holds at most.</summary>
    public int Capacity => _points.Length;

    /// <summary>How many it holds now.</summary>
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

    /// <summary>Adds a reading, dropping the oldest once full.</summary>
    public void Add(in SignalPoint point)
    {
        lock (_gate)
        {
            _points[_next] = point;
            _next = (_next + 1) % _points.Length;
            _count = Math.Min(_count + 1, _points.Length);
        }
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>The readings since a moment, oldest first.</summary>
    public SignalPoint[] Since(DateTimeOffset from)
    {
        lock (_gate)
        {
            List<SignalPoint> kept = new(_count);

            for (int each = 0; each < _count; each++)
            {
                SignalPoint point = _points[(_next - _count + each + _points.Length) % _points.Length];

                if (point.At >= from)
                {
                    kept.Add(point);
                }
            }

            return [.. kept];
        }
    }

    /// <summary>
    /// For each moment, the signal read closest to it within <paramref name="tolerance"/>,
    /// or nothing - so the signal can be drawn under the throughput chart, one
    /// value per throughput sample, with gaps where Wi-Fi was not read.
    /// </summary>
    /// <param name="moments">The throughput samples' times, oldest first.</param>
    /// <param name="readings">Signal readings, oldest first.</param>
    /// <param name="tolerance">How far apart a reading and a moment may be.</param>
    public static int?[] Align(IReadOnlyList<DateTimeOffset> moments, IReadOnlyList<SignalPoint> readings, TimeSpan tolerance)
    {
        ArgumentNullException.ThrowIfNull(moments);
        ArgumentNullException.ThrowIfNull(readings);

        int?[] aligned = new int?[moments.Count];
        int at = 0;

        for (int each = 0; each < moments.Count; each++)
        {
            DateTimeOffset moment = moments[each];

            // Step forward while the next reading is closer; a tie keeps the earlier.
            while (at + 1 < readings.Count
                && (readings[at + 1].At - moment).Duration() < (readings[at].At - moment).Duration())
            {
                at++;
            }

            if (readings.Count > 0 && (readings[at].At - moment).Duration() <= tolerance)
            {
                aligned[each] = readings[at].Rssi;
            }
        }

        return aligned;
    }
}
