using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Dxgi;

namespace Windsock.Core.Hardware;

/// <summary>
/// One display adapter as the graphics stack knows it.
/// </summary>
public sealed record DisplayAdapter(
    string Name,
    string Luid,
    long Memory,
    long Shared,
    GraphicsVendor Vendor);

/// <summary>
/// Asks the graphics stack which adapters are fitted.
/// </summary>
public static class DisplayAdapters
{
    private const uint SoftwareAdapter = 2;

    /// <summary>Reads the adapters. Costly, and the answer does not change.</summary>
    public static IReadOnlyList<DisplayAdapter> Read()
    {
        List<DisplayAdapter> adapters = [];

        try
        {
            Fill(adapters);
        }
        catch (COMException)
        {
        }
        catch (DllNotFoundException)
        {
            // Older or trimmed Windows without the library at all.
        }

        return adapters;
    }

    private static unsafe void Fill(List<DisplayAdapter> adapters)
    {
        Guid wanted = typeof(IDXGIFactory1).GUID;

        if (PInvoke.CreateDXGIFactory1(&wanted, out object made).Failed || made is not IDXGIFactory1 factory)
        {
            return;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1? adapter = null;

                try
                {
                    factory.EnumAdapters1(index, out adapter);
                }
                catch (COMException)
                {
                    // The enumeration ends by refusing, rather than by saying so.
                    break;
                }

                if (adapter is null)
                {
                    break;
                }

                try
                {
                    DXGI_ADAPTER_DESC1 description = adapter.GetDesc1();
                    if (((uint)description.Flags & SoftwareAdapter) != 0)
                    {
                        continue;
                    }

                    adapters.Add(Describe(description));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static unsafe DisplayAdapter Describe(DXGI_ADAPTER_DESC1 description)
    {
        string name = description.Description.ToString().Trim();

        return new DisplayAdapter(
            name,
            LuidOf(description.AdapterLuid.HighPart, description.AdapterLuid.LowPart),
            (long)(ulong)description.DedicatedVideoMemory,
            (long)(ulong)description.SharedSystemMemory,
            VendorOf(description.VendorId, name));
    }

    /// <summary>
    /// The identifier in the form the performance counters spell it.
    /// </summary>
    public static string LuidOf(int high, uint low) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{high:X8}_0x{low:X8}");

    private static GraphicsVendor VendorOf(uint vendorId, string name) => vendorId switch
    {
        0x10DE => GraphicsVendor.Nvidia,
        0x1002 or 0x1022 => GraphicsVendor.Amd,
        0x8086 => GraphicsVendor.Intel,
        _ => Named(name),
    };

    private static GraphicsVendor Named(string name) =>
        name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? GraphicsVendor.Nvidia
        : name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? GraphicsVendor.Amd
        : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? GraphicsVendor.Intel
        : GraphicsVendor.Unknown;
}
