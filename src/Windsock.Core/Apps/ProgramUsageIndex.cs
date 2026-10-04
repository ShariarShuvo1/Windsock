namespace Windsock.Core.Apps;

/// <summary>
/// Finds what Windows counted for the program a process runs, from the file
/// the process was started from.
/// </summary>
/// <remarks>
/// Windows counts a program by its executable and a packaged app by its
/// package, never by process. So a figure found here belongs to every process
/// of that program together - which is why the Apps page shows it only on a
/// row that stands for the whole program, and never shares it out between
/// processes it cannot tell apart.
/// </remarks>
public sealed class ProgramUsageIndex
{
    private readonly Dictionary<string, AppUsageRecord> _programs;
    private readonly List<(string Folder, AppUsageRecord Record)> _packages;

    internal ProgramUsageIndex(
        IEnumerable<(string Path, AppUsageRecord Record)> programs,
        IEnumerable<(string Folder, AppUsageRecord Record)> packages)
    {
        _programs = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

        foreach ((string path, AppUsageRecord record) in programs)
        {
            _programs.TryAdd(path, record);
        }

        _packages = [.. packages
            .Select(package => (Folder: package.Folder.TrimEnd('\\') + "\\", package.Record))
            .OrderByDescending(package => package.Folder.Length)];
    }

    /// <summary>An index with nothing in it.</summary>
    public static ProgramUsageIndex Empty { get; } = new([], []);

    /// <summary>Indexes one window's records by the files and folders they belong to.</summary>
    public static ProgramUsageIndex Build(IReadOnlyDictionary<string, AppUsageRecord> records, AppNames names)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(names);

        List<(string, AppUsageRecord)> programs = [];
        List<(string, AppUsageRecord)> packages = [];

        foreach ((string id, AppUsageRecord record) in records)
        {
            AppDescription app = names.Describe(id);

            if (app.Path is null)
            {
                continue;
            }

            if (app.Kind == AppKind.Program)
            {
                programs.Add((app.Path, record));

                if (AsSeenByThePackage(
                        app.Path,
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify)) is { } seen)
                {
                    programs.Add((seen, record));
                }
            }
            else if (app.Kind == AppKind.Package)
            {
                packages.Add((app.Path, record));
            }
        }

        return new ProgramUsageIndex(programs, packages);
    }

    /// <summary>
    /// The path a program in a packaged app's redirected folders goes by from
    /// inside, or null when the path is not in one.
    /// </summary>
    /// <remarks>
    /// A packaged app that writes to AppData writes, in fact, to
    /// <c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Roaming</c>, and a program
    /// it starts from there runs under the path the app sees. Measured on
    /// 2026-09-15: Claude Code, started by the Claude desktop app, reported
    /// <c>C:\Users\...\AppData\Roaming\Claude\claude-code\2.1.270\claude.exe</c>,
    /// and Windows counted its traffic under
    /// <c>...\packages\claude_pzs8sxrjxfjjc\localcache\roaming\claude\...</c>, so the
    /// two never met and the program showed a dash with traffic moving.
    /// </remarks>
    internal static string? AsSeenByThePackage(string path, string roaming, string local)
    {
        ArgumentNullException.ThrowIfNull(path);

        const string Packages = "\\Packages\\";
        const string LocalCache = "\\LocalCache\\";

        if (string.IsNullOrEmpty(local) || !path.StartsWith(local.TrimEnd('\\') + Packages, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // LocalCache directly under the package's own folder, and nowhere else.
        int family = local.TrimEnd('\\').Length + Packages.Length;
        int cache = path.IndexOf('\\', family);

        if (cache <= family || string.Compare(path, cache, LocalCache, 0, LocalCache.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return null;
        }

        string inside = path[(cache + LocalCache.Length)..];
        int slash = inside.IndexOf('\\', StringComparison.Ordinal);

        if (slash <= 0)
        {
            return null;
        }

        string folder = inside[..slash];
        string rest = inside[(slash + 1)..];

        string? root = folder.ToUpperInvariant() switch
        {
            "ROAMING" => roaming,
            "LOCAL" => local,
            _ => null,
        };

        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, rest);
    }

    /// <summary>Whether anything was counted at all.</summary>
    public bool IsEmpty => _programs.Count == 0 && _packages.Count == 0;

    /// <summary>What Windows counted for the program at a path, if anything.</summary>
    public bool TryFind(string? path, out AppUsageRecord record)
    {
        if (string.IsNullOrEmpty(path))
        {
            record = default;
            return false;
        }

        if (_programs.TryGetValue(path, out record))
        {
            return true;
        }

        foreach ((string folder, AppUsageRecord found) in _packages)
        {
            if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                record = found;
                return true;
            }
        }

        record = default;
        return false;
    }
}
