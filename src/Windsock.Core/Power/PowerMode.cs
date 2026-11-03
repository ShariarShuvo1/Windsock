namespace Windsock.Core.Power;

/// <summary>
/// Windows' effective power mode: <c>EFFECTIVE_POWER_MODE</c> in
/// <c>powersetting.h</c>, with the values the header gives them.
/// </summary>
/// <remarks>
/// "Effective" is the word that matters: it is the slider in Settings with
/// battery saver and game mode laid over it, not the slider alone.
/// </remarks>
public enum PowerMode
{
    /// <summary><c>EffectivePowerModeBatterySaver</c>: battery (energy) saver is on.</summary>
    Saver = 0,

    /// <summary><c>EffectivePowerModeBetterBattery</c>: "Best power efficiency".</summary>
    Efficiency = 1,

    /// <summary><c>EffectivePowerModeBalanced</c>.</summary>
    Balanced = 2,

    /// <summary>
    /// <c>EffectivePowerModeHighPerformance</c>: only reported under the old
    /// High performance plan's overlay.
    /// </summary>
    HighPerformance = 3,

    /// <summary><c>EffectivePowerModeMaxPerformance</c>: "Best performance".</summary>
    MaxPerformance = 4,

    /// <summary><c>EffectivePowerModeGameMode</c>: a game has the machine.</summary>
    GameMode = 5,

    /// <summary><c>EffectivePowerModeMixedReality</c>: a headset has the machine.</summary>
    MixedReality = 6,
}

/// <summary>Where the machine's power comes from.</summary>
public enum PowerSource
{
    Unknown,

    Mains,

    Battery,
}

/// <summary>The machine's power, as Windows has it set.</summary>
/// <param name="Source">Mains or battery.</param>
/// <param name="Mode">The effective power mode, or nothing until Windows has said.</param>
/// <param name="SaverOn">Whether battery (energy) saver is on.</param>
public sealed record PowerReading(PowerSource Source, PowerMode? Mode, bool SaverOn)
{
    /// <summary>Nothing read yet.</summary>
    public static PowerReading Unknown { get; } = new(PowerSource.Unknown, null, false);

    /// <summary>Builds a reading from Windows' power summary and the mode last reported.</summary>
    public static PowerReading From(SystemPowerStatus system, PowerMode? mode) => new(
        system.OnMains switch
        {
            true => PowerSource.Mains,
            false => PowerSource.Battery,
            null => PowerSource.Unknown,
        },
        mode,
        system.SaverOn || mode == PowerMode.Saver);
}

/// <summary>Power modes in the words Windows' own Settings uses.</summary>
public static class PowerModeNames
{
    /// <summary>
    /// The first build that calls battery saver "Energy saver": Windows 11
    /// 24H2, where it also became available on machines with no battery.
    /// </summary>
    public const int EnergySaverBuild = 26100;

    /// <summary>What battery saver is called on this build.</summary>
    public static string Saver(int build) => build >= EnergySaverBuild ? "Energy saver" : "Battery saver";

    /// <summary>A mode in words, for a build of Windows.</summary>
    public static string Describe(PowerMode mode, int build) => mode switch
    {
        PowerMode.Saver => Saver(build),
        PowerMode.Efficiency => "Best power efficiency",
        PowerMode.Balanced => "Balanced",
        PowerMode.HighPerformance => "Better performance",
        PowerMode.MaxPerformance => "Best performance",
        PowerMode.GameMode => "Game mode",
        PowerMode.MixedReality => "Mixed reality",
        _ => "Unknown",
    };

    /// <summary>A mode in words, for the build this process runs on.</summary>
    public static string Describe(PowerMode mode) => Describe(mode, Environment.OSVersion.Version.Build);
}
