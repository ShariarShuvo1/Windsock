namespace Windsock.Core.Startup;

/// <summary>
/// Makes sure only one Windsock is running for this user, and lets a second one
/// hand its request to the first.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Told to a Windsock started to take over from one already running.</summary>
    public const string ReplaceSwitch = "--replacing";

    /// <summary>
    /// How long a replacement waits for the Windsock it is replacing to go.
    /// </summary>
    public static readonly TimeSpan Handover = TimeSpan.FromSeconds(30);

    private readonly Mutex? _held;
    private readonly EventWaitHandle? _wake;

    private readonly EventWaitHandle _stop = new(false, EventResetMode.ManualReset);

    private readonly bool _owns;
    private Thread? _listener;
    private bool _disposed;

    /// <summary>Claims the name, or discovers that something else holds it.</summary>
    public SingleInstance(string name, TimeSpan handover = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string shared = @"Local\" + name;

        try
        {
            _held = new Mutex(initiallyOwned: true, shared, out bool taken);
            _wake = new EventWaitHandle(false, EventResetMode.AutoReset, shared + "-wake");

            _owns = taken;
            IsOnly = taken;

            if (!taken && handover > TimeSpan.Zero)
            {
                _owns = Await(handover);
                IsOnly = true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            IsOnly = false;
        }
    }

    /// <summary>Whether this is the one that gets to run.</summary>
    public bool IsOnly { get; }

    /// <summary>Raised off the UI thread when another Windsock asks to be seen.</summary>
    public event EventHandler? WakeRequested;

    /// <summary>Whether a command line asks to replace a Windsock already running.</summary>
    public static bool IsReplacing(IEnumerable<string>? arguments) =>
        CommandLine.Has(arguments, ReplaceSwitch);

    /// <summary>
    /// Asks the Windsock already running to show itself.
    /// </summary>
    public void Wake() => _wake?.Set();

    /// <summary>Starts listening for a second Windsock asking to be let in.</summary>
    public void Listen()
    {
        if (!IsOnly || _wake is null || _listener is not null || _disposed)
        {
            return;
        }
        _listener = new Thread(Wait)
        {
            IsBackground = true,
            Name = "Windsock instance listener",
        };

        _listener.Start();
    }

    private bool Await(TimeSpan handover)
    {
        try
        {
            return _held!.WaitOne(handover);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private void Wait()
    {
        WaitHandle[] both = [_wake!, _stop];

        while (true)
        {
            if (WaitHandle.WaitAny(both) != 0)
            {
                return;
            }

            WakeRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Set();
        _listener?.Join(TimeSpan.FromSeconds(1));
        if (_owns)
        {
            _held?.ReleaseMutex();
        }

        _held?.Dispose();
        _wake?.Dispose();
        _stop.Dispose();
    }
}
