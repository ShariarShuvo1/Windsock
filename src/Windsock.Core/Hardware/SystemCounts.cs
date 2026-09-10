using System.Runtime.InteropServices;

namespace Windsock.Core.Hardware;

/// <summary>
/// The figures Windows keeps about itself as a whole.
/// </summary>
public readonly record struct SystemCounts(
    long PhysicalTotal,
    long PhysicalAvailable,
    long SystemCache,
    long CommitTotal,
    long CommitLimit,
    long KernelPaged,
    long KernelNonPaged,
    int Processes,
    int Threads,
    int Handles);

/// <summary>
/// Reads what Windows will say about itself in one call.
/// </summary>
public static partial class SystemInformation
{
    /// <summary>Takes a reading.</summary>
    public static SystemCounts Read()
    {
        PerformanceInformation info = default;
        info.Size = (uint)Marshal.SizeOf<PerformanceInformation>();

        if (!GetPerformanceInfo(ref info, info.Size))
        {
            return default;
        }
        long page = (long)info.PageSize;

        return new SystemCounts(
            (long)info.PhysicalTotal * page,
            (long)info.PhysicalAvailable * page,
            (long)info.SystemCache * page,
            (long)info.CommitTotal * page,
            (long)info.CommitLimit * page,
            (long)info.KernelPaged * page,
            (long)info.KernelNonPaged * page,
            (int)info.ProcessCount,
            (int)info.ThreadCount,
            (int)info.HandleCount);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonPaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [LibraryImport("psapi.dll", EntryPoint = "GetPerformanceInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(ref PerformanceInformation info, uint size);
}
