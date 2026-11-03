using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Windsock.Core.Power;

/// <summary>
/// Follows Windows' effective power mode through
/// <c>PowerRegisterForEffectivePowerModeNotifications</c>.
/// </summary>
/// <remarks>
/// <para>
/// Windows calls back once at registration with the mode as it is, then on
/// every change, on a thread-pool thread; nothing is polled, so holding the
/// registration costs nothing between changes. Version 2 is asked for first,
/// because it also reports game mode; Windows before 1903 knows only version 1.
/// </para>
/// <para>
/// The callback finds its watch through a number in a table rather than a
/// handle to the object, so a call that arrives after <see cref="Dispose"/>
/// finds nothing and does nothing. The documentation does not promise none
/// will.
/// </para>
/// </remarks>
public sealed partial class PowerModeWatch : IDisposable
{
    private const uint Version1 = 1;
    private const uint Version2 = 2;
    private const int NoMode = -1;

    private static readonly ConcurrentDictionary<nint, PowerModeWatch> Live = new();
    private static long _next;

    private readonly nint _key;
    private nint _registration;
    private int _mode = NoMode;

    private PowerModeWatch(nint key) => _key = key;

    /// <summary>Raised on a thread-pool thread when the mode changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The effective power mode, or nothing until Windows has said.</summary>
    public PowerMode? Current
    {
        get
        {
            int mode = Volatile.Read(ref _mode);
            return mode == NoMode ? null : (PowerMode)mode;
        }
    }

    /// <summary>
    /// Starts following the mode, or answers nothing on a Windows that cannot say.
    /// </summary>
    public static unsafe PowerModeWatch? Start()
    {
        nint key = (nint)Interlocked.Increment(ref _next);
        PowerModeWatch watch = new(key);
        Live[key] = watch;

        try
        {
            delegate* unmanaged[Stdcall]<int, nint, void> callback = &OnMode;

            if (PowerRegisterForEffectivePowerModeNotifications(Version2, callback, key, out nint registration) < 0
                && PowerRegisterForEffectivePowerModeNotifications(Version1, callback, key, out registration) < 0)
            {
                _ = Live.TryRemove(key, out _);
                return null;
            }

            watch._registration = registration;
            return watch;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older than Windows 10 1809, which is older than Windsock supports.
            _ = Live.TryRemove(key, out _);
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnMode(int mode, nint context)
    {
        if (!Live.TryGetValue(context, out PowerModeWatch? watch))
        {
            return;
        }

        int was = Interlocked.Exchange(ref watch._mode, mode);

        if (was == mode)
        {
            return;
        }

        try
        {
            watch.Changed?.Invoke(watch, EventArgs.Empty);
        }
#pragma warning disable CA1031 // Nothing may be thrown back into Windows' thread pool from here.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    public void Dispose()
    {
        _ = Live.TryRemove(_key, out _);

        nint registration = Interlocked.Exchange(ref _registration, 0);

        if (registration != 0)
        {
            _ = PowerUnregisterFromEffectivePowerModeNotifications(registration);
        }
    }

    [LibraryImport("powrprof.dll")]
    private static unsafe partial int PowerRegisterForEffectivePowerModeNotifications(
        uint version,
        delegate* unmanaged[Stdcall]<int, nint, void> callback,
        nint context,
        out nint registration);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerUnregisterFromEffectivePowerModeNotifications(nint registration);
}
