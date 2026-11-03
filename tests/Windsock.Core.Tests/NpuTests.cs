using Windsock.Core.Hardware;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// A neural processor's load, from GPU Engine counter instances under its
/// LUID. The instance names follow the form Windows uses for a graphics card
/// (verified here) and that a third-party tool reports for an Intel NPU (not
/// verified: this machine has none).
/// </summary>
public sealed class NpuTests
{
    private const string NpuLuid = "0x00000000_0x0001D2C5";
    private const string GpuLuid = "0x00000000_0x0000F1A3";

    private static string Engine(int process, string luid, int engine, string type) =>
        $"pid_{process}_luid_{luid}_phys_0_eng_{engine}_engtype_{type}";

    private static EngineTally Tally()
    {
        EngineTally tally = new();

        tally.AddEngine(Engine(1200, NpuLuid, 0, "Neural"), 30);
        tally.AddEngine(Engine(1300, NpuLuid, 0, "Neural"), 25);
        tally.AddEngine(Engine(1200, NpuLuid, 1, "Compute"), 12);
        tally.AddEngine(Engine(1200, GpuLuid, 0, "3D"), 80);
        tally.AddShared($"luid_{NpuLuid}_phys_0", 300_000_000);

        return tally;
    }

    /// <summary>
    /// An engine's load is summed over processes; the NPU's load is its
    /// busiest engine, as Task Manager draws it.
    /// </summary>
    [Fact]
    public void Npu_IsItsBusiestEngine()
    {
        NpuReading npu = Assert.Single(NpuProbe.Read([new NpuFacts("Intel(R) AI Boost", NpuLuid)], Tally()));

        Assert.Equal("Intel(R) AI Boost", npu.Name);
        Assert.Equal(55, npu.BusyPercent, 6);
        Assert.Equal(["Neural", "Compute"], npu.Engines.Select(engine => engine.Kind));
        Assert.Equal(300_000_000, npu.SharedBytes);
        Assert.Null(npu.DedicatedBytes);
        Assert.Equal([1200, 1300], npu.Processes.Select(process => process.ProcessId));
    }

    /// <summary>The graphics card's engines never count towards the NPU.</summary>
    [Fact]
    public void Npu_IgnoresOtherAdapters()
    {
        EngineTally tally = new();
        tally.AddEngine(Engine(1200, GpuLuid, 0, "3D"), 80);

        NpuReading npu = Assert.Single(NpuProbe.Read([new NpuFacts("NPU", NpuLuid)], tally));

        Assert.Equal(0, npu.BusyPercent);
        Assert.Empty(npu.Engines);
    }

    /// <summary>No NPU, no reading: nothing to draw.</summary>
    [Fact]
    public void NoNpu_IsAnEmptyList()
    {
        Assert.Empty(NpuProbe.Read([], Tally()));
    }

    /// <summary>Several processes on one engine can sum past 100; the load cannot.</summary>
    [Fact]
    public void Npu_IsClampedToAHundred()
    {
        EngineTally tally = new();
        tally.AddEngine(Engine(1, NpuLuid, 0, "Neural"), 70);
        tally.AddEngine(Engine(2, NpuLuid, 0, "Neural"), 70);

        Assert.Equal(100, Assert.Single(NpuProbe.Read([new NpuFacts("NPU", NpuLuid)], tally)).BusyPercent);
    }

    /// <summary>Clearing forgets the last collection, for every adapter.</summary>
    [Fact]
    public void Tally_ClearForgetsEverything()
    {
        EngineTally tally = Tally();
        tally.Clear();

        Assert.Equal(0, tally.Load(NpuLuid).Busy);
        Assert.Null(tally.Shared(NpuLuid));
        Assert.Empty(tally.Processes(GpuLuid, 6));
    }

    /// <summary>
    /// DXCore's vtable calls run on this machine. Measured here: the RTX 3070
    /// Ti and Microsoft's software renderer, neither an NPU.
    /// </summary>
    [Fact]
    public void DxCore_ListsAdaptersWithoutThrowing()
    {
        IReadOnlyList<DxCoreAdapter> adapters = DxCoreAdapters.Read();

        Assert.All(adapters, adapter => Assert.StartsWith("0x", adapter.Luid, StringComparison.Ordinal));
    }
}
