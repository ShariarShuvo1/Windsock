using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Windsock.Core.Security;

/// <summary>A file as it is on disk now: a changed file is checked again.</summary>
/// <param name="Length">Its size in bytes.</param>
/// <param name="WrittenTicks">When it was last written, in UTC ticks.</param>
public readonly record struct FileStamp(long Length, long WrittenTicks)
{
    /// <summary>The stamp of a file, or null when it cannot be read.</summary>
    public static FileStamp? Of(string path)
    {
        try
        {
            FileInfo file = new(path);
            return file.Exists ? new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>Says a file's signature has been checked.</summary>
public sealed class SignatureCheckedEventArgs(string path, SignatureInfo signature) : EventArgs
{
    /// <summary>The file's path, as it was asked for.</summary>
    public string Path { get; } = path;

    /// <summary>What the check found.</summary>
    public SignatureInfo Signature { get; } = signature;
}

/// <summary>
/// Remembers each program's signature, checked once per version of the file,
/// in the background and one file at a time.
/// </summary>
/// <remarks>
/// <para>
/// A check hashes the whole file, which takes milliseconds for most programs
/// (3 to 42 ms on the development machine) and seconds for a large one, so a
/// page asks <see cref="Find"/>, shows nothing yet, and redraws on
/// <see cref="Checked"/>. A file is known by its path, size and last write
/// time; an update to the program is checked again.
/// </para>
/// <para>
/// The worker runs below normal priority and only while there is something
/// to check: it ends when the queue is empty and a new one starts with the
/// next file queued, both under one lock, so none is ever left waiting. A
/// thread that waited instead kept the last handler it called on its stack,
/// and with it whatever page had subscribed - a closed main window and its
/// whole scope, measured in the live check of 2026-10-05.
/// </para>
/// </remarks>
public sealed class SignatureBook : IDisposable
{
    private const int Capacity = 4096;
    private const int QueueLimit = 1024;

    private readonly ISignatureVerifier _verifier;
    private readonly Func<string, FileStamp?> _stamp;
    private readonly ConcurrentDictionary<string, Entry> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _work = new();
    private readonly Lock _gate = new();
    private Thread? _worker;
    private volatile bool _disposed;

    public SignatureBook(ISignatureVerifier verifier)
        : this(verifier, FileStamp.Of)
    {
    }

    internal SignatureBook(ISignatureVerifier verifier, Func<string, FileStamp?> stamp)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(stamp);

        _verifier = verifier;
        _stamp = stamp;
    }

    /// <summary>Raised on the worker's thread when a file has been checked.</summary>
    public event EventHandler<SignatureCheckedEventArgs>? Checked;

    /// <summary>
    /// The signature of a file, when it has been checked and has not changed
    /// since; otherwise null, and a check is queued.
    /// </summary>
    public SignatureInfo? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || _disposed)
        {
            return null;
        }

        FileStamp? stamp = _stamp(path);

        if (stamp is null)
        {
            return SignatureInfo.Unreadable();
        }

        if (_known.TryGetValue(path, out Entry? entry) && entry.Stamp == stamp)
        {
            return entry.Signature;
        }

        Queue(path);
        return null;
    }

    /// <summary>
    /// The signature of a file, checked now on the calling thread when it is
    /// not already known. For a background service, never the UI thread.
    /// </summary>
    public SignatureInfo Check(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FileStamp? stamp = _stamp(path);

        if (stamp is null)
        {
            return SignatureInfo.Unreadable();
        }

        if (_known.TryGetValue(path, out Entry? entry) && entry.Stamp == stamp)
        {
            return entry.Signature;
        }

        SignatureInfo signature = _verifier.Verify(path);
        Remember(path, stamp.Value, signature);
        return signature;
    }

    /// <summary>Whether a worker is running now. For tests.</summary>
    internal bool IsWorking
    {
        get
        {
            lock (_gate)
            {
                return _worker is not null;
            }
        }
    }

    private void Queue(string path)
    {
        if (!_queued.TryAdd(path, 0))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _work.Count >= QueueLimit)
            {
                _queued.TryRemove(path, out _);
                return;
            }

            _work.Enqueue(path);

            // Started under the same lock the worker ends under, so a file
            // queued as the last worker finds the queue empty starts a new one.
            if (_worker is null)
            {
                _worker = new Thread(Work)
                {
                    IsBackground = true,
                    Name = "Windsock-signatures",
                    Priority = ThreadPriority.BelowNormal,
                };

                _worker.Start();
            }
        }
    }

    private void Work()
    {
        while (true)
        {
            string path;

            lock (_gate)
            {
                if (_disposed || !_work.TryDequeue(out string? next))
                {
                    _worker = null;
                    return;
                }

                path = next;
            }

            _queued.TryRemove(path, out _);
            Raise(path, Check(path));
        }
    }

    /// <summary>
    /// Raises <see cref="Checked"/> from a frame of its own, so the handler is
    /// held only for the call and never by the worker's loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Raise(string path, SignatureInfo signature)
    {
        EventHandler<SignatureCheckedEventArgs>? handler = Checked;
        handler?.Invoke(this, new SignatureCheckedEventArgs(path, signature));
    }

    private void Remember(string path, FileStamp stamp, SignatureInfo signature)
    {
        if (_known.Count >= Capacity && !_known.ContainsKey(path))
        {
            _known.Clear();
        }

        _known[path] = new Entry(stamp, signature);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Thread? worker;

        lock (_gate)
        {
            _disposed = true;
            _work.Clear();
            worker = _worker;
        }

        worker?.Join(TimeSpan.FromSeconds(1));
    }

    private sealed record Entry(FileStamp Stamp, SignatureInfo Signature);
}
