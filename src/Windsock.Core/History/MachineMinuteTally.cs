using Windsock.Core.Connectivity;
using Windsock.Core.Hardware;

namespace Windsock.Core.History;

/// <summary>Gathers the machine's readings into minutes.</summary>
public sealed class SystemMinuteTally
{
    /// <summary>The most a reading stands for; a longer gap is time nobody read.</summary>
    private static readonly TimeSpan LongestStretch = TimeSpan.FromSeconds(10);

    private DateTimeOffset? _lastAt;
    private long _minute = long.MinValue;
    private int _samples;
    private long _cpuSum;
    private int _cpuPeak;
    private long _heatSum;
    private int _heatSamples;
    private int? _heatPeak;
    private long _gpuSum;
    private int _gpuPeak;
    private int? _gpuHeat;
    private long _memorySum;
    private long _memoryPeak;
    private long _memoryTotal;
    private double _read;
    private double _write;

    /// <summary>Takes one reading; returns the minute it finished, if it started a new one.</summary>
    public SystemMinute? Add(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.HasValue)
        {
            return null;
        }

        long minute = EpochMinute.From(snapshot.Taken);
        SystemMinute? finished = null;

        if (minute != _minute)
        {
            finished = Take();
            _minute = minute;
        }

        // Drives report a rate; what it stands for is the stretch since the
        // reading before, however often readings come.
        double seconds = _lastAt is { } last && snapshot.Taken > last
            ? Math.Min((snapshot.Taken - last).TotalSeconds, LongestStretch.TotalSeconds)
            : 1;
        _lastAt = snapshot.Taken;

        _samples++;

        int cpu = Tenths(snapshot.Processor.BusyPercent);
        _cpuSum += cpu;
        _cpuPeak = Math.Max(_cpuPeak, cpu);

        if (snapshot.Processor.Celsius is { } celsius)
        {
            int heat = Tenths(celsius);
            _heatSum += heat;
            _heatSamples++;
            _heatPeak = Math.Max(_heatPeak ?? int.MinValue, heat);
        }

        int gpu = 0;

        foreach (GraphicsReading adapter in snapshot.Graphics)
        {
            gpu = Math.Max(gpu, Tenths(adapter.BusyPercent));

            if (adapter.Celsius is { } adapterHeat)
            {
                _gpuHeat = Math.Max(_gpuHeat ?? int.MinValue, Tenths(adapterHeat));
            }
        }

        _gpuSum += gpu;
        _gpuPeak = Math.Max(_gpuPeak, gpu);

        _memorySum += snapshot.Memory.Used;
        _memoryPeak = Math.Max(_memoryPeak, snapshot.Memory.Used);
        _memoryTotal = Math.Max(_memoryTotal, snapshot.Memory.Total);

        foreach (DriveReading drive in snapshot.Drives)
        {
            _read += Math.Max(0, drive.ReadBytesPerSecond) * seconds;
            _write += Math.Max(0, drive.WriteBytesPerSecond) * seconds;
        }

        return finished;
    }

    /// <summary>Hands over the minute under way, if it has anything, and starts afresh.</summary>
    public SystemMinute? Take()
    {
        SystemMinute? taken = _samples == 0
            ? null
            : new SystemMinute(
                _minute,
                _samples,
                _cpuSum,
                _cpuPeak,
                _heatSum,
                _heatSamples,
                _heatPeak,
                _gpuSum,
                _gpuPeak,
                _gpuHeat,
                _memorySum,
                _memoryPeak,
                _memoryTotal,
                (long)Math.Round(_read),
                (long)Math.Round(_write));

        _samples = 0;
        _cpuSum = 0;
        _cpuPeak = 0;
        _heatSum = 0;
        _heatSamples = 0;
        _heatPeak = null;
        _gpuSum = 0;
        _gpuPeak = 0;
        _gpuHeat = null;
        _memorySum = 0;
        _memoryPeak = 0;
        _memoryTotal = 0;
        _read = 0;
        _write = 0;

        return taken;
    }

    /// <summary>Forgets when the last reading was, so a pause does not count as one long stretch.</summary>
    public void Break() => _lastAt = null;

    private static int Tenths(double value) =>
        double.IsFinite(value) ? (int)Math.Round(Math.Clamp(value, 0, 100_000) * 10) : 0;
}

/// <summary>Gathers the connection checks into minutes.</summary>
public sealed class ConnectionMinuteTally
{
    private long _minute = long.MinValue;
    private int _checks;
    private int _offline;
    private int _routerTried;
    private int _routerAnswered;
    private long _routerSum;
    private int _routerPeak;
    private int _internetTried;
    private int _internetAnswered;
    private long _internetSum;
    private int _internetPeak;

    /// <summary>Takes one check; returns the minute it finished, if it started a new one.</summary>
    public ConnectionMinute? Add(in ConnectionSample sample, bool offline)
    {
        long minute = EpochMinute.From(sample.At);
        ConnectionMinute? finished = null;

        if (minute != _minute)
        {
            finished = Take();
            _minute = minute;
        }

        _checks++;
        _offline += offline ? 1 : 0;

        if (sample.RouterChecked)
        {
            _routerTried++;

            if (sample.Router is { } router)
            {
                int tenths = Tenths(router);
                _routerAnswered++;
                _routerSum += tenths;
                _routerPeak = Math.Max(_routerPeak, tenths);
            }
        }

        if (sample.InternetChecked)
        {
            _internetTried++;

            if (sample.Internet is { } internet)
            {
                int tenths = Tenths(internet);
                _internetAnswered++;
                _internetSum += tenths;
                _internetPeak = Math.Max(_internetPeak, tenths);
            }
        }

        return finished;
    }

    /// <summary>Hands over the minute under way, if it has anything, and starts afresh.</summary>
    public ConnectionMinute? Take()
    {
        ConnectionMinute? taken = _checks == 0
            ? null
            : new ConnectionMinute(
                _minute,
                _checks,
                _offline,
                _routerTried,
                _routerAnswered,
                _routerSum,
                _routerPeak,
                _internetTried,
                _internetAnswered,
                _internetSum,
                _internetPeak);

        _checks = 0;
        _offline = 0;
        _routerTried = 0;
        _routerAnswered = 0;
        _routerSum = 0;
        _routerPeak = 0;
        _internetTried = 0;
        _internetAnswered = 0;
        _internetSum = 0;
        _internetPeak = 0;

        return taken;
    }

    private static int Tenths(TimeSpan elapsed) =>
        (int)Math.Round(Math.Clamp(elapsed.TotalMilliseconds, 0, 100_000) * 10);
}
