using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windsock.Core.Notifications;

namespace Windsock.App.Services.Notifications;

/// <summary>
/// The COM class Windows calls when somebody clicks a Windsock notification, for
/// the edition installed without a package.
/// </summary>
/// <remarks>
/// <para>
/// A notification's own click event only reaches a program while its pop-up is
/// on screen. Once it has moved into the notification centre, Windows finds the
/// program through the COM class named in its registration and calls this, or -
/// if Windsock has since closed - starts Windsock to call it. Without it, clicking
/// an older notification did nothing at all.
/// </para>
/// <para>
/// Built with the source-generated COM wrappers rather than the runtime's
/// built-in COM, which needs no reflection and makes every signature here
/// explicit. Registered on the UI thread, which is a single-threaded apartment,
/// so each call arrives through the message loop on that thread.
/// </para>
/// </remarks>
internal sealed unsafe partial class NoticeActivator : IDisposable
{
    private const uint LocalServer = 4;
    private const uint MultipleUse = 1;

    internal static readonly StrategyBasedComWrappers Wrappers = new();

    private nint _factory;
    private uint _cookie;

    private NoticeActivator(nint factory, uint cookie)
    {
        _factory = factory;
        _cookie = cookie;
    }

    /// <summary>
    /// Registers the class for this process. Nothing when Windows refuses, in
    /// which case notifications still show and only clicks from the notification
    /// centre go unanswered.
    /// </summary>
    public static NoticeActivator? Register(Action<string?> activated)
    {
        ArgumentNullException.ThrowIfNull(activated);

        NoticeClassFactory factory = new(new NoticeCallback(activated));
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(factory, CreateComInterfaceFlags.None);
        Guid activator = NotificationRegistration.Activator;

        int result = CoRegisterClassObject(in activator, unknown, LocalServer, MultipleUse, out uint cookie);

        if (result < 0)
        {
            Marshal.Release(unknown);
            return null;
        }

        return new NoticeActivator(unknown, cookie);
    }

    public void Dispose()
    {
        if (_cookie != 0)
        {
            _ = CoRevokeClassObject(_cookie);
            _cookie = 0;
        }

        if (_factory != 0)
        {
            Marshal.Release(_factory);
            _factory = 0;
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(in Guid classId, nint unknown, uint context, uint flags, out uint cookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint cookie);
}

/// <summary>What Windows calls on a click: the app id, and the launch text the notice carried.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
internal partial interface INotificationActivationCallback
{
    void Activate(string appUserModelId, string? invokedArgs, nint data, uint count);
}

/// <summary>The standard COM class factory, which Windows asks for the callback.</summary>
[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal unsafe partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint outer, Guid* interfaceId, nint* instance);

    [PreserveSig]
    int LockServer(int doLock);
}

[GeneratedComClass]
internal sealed partial class NoticeCallback(Action<string?> activated) : INotificationActivationCallback
{
    public void Activate(string appUserModelId, string? invokedArgs, nint data, uint count) =>
        activated(invokedArgs);
}

[GeneratedComClass]
internal sealed unsafe partial class NoticeClassFactory(NoticeCallback callback) : IClassFactory
{
    private const int NoAggregation = unchecked((int)0x80040110);
    private const int NullPointer = unchecked((int)0x80004003);

    public int CreateInstance(nint outer, Guid* interfaceId, nint* instance)
    {
        if (instance is null || interfaceId is null)
        {
            return NullPointer;
        }

        *instance = 0;

        if (outer != 0)
        {
            return NoAggregation;
        }

        nint unknown = NoticeActivator.Wrappers.GetOrCreateComInterfaceForObject(callback, CreateComInterfaceFlags.None);
        int result = Marshal.QueryInterface(unknown, in *interfaceId, out nint wanted);
        Marshal.Release(unknown);

        if (result >= 0)
        {
            *instance = wanted;
        }

        return result;
    }

    public int LockServer(int doLock) => 0;
}
