using System.Reflection;
using System.Runtime.InteropServices;
using HdrCapture.Infrastructure;

namespace HdrCapture.Imaging;

/// <summary>
/// Loads the bundled Intel Open Image Denoise 2.5 CPU runtime. The native files are embedded
/// in the executable and extracted to a versioned local directory before use.
/// </summary>
internal static class OidnRuntime
{
    private const string RuntimeVersion = "2.5.0";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> FileHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenImageDenoise.dll"] = "E200AC479E04CF89D80B7422B524A6D05FCDCAEB9BF391A16DBC07D2C0838075",
        ["OpenImageDenoise_core.dll"] = "0F465EFB065A29DFC31B9F326A61753E0EF801FD99EEA21E7B2A79C5A26DA884",
        ["OpenImageDenoise_device_cpu.dll"] = "CE5518D38733F23ACD2AE2FA486262DF70AFB818CC9A748F90940CDA0925C061",
        ["tbb12.dll"] = "12D8622ECEDE4409961B449E77D3AB368DEE8D15F6A0B1C8352A657F5848DBB0"
    };

    private static IntPtr _library;
    private static string? _directory;
    private static string? _error;
    private static bool _initialized;

    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _library != 0;
        }
    }

    public static string? Error
    {
        get
        {
            EnsureInitialized();
            return _error;
        }
    }

    public static string? RuntimeDirectory
    {
        get
        {
            EnsureInitialized();
            return _directory;
        }
    }

    public static T GetExport<T>(string name) where T : Delegate
    {
        EnsureInitialized();
        if (_library == 0)
        {
            throw new InvalidOperationException(_error ?? "Open Image Denoise 不可用。");
        }

        var address = NativeLibrary.GetExport(_library, name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HDRCapture",
                    "runtime",
                    "oidn-" + RuntimeVersion);
                Directory.CreateDirectory(directory);
                ExtractRuntimeFiles(directory);

                // Load dependencies first so the main DLL resolves the exact bundled copy.
                NativeLibrary.Load(Path.Combine(directory, "tbb12.dll"));
                NativeLibrary.Load(Path.Combine(directory, "OpenImageDenoise_core.dll"));
                NativeLibrary.Load(Path.Combine(directory, "OpenImageDenoise_device_cpu.dll"));
                _library = NativeLibrary.Load(Path.Combine(directory, "OpenImageDenoise.dll"));
                _directory = directory;
                Log.Info($"OIDN {RuntimeVersion} loaded from {directory}");
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                Log.Error("Failed to load the bundled OIDN runtime.", ex);
            }
        }
    }

    private static void ExtractRuntimeFiles(string directory)
    {
        var assembly = typeof(OidnRuntime).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();
        foreach (var (fileName, expectedHash) in FileHashes)
        {
            var destination = Path.Combine(directory, fileName);
            if (File.Exists(destination) && HashMatches(destination, expectedHash))
            {
                continue;
            }

            var resourceName = resourceNames.FirstOrDefault(name =>
                name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
            {
                throw new FileNotFoundException($"程序资源中缺少 {fileName}。");
            }

            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                using (var source = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new FileNotFoundException($"无法读取程序资源 {resourceName}。"))
                using (var target = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           1 << 20,
                           FileOptions.SequentialScan))
                {
                    source.CopyTo(target, 1 << 20);
                }

                if (!HashMatches(temporary, expectedHash))
                {
                    throw new InvalidDataException($"{fileName} 的资源哈希不匹配。");
                }

                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))
                .Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal sealed class OidnApi : IDisposable
{
    private const int DeviceTypeCpu = 1;
    private const int FormatFloat3 = 0x3;
    private const string FilterTypeRt = "RT";

    private readonly Delegates _delegates;
    private readonly IntPtr _device;
    private bool _disposed;

    public OidnApi()
    {
        _delegates = new Delegates(
            OidnRuntime.GetExport<NewDeviceDelegate>("oidnNewDevice"),
            OidnRuntime.GetExport<CommitDeviceDelegate>("oidnCommitDevice"),
            OidnRuntime.GetExport<NewFilterDelegate>("oidnNewFilter"),
            OidnRuntime.GetExport<SetSharedFilterImageDelegate>("oidnSetSharedFilterImage"),
            OidnRuntime.GetExport<SetFilterBoolDelegate>("oidnSetFilterBool"),
            OidnRuntime.GetExport<CommitFilterDelegate>("oidnCommitFilter"),
            OidnRuntime.GetExport<ExecuteFilterDelegate>("oidnExecuteFilter"),
            OidnRuntime.GetExport<ReleaseFilterDelegate>("oidnReleaseFilter"),
            OidnRuntime.GetExport<ReleaseDeviceDelegate>("oidnReleaseDevice"),
            OidnRuntime.GetExport<GetDeviceErrorDelegate>("oidnGetDeviceError"));

        _device = _delegates.NewDevice(DeviceTypeCpu);
        if (_device == 0)
        {
            throw new InvalidOperationException("OIDN 无法创建 CPU 设备。");
        }

        _delegates.CommitDevice(_device);
        ThrowIfError("初始化 OIDN 设备");
    }

    public unsafe void Execute(float[] color, float[] output, int width, int height)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OidnApi));
        }

        IntPtr filter = 0;
        try
        {
            filter = _delegates.NewFilter(_device, FilterTypeRt);
            if (filter == 0)
            {
                ThrowIfError("创建 OIDN RT 滤波器");
                throw new InvalidOperationException("OIDN 无法创建 RT 滤波器。");
            }

            fixed (float* colorPointer = color)
            fixed (float* outputPointer = output)
            {
                _delegates.SetSharedFilterImage(
                    filter,
                    "color",
                    (nint)colorPointer,
                    FormatFloat3,
                    (nuint)width,
                    (nuint)height,
                    0,
                    0,
                    0);
                ThrowIfError("设置 OIDN 输入图像");

                _delegates.SetSharedFilterImage(
                    filter,
                    "output",
                    (nint)outputPointer,
                    FormatFloat3,
                    (nuint)width,
                    (nuint)height,
                    0,
                    0,
                    0);
                ThrowIfError("设置 OIDN 输出图像");

                // HDR is supported by the RT filter. Older runtimes may not expose this option,
                // so an unsupported parameter is treated as a probe and does not abort the run.
                _delegates.SetFilterBool(filter, "hdr", true);
                _delegates.GetDeviceError(_device, out var hdrError);
                _ = hdrError;

                _delegates.CommitFilter(filter);
                ThrowIfError("提交 OIDN 滤波器");
                _delegates.ExecuteFilter(filter);
                ThrowIfError("执行 OIDN 降噪");
            }
        }
        finally
        {
            if (filter != 0)
            {
                _delegates.ReleaseFilter(filter);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _delegates.ReleaseDevice(_device);
    }

    private void ThrowIfError(string operation)
    {
        if (!_delegates.GetDeviceError(_device, out var messagePointer))
        {
            return;
        }

        var message = messagePointer == 0
            ? "未知错误"
            : Marshal.PtrToStringAnsi(messagePointer) ?? "未知错误";

        throw new InvalidOperationException($"{operation}失败：{message}");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NewDeviceDelegate(int type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CommitDeviceDelegate(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NewFilterDelegate(IntPtr device, [MarshalAs(UnmanagedType.LPStr)] string type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSharedFilterImageDelegate(
        IntPtr filter,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        IntPtr pointer,
        int format,
        nuint width,
        nuint height,
        nuint byteOffset,
        nuint pixelStride,
        nuint rowStride);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetFilterBoolDelegate(
        IntPtr filter,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        [MarshalAs(UnmanagedType.I1)] bool value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CommitFilterDelegate(IntPtr filter);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ExecuteFilterDelegate(IntPtr filter);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReleaseFilterDelegate(IntPtr filter);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReleaseDeviceDelegate(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetDeviceErrorDelegate(IntPtr device, out IntPtr message);

    private sealed record Delegates(
        NewDeviceDelegate NewDevice,
        CommitDeviceDelegate CommitDevice,
        NewFilterDelegate NewFilter,
        SetSharedFilterImageDelegate SetSharedFilterImage,
        SetFilterBoolDelegate SetFilterBool,
        CommitFilterDelegate CommitFilter,
        ExecuteFilterDelegate ExecuteFilter,
        ReleaseFilterDelegate ReleaseFilter,
        ReleaseDeviceDelegate ReleaseDevice,
        GetDeviceErrorDelegate GetDeviceError);
}
