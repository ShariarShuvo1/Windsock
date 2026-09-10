namespace Windsock.Core.Hardware;

/// <summary>
/// What a graphics counter instance name says.
/// </summary>
public readonly record struct GpuEngine(int ProcessId, string Luid, string Engine);

/// <summary>
/// Reads the names Windows files graphics counters under.
/// </summary>
public static class GpuEngineName
{
    private const string ProcessMark = "pid_";
    private const string AdapterMark = "_luid_";
    private const string PhysicalMark = "_phys_";
    private const string KindMark = "_engtype_";

    /// <summary>Reads an engine counter instance name.</summary>
    public static GpuEngine? Parse(string? instance)
    {
        if (string.IsNullOrEmpty(instance) || !instance.StartsWith(ProcessMark, StringComparison.Ordinal))
        {
            return null;
        }

        int adapter = instance.IndexOf(AdapterMark, StringComparison.Ordinal);

        if (adapter < 0)
        {
            return null;
        }

        ReadOnlySpan<char> process = instance.AsSpan(ProcessMark.Length, adapter - ProcessMark.Length);

        if (!int.TryParse(process, out int processId))
        {
            return null;
        }

        return new GpuEngine(processId, Adapter(instance, adapter), Kind(instance));
    }

    /// <summary>Reads the adapter out of a memory counter instance name.</summary>
    public static string Adapter(string? instance)
    {
        if (string.IsNullOrEmpty(instance))
        {
            return string.Empty;
        }

        const string mark = "luid_";

        return instance.StartsWith(mark, StringComparison.Ordinal)
            ? Adapter(instance, mark.Length - AdapterMark.Length)
            : string.Empty;
    }

    private static string Adapter(string instance, int at)
    {
        int from = at + AdapterMark.Length;

        if (from >= instance.Length)
        {
            return string.Empty;
        }

        int until = instance.IndexOf(PhysicalMark, from, StringComparison.Ordinal);

        return until < 0
            ? instance[from..]
            : instance[from..until];
    }

    private static string Kind(string instance)
    {
        int at = instance.IndexOf(KindMark, StringComparison.Ordinal);

        return at < 0 ? string.Empty : instance[(at + KindMark.Length)..];
    }

    /// <summary>
    /// An engine kind in the words a reader would use.
    /// </summary>
    public static string Describe(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return kind.ToUpperInvariant() switch
        {
            "3D" => "3D",
            "COPY" => "Copy",
            "COMPUTE" => "Compute",
            "VIDEODECODE" => "Video decode",
            "VIDEOENCODE" => "Video encode",
            "VIDEOPROCESSING" => "Video processing",
            "SECURITY" => "Security",
            "VR" => "VR",
            "" => "Other",
            _ => Spaced(kind),
        };
    }

    private static string Spaced(string kind)
    {
        System.Text.StringBuilder spaced = new(kind.Length + 4);

        for (int at = 0; at < kind.Length; at++)
        {
            if (at > 0 && char.IsUpper(kind[at]) && !char.IsUpper(kind[at - 1]))
            {
                spaced.Append(' ').Append(char.ToLowerInvariant(kind[at]));
                continue;
            }

            spaced.Append(at == 0 ? char.ToUpperInvariant(kind[at]) : kind[at]);
        }

        return spaced.ToString();
    }
}
