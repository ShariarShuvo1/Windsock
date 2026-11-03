using System.Globalization;
using Windsock.Core.Hardware;
using Windsock.Core.Settings;

namespace Windsock.Core.Alerts;

/// <summary>
/// A reading that must stay at or above a level for a while before it counts,
/// and back below a lower level for a while before it can count again.
/// </summary>
/// <remarks>
/// The two levels are the hysteresis: a temperature sitting on its level,
/// a degree either side, fires once rather than every time it crosses. A
/// missing reading is neither above nor below and starts both clocks again.
/// </remarks>
public sealed class LevelWatch
{
    private DateTimeOffset? _over;
    private DateTimeOffset? _under;

    /// <summary>Whether it has fired and not yet been re-armed.</summary>
    public bool HasFired { get; private set; }

    /// <summary>Takes a reading, and says whether it fires now.</summary>
    /// <param name="at">When it was read.</param>
    /// <param name="value">The reading, or nothing when there is none.</param>
    /// <param name="level">The level it must reach.</param>
    /// <param name="margin">How far below the level it must fall to re-arm.</param>
    /// <param name="hold">How long it must stay at or above the level.</param>
    /// <param name="clearFor">How long it must stay below the lower level to re-arm.</param>
    public bool Observe(DateTimeOffset at, double? value, double level, double margin, TimeSpan hold, TimeSpan clearFor)
    {
        if (value is not { } reading || !double.IsFinite(reading))
        {
            _over = null;
            _under = null;
            return false;
        }

        if (!HasFired)
        {
            if (reading < level)
            {
                _over = null;
                return false;
            }

            _over ??= at;

            if (at - _over < hold)
            {
                return false;
            }

            HasFired = true;
            _over = null;
            return true;
        }

        if (reading >= level - margin)
        {
            _under = null;
            return false;
        }

        _under ??= at;

        if (at - _under >= clearFor)
        {
            HasFired = false;
            _under = null;
        }

        return false;
    }

    /// <summary>Starts both clocks again, as after waking; a fired watch stays fired.</summary>
    public void Reset()
    {
        _over = null;
        _under = null;
    }
}

/// <summary>What is wrong with the hardware.</summary>
public enum HardwareProblem
{
    /// <summary>A graphics adapter is too hot.</summary>
    GraphicsHot,

    /// <summary>The processor is too hot.</summary>
    ProcessorHot,

    /// <summary>A drive is too hot.</summary>
    DriveHot,

    /// <summary>A volume is nearly full.</summary>
    DriveFull,

    /// <summary>A drive has used a share of its rated life.</summary>
    DriveWear,
}

/// <summary>One part of the machine past its level.</summary>
/// <param name="Problem">What is wrong.</param>
/// <param name="Subject">The part, as a person would name it: "NVIDIA GeForce RTX 4070", "Drive C:".</param>
/// <param name="Tag">What it is about, stable across runs, for the notice and its cooldown.</param>
/// <param name="Value">The reading: degrees, or percent.</param>
/// <param name="Level">The level it passed.</param>
public sealed record HardwareAlarm(HardwareProblem Problem, string Subject, string Tag, double Value, double Level)
{
    /// <summary>Bytes free, for a nearly full volume.</summary>
    public long? Free { get; init; }
}

/// <summary>
/// Watches the machine's readings for temperatures, fullness and wear past the
/// levels the person chose.
/// </summary>
/// <remarks>
/// <para>
/// A temperature must hold for <see cref="HotFor"/> (a drive for
/// <see cref="DriveHotFor"/>), so a moment's spike under load is not an alert,
/// and must fall <see cref="CoolerBy"/> degrees below the level for
/// <see cref="ClearFor"/> before it can fire again. Readings outside 1-150 C
/// are a misread field, not a temperature, and are ignored.
/// </para>
/// <para>
/// Fullness is judged per volume, since "C: is full" is what a person acts
/// on; wear per drive, once, since wear never goes back. Whether either is
/// said again on a later start is the alert kinds' cooldown, kept in the record.
/// </para>
/// </remarks>
public sealed class HardwareWatch
{
    /// <summary>How long a processor or graphics temperature must stay at its level.</summary>
    public static readonly TimeSpan HotFor = TimeSpan.FromSeconds(30);

    /// <summary>How long a drive temperature must stay at its level; drives warm and cool slowly.</summary>
    public static readonly TimeSpan DriveHotFor = TimeSpan.FromMinutes(1);

    /// <summary>How long a reading must stay back below before it can fire again.</summary>
    public static readonly TimeSpan ClearFor = TimeSpan.FromMinutes(2);

    /// <summary>How far below its level a temperature must fall to re-arm, in degrees.</summary>
    public const double CoolerBy = 5;

    /// <summary>How far below its level a volume must empty to re-arm, in percent.</summary>
    public const double EmptierBy = 2;

    private readonly Dictionary<string, LevelWatch> _watches = new(StringComparer.Ordinal);

    /// <summary>Takes one reading of the machine, and returns what fires now.</summary>
    public IReadOnlyList<HardwareAlarm> Observe(SystemSnapshot snapshot, HardwareAlertSettings rule)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rule);

        if (!snapshot.HasValue)
        {
            return [];
        }

        List<HardwareAlarm> alarms = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        DateTimeOffset at = snapshot.Taken;

        if (rule.GraphicsHot)
        {
            for (int index = 0; index < snapshot.Graphics.Count; index++)
            {
                GraphicsReading graphics = snapshot.Graphics[index];
                string key = string.Create(CultureInfo.InvariantCulture, $"graphics:{index}:{graphics.Name}");

                Watch(key, Celsius(graphics.Celsius), rule.GraphicsCelsius, CoolerBy, HotFor, () =>
                    new HardwareAlarm(HardwareProblem.GraphicsHot, Named(graphics.Name, "The graphics adapter"), key, graphics.Celsius ?? 0, rule.GraphicsCelsius));
            }
        }

#if !STORE
        // The Store edition cannot read the processor's temperature at all.
        if (rule.ProcessorHot)
        {
            Watch("processor", Celsius(snapshot.Processor.Celsius), rule.ProcessorCelsius, CoolerBy, HotFor, () =>
                new HardwareAlarm(HardwareProblem.ProcessorHot, "The processor", "processor", snapshot.Processor.Celsius ?? 0, rule.ProcessorCelsius));
        }
#endif

        foreach (DriveReading drive in snapshot.Drives)
        {
            // Watched by the drive's place, not its model: two drives of the same
            // model are two drives. The model is only for saying which.
            string name = Named(drive.Model, string.Create(CultureInfo.InvariantCulture, $"Drive {drive.Index}"));
            string drivePlace = string.Create(CultureInfo.InvariantCulture, $"{drive.Index}:{name}");

            if (rule.DriveHot)
            {
                string key = string.Concat("drive-hot:", drivePlace);

                Watch(key, Celsius(drive.Celsius), rule.DriveCelsius, CoolerBy, DriveHotFor, () =>
                    new HardwareAlarm(HardwareProblem.DriveHot, name, key, drive.Celsius ?? 0, rule.DriveCelsius));
            }

            if (rule.DriveWear && drive.LifeUsedPercent is { } used && used is >= 0 and <= 255)
            {
                string key = string.Create(CultureInfo.InvariantCulture, $"drive-wear:{drivePlace}:{rule.DriveWearPercent}");

                // Wear never goes back down, so the watch is never re-armed here.
                Watch(key, used, rule.DriveWearPercent, double.PositiveInfinity, TimeSpan.Zero, () =>
                    new HardwareAlarm(HardwareProblem.DriveWear, name, key, used, rule.DriveWearPercent));
            }

            if (rule.DriveFull)
            {
                foreach (VolumeReading volume in drive.Volumes)
                {
                    if (volume.Size <= 0)
                    {
                        continue;
                    }

                    string subject = string.IsNullOrWhiteSpace(volume.Letter)
                        ? Named(volume.Label, name)
                        : string.Concat("Drive ", volume.Letter.TrimEnd('\\'));
                    string key = string.IsNullOrWhiteSpace(volume.Letter)
                        ? string.Concat("drive-full:", drivePlace, ":", subject)
                        : string.Concat("drive-full:", subject);

                    Watch(key, volume.UsedPercent, rule.DriveFullPercent, EmptierBy, TimeSpan.FromMinutes(1), () =>
                        new HardwareAlarm(HardwareProblem.DriveFull, subject, key, volume.UsedPercent, rule.DriveFullPercent)
                        {
                            Free = volume.Free,
                        });
                }
            }
        }

        // A part gone, or a rule switched off, forgets its watch: on again, it starts fresh.
        foreach (string gone in _watches.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            _watches.Remove(gone);
        }

        return alarms;

        void Watch(string key, double? value, double level, double margin, TimeSpan hold, Func<HardwareAlarm> alarm)
        {
            seen.Add(key);

            if (!_watches.TryGetValue(key, out LevelWatch? watch))
            {
                watch = new LevelWatch();
                _watches[key] = watch;
            }

            if (watch.Observe(at, value, level, margin, hold, ClearFor))
            {
                alarms.Add(alarm());
            }
        }
    }

    /// <summary>Starts every clock again, as after waking.</summary>
    public void Reset()
    {
        foreach (LevelWatch watch in _watches.Values)
        {
            watch.Reset();
        }
    }

    /// <summary>A temperature worth believing, or nothing: outside 1-150 C is a misread field.</summary>
    public static double? Celsius(double? reading) =>
        reading is { } degrees && double.IsFinite(degrees) && degrees is >= 1 and <= 150 ? degrees : null;

    private static string Named(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
}
