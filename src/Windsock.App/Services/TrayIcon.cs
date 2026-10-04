using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Windsock.App.Services;

/// <summary>
/// An icon in the notification area, and the way back to a window that is not
/// open.
/// </summary>
/// <remarks>
/// <para>
/// The icon hangs off a hidden window of its own. It used to borrow the main
/// window's handle, and a window that has never been shown has no handle, so
/// a Windsock started at sign-in - which opens no window - put up no icon at
/// all. It also means the main window can be closed outright rather than
/// hidden, and the icon stays.
/// </para>
/// <para>
/// That window is a real top-level one, never shown, rather than message-only:
/// Explorer announces a rebuilt taskbar with a broadcast, and a message-only
/// window does not receive broadcasts. Without that the icon would vanish for
/// good the first time Explorer restarted.
/// </para>
/// </remarks>
public sealed unsafe partial class TrayIcon : IDisposable
{
    // WM_APP + 1. The shell sends the icon's mouse events back as this.
    private const uint Callback = 0x8000 + 1;

    private const uint LeftButtonUp = 0x0202;
    private const uint LeftDoubleClick = 0x0203;
    private const uint RightButtonUp = 0x0205;

    private const uint Add = 0x00000000;
    private const uint Modify = 0x00000001;
    private const uint Delete = 0x00000002;

    private const uint HasMessage = 0x00000001;
    private const uint HasIcon = 0x00000002;
    private const uint HasTip = 0x00000004;
    private const uint HasInfo = 0x00000010;

    // Balloon flags: the app's own picture, large, silent when asked, and held
    // back while Windows is in quiet hours or a presentation.
    private const uint InfoUserIcon = 0x00000004;
    private const uint InfoNoSound = 0x00000010;
    private const uint InfoLargeIcon = 0x00000020;
    private const uint InfoRespectQuietTime = 0x00000080;

    // What the shell sends back about a balloon, in the callback's lParam.
    private const uint BalloonHide = 0x0403;
    private const uint BalloonTimeout = 0x0404;
    private const uint BalloonClick = 0x0405;

    private const uint Id = 1;

    private const int ToolWindow = 0x00000080;
    private const uint AllowMessage = 1;

    /// <summary>
    /// How long to wait before asking again when the shell refuses the icon,
    /// which it does when Windsock starts before the taskbar is ready.
    /// </summary>
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _retry;
    private readonly uint _taskbarCreated;

    private HwndSource? _source;
    private HICON _icon;
    private HICON _largeIcon;
    private string _tooltip = "Windsock";
    private bool _wanted;
    private bool _lending;
    private bool _shown;
    private bool _disposed;

    public TrayIcon(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _retry = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Retry };
        _retry.Tick += (_, _) => Put();
    }

    /// <summary>Raised when the icon is clicked.</summary>
    public event EventHandler? Opened;

    /// <summary>Raised when the icon is right-clicked.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>Raised when a balloon shown by <see cref="Announce"/> is clicked.</summary>
    public event EventHandler? AnnouncementClicked;

    /// <summary>Whether the icon is in the notification area.</summary>
    public bool IsShown => _shown;

    /// <summary>
    /// Puts the icon in the notification area, and keeps it there until
    /// <see cref="Hide"/>, including across a restart of Explorer.
    /// </summary>
    public void Show(string tooltip)
    {
        ArgumentNullException.ThrowIfNull(tooltip);

        if (_disposed)
        {
            return;
        }

        _tooltip = tooltip;
        _wanted = true;

        if (_shown)
        {
            NotifyIconData data = Describe();
            data.Flags = HasTip;
            _ = Shell_NotifyIconW(Modify, ref data);
            return;
        }

        Put();
    }

    /// <summary>
    /// Changes what the icon says on hover, without showing or hiding it.
    /// </summary>
    public void SetTooltip(string tooltip)
    {
        ArgumentNullException.ThrowIfNull(tooltip);

        if (_disposed || tooltip == _tooltip)
        {
            return;
        }

        _tooltip = tooltip;

        if (_shown)
        {
            NotifyIconData data = Describe();
            data.Flags = HasTip;
            _ = Shell_NotifyIconW(Modify, ref data);
        }
    }

    /// <summary>
    /// Takes the icon out of the notification area, once any balloon it is
    /// showing has gone.
    /// </summary>
    public void Hide()
    {
        _wanted = false;

        if (!_lending)
        {
            Remove();
        }
    }

    /// <summary>
    /// Shows a notification-area balloon: the way to tell somebody something
    /// when Windows' own notifications cannot be used.
    /// </summary>
    /// <remarks>
    /// A balloon needs an icon to come from, so the icon is borrowed for as long
    /// as the balloon lasts even while the window is open and the icon would
    /// otherwise be away. Windows still applies Do not disturb to it.
    /// </remarks>
    /// <returns>Whether the shell accepted it.</returns>
    public bool Announce(string title, string message, bool sound)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        if (_disposed)
        {
            return false;
        }

        _lending = true;
        Put();

        if (!_shown)
        {
            EndLoan();
            return false;
        }

        NotifyIconData data = Describe();
        data.Flags = HasInfo;
        data.InfoFlags = InfoUserIcon | InfoLargeIcon | InfoRespectQuietTime | (sound ? 0u : InfoNoSound);
        data.BalloonIcon = LoadLargeIcon();

        // Fixed fields of 256 and 64 characters, each keeping its terminator.
        title.AsSpan(0, Math.Min(title.Length, 63)).CopyTo(new Span<char>(data.InfoTitle, 64));
        message.AsSpan(0, Math.Min(message.Length, 255)).CopyTo(new Span<char>(data.Info, 256));

        if (Shell_NotifyIconW(Modify, ref data))
        {
            return true;
        }

        EndLoan();
        return false;
    }

    /// <summary>Gives the borrowed icon back once its balloon is over.</summary>
    private void EndLoan()
    {
        if (!_lending)
        {
            return;
        }

        _lending = false;

        if (!_wanted)
        {
            Remove();
        }
    }

    private void Remove()
    {
        _retry.Stop();

        if (!_shown)
        {
            return;
        }

        NotifyIconData data = new()
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = _source is { } source ? source.Handle : 0,
            Id = Id,
        };

        _ = Shell_NotifyIconW(Delete, ref data);
        _shown = false;
    }

    /// <summary>
    /// Brings Windsock to the foreground by way of the icon's own window.
    /// </summary>
    /// <remarks>
    /// A menu opened from the icon does not close when the next click lands
    /// elsewhere unless its process is in front, which is a shell rule about
    /// notification-area menus. Windows lets the process that owns a clicked
    /// icon take the foreground, so this works with no window of Windsock's
    /// showing at all.
    /// </remarks>
    public void TakeForeground()
    {
        if (_source is { } source)
        {
            _ = PInvoke.SetForegroundWindow(new HWND(source.Handle));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _wanted = false;
        _lending = false;
        Remove();
        _disposed = true;

        if (_source is not null)
        {
            _source.RemoveHook(OnMessage);
            _source.Dispose();
            _source = null;
        }

        if (!_icon.IsNull)
        {
            _ = PInvoke.DestroyIcon(_icon);
            _icon = default;
        }

        if (!_largeIcon.IsNull)
        {
            _ = PInvoke.DestroyIcon(_largeIcon);
            _largeIcon = default;
        }
    }

    /// <summary>Adds the icon, or schedules another try if the shell refuses.</summary>
    private void Put()
    {
        if ((!_wanted && !_lending) || _disposed)
        {
            _retry.Stop();
            return;
        }

        Attach();

        NotifyIconData data = Describe();
        data.Flags = HasMessage | HasIcon | HasTip;

        // An add that timed out may still have landed, and adding again would
        // then fail for good; a modify that succeeds says the icon is there.
        _shown = Shell_NotifyIconW(Add, ref data) || Shell_NotifyIconW(Modify, ref data);

        if (_shown)
        {
            _retry.Stop();
        }
        else
        {
            _retry.Start();
        }
    }

    /// <summary>Makes the hidden window the shell talks to.</summary>
    private void Attach()
    {
        if (_source is not null)
        {
            return;
        }

        HwndSourceParameters parameters = new("Windsock notification icon")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = ToolWindow,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(OnMessage);

        // Run as administrator, Windsock sits above Explorer, and Windows drops
        // messages sent upward across that line unless they are let through.
        _ = ChangeWindowMessageFilterEx(_source.Handle, _taskbarCreated, AllowMessage, 0);
        _ = ChangeWindowMessageFilterEx(_source.Handle, Callback, AllowMessage, 0);
    }

    private NotifyIconData Describe()
    {
        NotifyIconData data = new()
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = _source is { } source ? source.Handle : 0,
            Id = Id,
            CallbackMessage = Callback,
            Icon = LoadIcon(),
        };

        // Tip is a fixed 128-character field and has to keep its terminator.
        ReadOnlySpan<char> text = _tooltip.AsSpan(0, Math.Min(_tooltip.Length, 127));
        text.CopyTo(new Span<char>(data.Tip, 128));

        return data;
    }

    private nint LoadIcon()
    {
        if (!_icon.IsNull)
        {
            return _icon;
        }

        string? exe = Process.GetCurrentProcess().MainModule?.FileName;

        if (string.IsNullOrEmpty(exe))
        {
            return 0;
        }

        HICON small = default;
        fixed (char* name = exe)
        {
            _ = PInvoke.ExtractIconEx(name, 0, null, &small, 1);
        }

        _icon = small;
        return _icon;
    }

    private nint LoadLargeIcon()
    {
        if (!_largeIcon.IsNull)
        {
            return _largeIcon;
        }

        string? exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
        {
            return 0;
        }

        HICON large = default;
        fixed (char* name = exe)
        {
            _ = PInvoke.ExtractIconEx(name, 0, &large, null, 1);
        }

        _largeIcon = large;
        return _largeIcon;
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated)
        {
            // Explorer started again, and every icon it held went with it.
            _shown = false;
            Put();
            return 0;
        }

        if ((uint)message != Callback)
        {
            return 0;
        }

        switch ((uint)lParam)
        {
            case LeftButtonUp:
            case LeftDoubleClick:
                Opened?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case RightButtonUp:
                MenuRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case BalloonClick:
                EndLoan();
                AnnouncementClicked?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case BalloonHide:
            case BalloonTimeout:
                EndLoan();
                handled = true;
                break;

            default:
                break;
        }

        return 0;
    }

    /// <summary>
    /// The shell's icon record.
    /// </summary>
    /// <remarks>
    /// Hand-laid rather than generated: its layout depends on the architecture,
    /// which CsWin32 refuses to emit for an AnyCPU build. Every field is
    /// declared even though only the first few are used, because cbSize must be
    /// the size of the whole record: 976 bytes on x64, which is the only
    /// architecture Windsock ships. A wrong size is not silent - the shell
    /// rejects the call and no icon appears.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint filter);
}
