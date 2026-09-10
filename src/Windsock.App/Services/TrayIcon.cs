using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Windsock.App.Services;

/// <summary>
/// An icon in the notification area, and the way back to a hidden window.
/// </summary>
internal sealed unsafe partial class TrayIcon : IDisposable
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

    private const uint Id = 1;

    private readonly Window _owner;

    private HwndSource? _source;
    private HICON _icon;
    private bool _shown;

    public TrayIcon(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <summary>Raised when the icon is clicked.</summary>
    public event EventHandler? Opened;

    /// <summary>Raised when the icon is right-clicked.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>Whether the icon is in the notification area.</summary>
    public bool IsShown => _shown;

    /// <summary>Puts the icon in the notification area.</summary>
    public void Show(string tooltip)
    {
        if (_shown)
        {
            SetTooltip(tooltip);
            return;
        }

        if (!Attach())
        {
            return;
        }

        NotifyIconData data = Describe(tooltip);
        data.Flags = HasMessage | HasIcon | HasTip;

        _shown = Shell_NotifyIconW(Add, ref data);
    }

    /// <summary>Takes the icon out of the notification area.</summary>
    public void Hide()
    {
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

    /// <summary>Changes what hovering the icon says.</summary>
    public void SetTooltip(string tooltip)
    {
        if (!_shown)
        {
            return;
        }

        NotifyIconData data = Describe(tooltip);
        data.Flags = HasTip;

        _ = Shell_NotifyIconW(Modify, ref data);
    }

    public void Dispose()
    {
        Hide();

        _source?.RemoveHook(OnMessage);
        _source = null;

        if (!_icon.IsNull)
        {
            _ = PInvoke.DestroyIcon(_icon);
            _icon = default;
        }
    }

    /// <summary>Finds the window's handle and listens for the shell's messages.</summary>
    private bool Attach()
    {
        if (_source is not null)
        {
            return true;
        }

        // A window that has never been shown has no handle to hang an icon on.
        nint handle = new WindowInteropHelper(_owner).Handle;

        if (handle == 0 || HwndSource.FromHwnd(handle) is not { } source)
        {
            return false;
        }

        _source = source;
        _source.AddHook(OnMessage);
        return true;
    }

    private NotifyIconData Describe(string tooltip)
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
        ReadOnlySpan<char> text = tooltip.AsSpan(0, Math.Min(tooltip.Length, 127));
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

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
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
}
