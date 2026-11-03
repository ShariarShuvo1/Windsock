using System.Runtime.InteropServices;
using System.Text;

namespace Windsock.Core.Hardware;

/// <summary>
/// One adapter as DXCore lists it: graphics cards, and also what the display
/// stack never shows, such as neural processors.
/// </summary>
/// <param name="Name">The driver's description.</param>
/// <param name="Luid">Its identifier in the form the performance counters spell it.</param>
/// <param name="IsNpu">Whether DXCore calls it a neural processor.</param>
/// <param name="IsGpu">Whether DXCore calls it a graphics processor.</param>
public readonly record struct DxCoreAdapter(string Name, string Luid, bool IsNpu, bool IsGpu);

/// <summary>
/// Lists compute adapters through DXCore, which is how a neural processor is
/// found: it is an MCDM device, compute only, and DXGI - which lists the
/// display adapters - does not see it.
/// </summary>
/// <remarks>
/// <para>
/// DXCore is a small COM-style API with no type library, so it is called
/// through its vtables, slot by slot as <c>dxcore_interface.h</c> declares
/// them: the factory's <c>CreateAdapterList</c> is slot 3; the list's
/// <c>GetAdapter</c> 3 and <c>GetAdapterCount</c> 4; the adapter's
/// <c>IsAttributeSupported</c> 4, <c>GetProperty</c> 6 and
/// <c>GetPropertySize</c> 7. Slot 2 is always <c>Release</c>.
/// </para>
/// <para>
/// Adapters are asked for twice, as machine-learning adapters
/// (<c>D3D12_GENERIC_ML</c>, how Microsoft's NPU samples look for one) and as
/// compute adapters (<c>D3D12_CORE_COMPUTE</c>), and each is asked whether it
/// carries <c>DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU</c>. A Windows too old to
/// know that attribute answers no, and no NPU is shown.
/// </para>
/// </remarks>
public static unsafe partial class DxCoreAdapters
{
    private static readonly Guid FactoryInterface = new("78EE5945-C36E-4B13-A669-005DD11C0F06");
    private static readonly Guid ListInterface = new("526C7776-40E9-459B-B711-F32AD76DFC28");
    private static readonly Guid AdapterInterface = new("F0DB4C7F-FE5A-42A2-BD62-F2A6CF6FC83E");
    private static readonly Guid MachineLearning = new("B71B0D41-1088-422F-A27C-0250B7D3A988");
    private static readonly Guid CoreCompute = new("248E2800-A793-4724-ABAA-23A6DE1BE090");
    private static readonly Guid NpuType = new("D46140C4-ADD7-451B-9E56-06FE8C3B58ED");
    private static readonly Guid GpuType = new("B69EB219-3DED-4464-979F-A00BD4687006");

    private const uint InstanceLuid = 0;
    private const uint DriverDescription = 2;

    /// <summary>Every compute adapter DXCore lists, once each; empty where DXCore is absent.</summary>
    public static IReadOnlyList<DxCoreAdapter> Read()
    {
        Dictionary<string, DxCoreAdapter> found = new(StringComparer.Ordinal);
        nint factory = 0;

        try
        {
            Guid wanted = FactoryInterface;

            if (DXCoreCreateAdapterFactory(&wanted, &factory) < 0 || factory == 0)
            {
                return [];
            }

            Collect(factory, MachineLearning, found);
            Collect(factory, CoreCompute, found);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // dxcore.dll arrived in Windows 10 2004.
            return [];
        }
        finally
        {
            Release(factory);
        }

        return [.. found.Values];
    }

    /// <summary>The neural processors among them.</summary>
    public static IReadOnlyList<NpuFacts> Npus()
    {
        List<NpuFacts> npus = [];

        foreach (DxCoreAdapter adapter in Read())
        {
            if (adapter.IsNpu)
            {
                npus.Add(new NpuFacts(adapter.Name, adapter.Luid));
            }
        }

        return npus;
    }

    private static void Collect(nint factory, Guid attribute, Dictionary<string, DxCoreAdapter> found)
    {
        nint list = 0;

        try
        {
            Guid listInterface = ListInterface;
            var createList = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, Guid*, nint*, int>)Slot(factory, 3);

            if (createList(factory, 1, &attribute, &listInterface, &list) < 0 || list == 0)
            {
                return;
            }

            var count = (delegate* unmanaged[Stdcall]<nint, uint>)Slot(list, 4);
            var get = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(list, 3);
            uint adapters = count(list);

            for (uint index = 0; index < adapters; index++)
            {
                nint adapter = 0;
                Guid adapterInterface = AdapterInterface;

                if (get(list, index, &adapterInterface, &adapter) < 0 || adapter == 0)
                {
                    continue;
                }

                try
                {
                    if (Describe(adapter) is { } described)
                    {
                        found.TryAdd(described.Luid, described);
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(list);
        }
    }

    private static DxCoreAdapter? Describe(nint adapter)
    {
        byte[]? luid = Property(adapter, InstanceLuid);

        if (luid is not { Length: >= 8 })
        {
            return null;
        }

        uint low = BitConverter.ToUInt32(luid, 0);
        int high = BitConverter.ToInt32(luid, 4);

        string name = Property(adapter, DriverDescription) is { } text
            ? Ansi(text)
            : string.Empty;

        return new DxCoreAdapter(
            name.Length > 0 ? name : "Neural processor",
            DisplayAdapters.LuidOf(high, low),
            Supports(adapter, NpuType),
            Supports(adapter, GpuType));
    }

    private static bool Supports(nint adapter, Guid attribute)
    {
        var supported = (delegate* unmanaged[Stdcall]<nint, Guid*, byte>)Slot(adapter, 4);
        return supported(adapter, &attribute) != 0;
    }

    private static byte[]? Property(nint adapter, uint property)
    {
        var size = (delegate* unmanaged[Stdcall]<nint, uint, nuint*, int>)Slot(adapter, 7);
        var get = (delegate* unmanaged[Stdcall]<nint, uint, nuint, void*, int>)Slot(adapter, 6);

        nuint length = 0;

        if (size(adapter, property, &length) < 0 || length == 0 || length > 4096)
        {
            return null;
        }

        byte[] value = new byte[(int)length];

        fixed (byte* buffer = value)
        {
            return get(adapter, property, length, buffer) < 0 ? null : value;
        }
    }

    private static string Ansi(byte[] text)
    {
        int end = Array.IndexOf(text, (byte)0);
        return Encoding.UTF8.GetString(text, 0, end < 0 ? text.Length : end).Trim();
    }

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];

    private static void Release(nint instance)
    {
        if (instance != 0)
        {
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2);
            _ = release(instance);
        }
    }

    [LibraryImport("dxcore.dll")]
    private static partial int DXCoreCreateAdapterFactory(Guid* interfaceId, nint* factory);
}
