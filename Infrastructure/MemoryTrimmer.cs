using System.Runtime.InteropServices;

namespace HdrCapture.Infrastructure;

/// <summary>
/// Releases managed garbage and returns eligible pages after the large capture buffers are no
/// longer needed. This prevents the following EXR and clipboard allocations from stacking on
/// top of pages that Windows would otherwise keep resident.
/// </summary>
internal static partial class MemoryTrimmer
{
    public static void CollectAndTrim(string stage)
    {
        var before = Environment.WorkingSet;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        _ = EmptyWorkingSet(GetCurrentProcess());
        Log.Info(
            $"[memory] {stage}: {before / (1024 * 1024)} MB -> " +
            $"{Environment.WorkingSet / (1024 * 1024)} MB");
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(nint process);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();
}
