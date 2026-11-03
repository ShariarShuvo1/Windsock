using Windsock.Core.Plans;

namespace Windsock.Core.Settings;

/// <summary>What the person said about one network.</summary>
public sealed class NetworkChoice
{
    /// <summary>The network's key (see <see cref="Networking.NetworkTag.Key"/>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Their own name for it, or null to use Windows' name.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Whether it is a phone's hotspot: true or false when they said so, null
    /// to let Windows' metered flag decide.
    /// </summary>
    public bool? Hotspot { get; set; }
}

/// <summary>How often scheduled export writes a file.</summary>
public enum ExportEvery
{
    /// <summary>One file a day.</summary>
    Day,

    /// <summary>One file a month.</summary>
    Month,
}

/// <summary>What scheduled export writes.</summary>
public enum ExportFormat
{
    /// <summary>Network use a minute at a time, which History can read back in.</summary>
    Csv,

    /// <summary>Everything History holds, by the hour: network, apps, the machine, the connection, notes.</summary>
    Json,
}

/// <summary>Writing each finished day or month of history to a folder by itself.</summary>
public sealed class AutoExportSettings
{
    /// <summary>Whether it happens.</summary>
    public bool Enabled { get; set; }

    /// <summary>The folder files go to; must be a full path.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>How much each file holds.</summary>
    public ExportEvery Every { get; set; } = ExportEvery.Day;

    /// <summary>What kind of file.</summary>
    public ExportFormat Format { get; set; } = ExportFormat.Csv;
}

/// <summary>A copy of the history file kept beside it, made each week.</summary>
public sealed class BackupSettings
{
    /// <summary>The fewest copies kept.</summary>
    public const int FewestKept = 1;

    /// <summary>The most copies kept.</summary>
    public const int MostKept = 12;

    /// <summary>Whether copies are made.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many weekly copies are kept; the oldest goes when a new one is made.</summary>
    public int Keep { get; set; } = 3;

    /// <summary><see cref="Keep"/>, held to what is sensible.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int KeepHeld => Math.Clamp(Keep, FewestKept, MostKept);
}

/// <summary>A short notice once a week of what the week used.</summary>
public sealed class DigestSettings
{
    /// <summary>Whether it is sent. Off until asked for: a notice nobody asked for is noise.</summary>
    public bool Enabled { get; set; }

    /// <summary>The day a week starts on, or null for the one the region uses.</summary>
    public DayOfWeek? WeekStarts { get; set; }
}

/// <summary>
/// Data plans, the person's names and markings for networks, and what is done
/// with history by itself: export, backup and the weekly digest.
/// </summary>
public sealed class UsageSettings
{
    private List<DataPlan> _plans = [];
    private List<NetworkChoice> _networks = [];
    private AutoExportSettings _autoExport = new();
    private BackupSettings _backup = new();
    private DigestSettings _digest = new();

    /// <summary>
    /// The data plans. Replace the list rather than changing it in place, then
    /// ask <see cref="PlanMonitor.Refresh"/>: the monitor reads it on its own thread.
    /// </summary>
    public List<DataPlan> Plans
    {
        get => _plans;
        set => _plans = value ?? [];
    }

    /// <summary>What the person said about networks. Replace rather than change in place, as for plans.</summary>
    public List<NetworkChoice> Networks
    {
        get => _networks;
        set => _networks = value ?? [];
    }

    /// <summary>Scheduled export.</summary>
    public AutoExportSettings AutoExport
    {
        get => _autoExport;
        set => _autoExport = value ?? new AutoExportSettings();
    }

    /// <summary>Backups of the history file.</summary>
    public BackupSettings Backup
    {
        get => _backup;
        set => _backup = value ?? new BackupSettings();
    }

    /// <summary>The weekly digest.</summary>
    public DigestSettings Digest
    {
        get => _digest;
        set => _digest = value ?? new DigestSettings();
    }

    /// <summary>What the person said about a network, or null when they said nothing.</summary>
    public NetworkChoice? ChoiceFor(string? networkKey)
    {
        if (networkKey is null)
        {
            return null;
        }

        foreach (NetworkChoice choice in _networks)
        {
            if (string.Equals(choice.Key, networkKey, StringComparison.Ordinal))
            {
                return choice;
            }
        }

        return null;
    }
}
