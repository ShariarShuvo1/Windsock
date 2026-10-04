using Windsock.Core.Processes;

namespace Windsock.Core.Apps;

/// <summary>
/// Says which app a running process belongs to, the way Windows counts it.
/// </summary>
/// <remarks>
/// <para>
/// Windows' data usage counts a program by its file and a packaged app by its
/// package, never a process. The Apps page, the network map and the details
/// window gather processes into apps by the same rule, so a row or a node stands
/// for exactly what Windows has a figure for: every process running one file,
/// or every process running a file one package installed.
/// </para>
/// <para>
/// A process whose file cannot be read is gathered with others of its name,
/// which is the most that can be said about it; Windows' figure is never
/// matched to such an app.
/// </para>
/// </remarks>
public static class RunningApps
{
    /// <summary>Compares app keys; paths and package names are not case-sensitive.</summary>
    public static StringComparer KeyComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>The app a process belongs to, as a key two processes share exactly when they are one app.</summary>
    public static string KeyOf(ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (identity.PackageFamily is { Length: > 0 } family)
        {
            return "package:" + family;
        }

        return identity.FilePath is { Length: > 0 } path
            ? "program:" + path
            : "process:" + identity.Name;
    }

    /// <summary>
    /// The app of every process in a reading, by process id.
    /// </summary>
    /// <remarks>
    /// Windows will not let an ordinary user read the file of some protected
    /// processes - a few of the service hosts, typically - while it reads others
    /// of the same program. Keyed alone, those would stand as an app of their
    /// own beside the program they are. So a process whose file is unknown
    /// joins the one program in the reading whose file is named like it; with
    /// no such program, or more than one, it stays gathered by its name.
    /// </remarks>
    public static Dictionary<int, string> KeysFor(IEnumerable<ProcessUsage> usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        List<ProcessUsage> all = [.. usage];
        Dictionary<string, HashSet<string>> programsByName = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProcessUsage each in all)
        {
            if (each.Identity.PackageFamily is null && each.Identity.FilePath is { Length: > 0 } path)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(path);

                if (!programsByName.TryGetValue(name, out HashSet<string>? keys))
                {
                    keys = new HashSet<string>(KeyComparer);
                    programsByName[name] = keys;
                }

                keys.Add(KeyOf(each.Identity));
            }
        }

        Dictionary<int, string> result = new(all.Count);

        foreach (ProcessUsage each in all)
        {
            string key = KeyOf(each.Identity);

            if (KindOf(key) == AppKind.Unattributed
                && programsByName.TryGetValue(each.Identity.Name, out HashSet<string>? candidates)
                && candidates.Count == 1)
            {
                key = candidates.First();
            }

            result[each.ProcessId] = key;
        }

        return result;
    }

    /// <summary>What sort of app a key stands for.</summary>
    public static AppKind KindOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key.StartsWith("package:", StringComparison.Ordinal) ? AppKind.Package
            : key.StartsWith("program:", StringComparison.Ordinal) ? AppKind.Program
            : AppKind.Unattributed;
    }

    /// <summary>Whether Windows can have a figure for the app a key stands for.</summary>
    public static bool IsCountable(string key) => KindOf(key) != AppKind.Unattributed;

    /// <summary>
    /// The package family name of a package full name:
    /// <c>Name_Version_Architecture_ResourceId_PublisherId</c> gives
    /// <c>Name_PublisherId</c>. Null for anything not shaped like one.
    /// </summary>
    /// <remarks>
    /// A package name may not contain an underscore, and the resource id is
    /// often empty, so a full name always splits into exactly five parts.
    /// </remarks>
    public static string? FamilyOf(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);

        string[] parts = fullName.Split('_');

        return parts.Length == 5 && parts[0].Length > 0 && parts[4].Length > 0
            ? string.Concat(parts[0], "_", parts[4])
            : null;
    }
}
