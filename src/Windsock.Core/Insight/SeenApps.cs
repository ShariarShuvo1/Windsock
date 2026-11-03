using System.Text;

namespace Windsock.Core.Insight;

/// <summary>
/// The apps Windsock has seen use the network, by the id Windows' data usage
/// gives them, kept in a small file of their own.
/// </summary>
/// <remarks>
/// History knows every app Windows counted, but only for as long as history
/// is kept and only after Windows publishes a figure; this list holds every
/// app Windsock itself has seen connect since it started keeping it, so an app
/// is called new once. One id a line, written whole each time through a
/// temporary file.
/// </remarks>
public sealed class SeenApps
{
    private readonly string _path;
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Reads the list, or starts an empty one when there is none.</summary>
    public SeenApps(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;

        try
        {
            if (File.Exists(path))
            {
                Existed = true;

                foreach (string line in File.ReadLines(path, Encoding.UTF8))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _ids.Add(line.Trim());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable list is a fresh one: the watcher learns again
            // quietly rather than calling everything new.
            Existed = false;
        }
    }

    /// <summary>
    /// Whether the list was there when this ran. A first run learns what is
    /// already in use without calling any of it new.
    /// </summary>
    public bool Existed { get; private set; }

    /// <summary>How many apps are known.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _ids.Count;
            }
        }
    }

    /// <summary>Whether an app has been seen.</summary>
    public bool Contains(string id)
    {
        lock (_gate)
        {
            return _ids.Contains(id);
        }
    }

    /// <summary>Notes an app; true when it was not known.</summary>
    public bool Add(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        lock (_gate)
        {
            return _ids.Add(id);
        }
    }

    /// <summary>Notes every app given; returns how many were not known.</summary>
    public int AddRange(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        int added = 0;

        lock (_gate)
        {
            foreach (string id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id) && _ids.Add(id))
                {
                    added++;
                }
            }
        }

        return added;
    }

    /// <summary>Forgets every app, and the file, as erasing all history does.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _ids.Clear();
            Existed = false;

            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Writes the list. False when the file could not be written; the list in memory stands.</summary>
    public bool Save()
    {
        string[] lines;

        lock (_gate)
        {
            lines = [.. _ids.Order(StringComparer.OrdinalIgnoreCase)];
        }

        try
        {
            string? directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = _path + ".tmp";
            File.WriteAllLines(temporary, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, _path, overwrite: true);

            lock (_gate)
            {
                Existed = true;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
