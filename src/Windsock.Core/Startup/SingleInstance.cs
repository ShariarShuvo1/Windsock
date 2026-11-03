using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Windsock.Core.Navigation;

namespace Windsock.Core.Startup;

/// <summary>What a second Windsock asked the running one for.</summary>
public sealed class InstanceWakeEventArgs(string? link) : EventArgs
{
    /// <summary>The page it asked to be shown, by link name, or nothing for the window as it was.</summary>
    public string? Link { get; } = link;
}

/// <summary>
/// Makes sure only one Windsock is running for this user, and lets a second one
/// hand its request to the first.
/// </summary>
/// <remarks>
/// <para>
/// The request travels two ways. A named pipe carries it whole - "open
/// history" from a jump list task, a shortcut or <c>--open</c> on the command
/// line - so the window turns to the page asked for. The pipe is this user's
/// only (<see cref="PipeOptions.CurrentUserOnly"/>), and named for this session
/// as well as for the instance, since pipes, unlike the mutex, are not kept
/// apart per session. A wake event is the fallback when the pipe cannot be
/// reached in time, and shows the window as it was.
/// </para>
/// <para>
/// What arrives on the pipe is text from outside the process, so it is held to
/// "open" and one page name of plain lowercase letters, a hundred-odd bytes at
/// most, and anything else is ignored.
/// </para>
/// <para>
/// Nothing on the pipe can stop the hand-off. Two instances listen, each
/// giving a caller <see cref="PipePatience"/> to say its line, so a caller that
/// connects and says nothing holds one for two seconds while the other serves.
/// The running Windsock answers "ok"; a sender that hears nothing within
/// <see cref="PipePatience"/> all told - connecting, saying its line and
/// hearing the answer - sets the wake event instead.
/// </para>
/// <para>
/// While none of this Windsock's instances is open, the next one must be the
/// pipe's first (<see cref="PipeOptions.FirstPipeInstance"/>), so a pipe some
/// other program made under the name first is refused rather than joined.
/// Such a name is tried again after a wait that doubles up to half a minute,
/// and reported once (<see cref="HandOffTrouble"/>); the app runs on without
/// the hand-off, and the wake event still shows the window. What is left: a
/// program of the same user that holds the name before Windsock starts can
/// answer "ok" itself, and one that races to add an instance while one of
/// Windsock's is being made again can take a caller - both need a program
/// already running as the user, which has easier ways to get in the way.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private const string OpenWord = "open";
    private const string AckWord = "ok";
    private const int LongestMessage = 128;
    private const int ServerInstances = 2;

    /// <summary>
    /// How long a sender has, all told, to connect, say its line and hear the
    /// answer; and how long a listener gives a caller to say its line.
    /// </summary>
    internal static readonly TimeSpan PipePatience = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan FirstBackOff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LongestBackOff = TimeSpan.FromSeconds(30);
    /// <summary>Told to a Windsock started to take over from one already running.</summary>
    public const string ReplaceSwitch = "--replacing";

    /// <summary>
    /// How long a replacement waits for the Windsock it is replacing to go.
    /// </summary>
    public static readonly TimeSpan Handover = TimeSpan.FromSeconds(30);

    private readonly Mutex? _held;
    private readonly EventWaitHandle? _wake;

    private readonly EventWaitHandle _stop = new(false, EventResetMode.ManualReset);

    private readonly CancellationTokenSource _closing = new();
    private readonly string _pipe;

    // Making an instance and closing one happen under this lock, with the
    // count of this Windsock's instances open, so "one of ours is open" holds
    // for the whole of a make that relies on it.
    private readonly Lock _instances = new();

    private bool _owns;
    private Thread? _listener;
    private Task[] _pipeListeners = [];
    private int _openInstances;
    private int _troubleTold;
    private int _pipeAttempts;
    private bool _disposed;

    /// <summary>Claims the name, or discovers that something else holds it.</summary>
    public SingleInstance(string name, TimeSpan handover = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string shared = @"Local\" + name;
        _pipe = PipeNameFor(name);

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
            Refused = true;
        }
    }

    /// <summary>Whether this is the one that gets to run.</summary>
    public bool IsOnly { get; private set; }

    /// <summary>
    /// Whether the lock could not be opened at all: made by a Windsock running
    /// as administrator, or under another account's rights.
    /// </summary>
    public bool Refused { get; }

    /// <summary>
    /// Whether this process holds the lock. A replacement whose predecessor
    /// never let go within <see cref="Handover"/> runs without it.
    /// </summary>
    public bool OwnsLock => _owns;

    /// <summary>Raised off the UI thread when another Windsock asks to be seen, with the page it asked for.</summary>
    public event EventHandler<InstanceWakeEventArgs>? WakeRequested;

    /// <summary>
    /// Raised once, off the UI thread, when the hand-off pipe cannot be made -
    /// its name taken by some other program - with Windows' words for why. A
    /// second launch then shows the window as it was rather than a page.
    /// </summary>
    public event EventHandler<string>? HandOffTrouble;

    /// <summary>The pipe's name, for tests.</summary>
    internal string PipeName => _pipe;

    /// <summary>How many times a listening instance has been made or tried, for tests.</summary>
    internal int PipeAttempts => Volatile.Read(ref _pipeAttempts);

    /// <summary>Whether an instance of this Windsock's is open on the pipe, for tests.</summary>
    internal bool HoldsThePipe
    {
        get
        {
            lock (_instances)
            {
                return _openInstances > 0;
            }
        }
    }

    /// <summary>The pipe a Windsock of this name listens on in this session.</summary>
    internal static string PipeNameFor(string name) => string.Concat(name, "-", SessionOf(), "-open");

    /// <summary>Whether a command line asks to replace a Windsock already running.</summary>
    public static bool IsReplacing(IEnumerable<string>? arguments) =>
        CommandLine.Has(arguments, ReplaceSwitch);

    /// <summary>
    /// Asks the Windsock already running to show itself, turned to a page when
    /// one is named. Waits a moment for one that is still starting, and never
    /// longer than that. True when it answered; otherwise its show-yourself
    /// event is set instead, unless told not to.
    /// </summary>
    public bool Wake(string? link = null, bool signalIfUnanswered = true)
    {
        if (Send(link))
        {
            return true;
        }

        if (signalIfUnanswered)
        {
            Signal();
        }

        return false;
    }

    /// <summary>Sets the running Windsock's show-yourself event, which shows the window as it was.</summary>
    public void Signal() => _wake?.Set();

    /// <summary>
    /// After a hand-off nobody answered: waits up to <paramref name="wait"/>
    /// for the lock, in case its holder is on its way out - killed a moment
    /// ago, say, its pipe already closed but its process not yet gone - and
    /// becomes the one that runs if it is let go. True when this is now the
    /// only one.
    /// </summary>
    /// <remarks>
    /// Without this a Windsock started just as another ended found the lock
    /// still held, handed off to nobody and left, and no Windsock ran.
    /// </remarks>
    public bool TakeOver(TimeSpan wait)
    {
        if (IsOnly)
        {
            return true;
        }

        if (_held is null || _disposed)
        {
            return false;
        }

        if (!Await(wait))
        {
            return false;
        }

        // A start decides for itself what to show: a show-yourself left set
        // for the Windsock that went would open this one's window unasked.
        _wake?.Reset();
        _owns = true;
        IsOnly = true;
        return true;
    }

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
        _pipeListeners = [.. Enumerable.Range(0, ServerInstances).Select(_ => Task.Run(() => ServeAsync(_closing.Token)))];
    }

    /// <summary>
    /// Reads a message from the pipe: whether it is a request at all, and the
    /// page it names, if any. A name that cannot be a page is a plain wake.
    /// </summary>
    internal static bool TryRead(string? message, out string? link)
    {
        link = null;

        if (string.IsNullOrEmpty(message) || message.Length > LongestMessage)
        {
            return false;
        }

        string[] words = message.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length is 0 or > 2 || !string.Equals(words[0], OpenWord, StringComparison.Ordinal))
        {
            return false;
        }

        if (words.Length == 2 && PageLink.IsName(words[1]))
        {
            link = words[1];
        }

        return true;
    }

    /// <summary>The message a wake carries over the pipe.</summary>
    internal static string Message(string? link) =>
        PageLink.IsName(link) ? string.Concat(OpenWord, " ", link) : OpenWord;

    private static string SessionOf()
    {
        using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
        return self.SessionId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Says the line on the pipe and waits for "ok". False when nobody
    /// answered as a Windsock would, so the caller can fall back to the event.
    /// </summary>
    private bool Send(string? link)
    {
        // One deadline for the whole exchange, so a second launch waits at
        // most this long, however the time is split, before it falls back.
        using CancellationTokenSource patience = new(PipePatience);

        try
        {
            using NamedPipeClientStream client = new(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.ConnectAsync(patience.Token).GetAwaiter().GetResult();

            byte[] line = Encoding.UTF8.GetBytes(Message(link) + "\n");
            client.WriteAsync(line, patience.Token).AsTask().GetAwaiter().GetResult();
            client.FlushAsync(patience.Token).GetAwaiter().GetResult();

            byte[] answer = new byte[8];
            int read = ReadLineAsync(client, answer, patience.Token).GetAwaiter().GetResult();

            return string.Equals(Encoding.ASCII.GetString(answer, 0, read).Trim(), AckWord, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Reads up to a line's end, the stream's end or the buffer's; how many bytes came.</summary>
    private static async Task<int> ReadLineAsync(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        int read = 0;

        while (read < buffer.Length)
        {
            int got = await stream.ReadAsync(buffer.AsMemory(read), cancel).ConfigureAwait(false);

            if (got == 0)
            {
                break;
            }

            read += got;

            if (Array.IndexOf(buffer, (byte)'\n', read - got, got) >= 0)
            {
                break;
            }
        }

        return read;
    }

    /// <summary>
    /// One listening instance, made again after each caller. A caller has
    /// <see cref="PipePatience"/> to say its line; a name that cannot be made
    /// is tried again after a wait that doubles, and reported once.
    /// </summary>
    private async Task ServeAsync(CancellationToken closing)
    {
        byte[] buffer = new byte[LongestMessage + 1];
        TimeSpan wait = FirstBackOff;

        while (!closing.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            Interlocked.Increment(ref _pipeAttempts);

            try
            {
                server = MakeInstance();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The name is held by something that is not this Windsock - a
                // program that made the pipe first - or both instances are in
                // use. Say so once, and try again later rather than in a spin.
                if (Interlocked.Exchange(ref _troubleTold, 1) == 0)
                {
                    HandOffTrouble?.Invoke(this, error.Message);
                }

                if (!await PauseAsync(wait, closing).ConfigureAwait(false))
                {
                    return;
                }

                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestBackOff.Ticks));
                continue;
            }

            wait = FirstBackOff;

            try
            {
                await server.WaitForConnectionAsync(closing).ConfigureAwait(false);

                using CancellationTokenSource patience = CancellationTokenSource.CreateLinkedTokenSource(closing);
                patience.CancelAfter(PipePatience);

                int read = await ReadLineAsync(server, buffer, patience.Token).ConfigureAwait(false);

                if (read <= LongestMessage && TryRead(Encoding.UTF8.GetString(buffer, 0, read), out string? link))
                {
                    // Answered before anything is shown: a caller that never
                    // hears "ok" sets the wake event, and the window would
                    // otherwise be asked for twice.
                    await server.WriteAsync(Encoding.ASCII.GetBytes(AckWord + "\n"), patience.Token).ConfigureAwait(false);
                    await server.FlushAsync(patience.Token).ConfigureAwait(false);

                    WakeRequested?.Invoke(this, new InstanceWakeEventArgs(link));
                }
            }
            catch (OperationCanceledException) when (closing.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // One caller that said nothing in time, or went away: the
                // next one gets a fresh instance.
            }
            finally
            {
                Retire(server);
            }
        }
    }

    /// <summary>
    /// Makes one listening instance: the pipe's first while none of this
    /// Windsock's is open, so a pipe made first by anything else throws here
    /// instead of being joined.
    /// </summary>
    private NamedPipeServerStream MakeInstance()
    {
        lock (_instances)
        {
            PipeOptions options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

            if (_openInstances == 0)
            {
                options |= PipeOptions.FirstPipeInstance;
            }

            NamedPipeServerStream server = new(_pipe, PipeDirection.InOut, ServerInstances, PipeTransmissionMode.Byte, options);
            _openInstances++;
            return server;
        }
    }

    private void Retire(NamedPipeServerStream server)
    {
        lock (_instances)
        {
            server.Dispose();
            _openInstances--;
        }
    }

    private static async Task<bool> PauseAsync(TimeSpan wait, CancellationToken closing)
    {
        try
        {
            await Task.Delay(wait, closing).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
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

            WakeRequested?.Invoke(this, new InstanceWakeEventArgs(null));
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
        _closing.Cancel();
        _listener?.Join(TimeSpan.FromSeconds(1));

        try
        {
            _ = Task.WaitAll(_pipeListeners, TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // How the listeners ended is not the disposer's to report.
        }

        if (_owns)
        {
            _held?.ReleaseMutex();
        }

        _held?.Dispose();
        _wake?.Dispose();
        _stop.Dispose();
        _closing.Dispose();
    }
}
