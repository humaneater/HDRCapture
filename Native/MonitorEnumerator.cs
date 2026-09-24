using System.ComponentModel;
using System.Runtime.InteropServices;
using HdrCapture.Capture;

namespace HdrCapture.Native;

internal static class MonitorEnumerator
{
    private const uint MonitorInfofPrimary = 0x00000001;
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const uint DisplayConfigDeviceInfoGetSourceName = 1;
    private const uint DisplayConfigDeviceInfoGetAdvancedColorInfo = 9;
    private const uint DisplayConfigDeviceInfoGetSdrWhiteLevel = 11;

    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var displayInfo = QueryDisplayInfo();
        var monitors = new List<MonitorInfo>();

        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx
            {
                Size = Marshal.SizeOf<MonitorInfoEx>()
            };

            if (!GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var dpi = GetEffectiveDpi(monitor);
            displayInfo.TryGetValue(info.DeviceName, out var colorInfo);
            monitors.Add(new MonitorInfo(
                monitor,
                info.DeviceName,
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top,
                dpi,
                colorInfo.HdrEnabled,
                colorInfo.SdrWhiteNits > 0 ? colorInfo.SdrWhiteNits : 200.0));
            return true;
        }, 0);

        return monitors
            .OrderBy(monitor => monitor.X)
            .ThenBy(monitor => monitor.Y)
            .ToArray();
    }

    private static uint GetEffectiveDpi(nint monitor)
    {
        try
        {
            var result = GetDpiForMonitor(monitor, 0, out var dpiX, out _);
            return result == 0 && dpiX > 0 ? dpiX : 96;
        }
        catch (DllNotFoundException)
        {
            return 96;
        }
    }

    private static Dictionary<string, DisplayColorInfo> QueryDisplayInfo()
    {
        var result = new Dictionary<string, DisplayColorInfo>(StringComparer.OrdinalIgnoreCase);
        var error = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
        if (error != 0 || pathCount == 0)
        {
            return result;
        }

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];
        error = QueryDisplayConfig(
            QdcOnlyActivePaths,
            ref pathCount,
            paths,
            ref modeCount,
            modes,
            0);

        if (error != 0)
        {
            return result;
        }

        for (var index = 0; index < pathCount; index++)
        {
            var path = paths[index];
            var sourceName = new DisplayConfigSourceDeviceName
            {
                Header = new DisplayConfigDeviceInfoHeader
                {
                    Type = DisplayConfigDeviceInfoGetSourceName,
                    Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                    AdapterId = path.SourceInfo.AdapterId,
                    Id = path.SourceInfo.Id
                },
                ViewGdiDeviceName = string.Empty
            };

            if (DisplayConfigGetDeviceInfo(ref sourceName) != 0 ||
                string.IsNullOrWhiteSpace(sourceName.ViewGdiDeviceName))
            {
                continue;
            }

            var advancedColor = new DisplayConfigAdvancedColorInfo
            {
                Header = new DisplayConfigDeviceInfoHeader
                {
                    Type = DisplayConfigDeviceInfoGetAdvancedColorInfo,
                    Size = (uint)Marshal.SizeOf<DisplayConfigAdvancedColorInfo>(),
                    AdapterId = path.TargetInfo.AdapterId,
                    Id = path.TargetInfo.Id
                }
            };
            var advancedResult = DisplayConfigGetDeviceInfo(ref advancedColor);

            var whiteLevel = new DisplayConfigSdrWhiteLevel
            {
                Header = new DisplayConfigDeviceInfoHeader
                {
                    Type = DisplayConfigDeviceInfoGetSdrWhiteLevel,
                    Size = (uint)Marshal.SizeOf<DisplayConfigSdrWhiteLevel>(),
                    AdapterId = path.TargetInfo.AdapterId,
                    Id = path.TargetInfo.Id
                }
            };
            var whiteResult = DisplayConfigGetDeviceInfo(ref whiteLevel);

            var hdrEnabled = advancedResult == 0 && (advancedColor.Value & 0x2) != 0;
            var whiteNits = whiteResult == 0
                ? whiteLevel.SdrWhiteLevel / 1000.0 * 80.0
                : 200.0;

            result[sourceName.ViewGdiDeviceName] = new DisplayColorInfo(hdrEnabled, whiteNits);
        }

        return result;
    }

    private readonly record struct DisplayColorInfo(bool HdrEnabled, double SdrWhiteNits);

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSdrWhiteLevel
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint SdrWhiteLevel;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(
        nint hdc,
        nint clipRect,
        MonitorEnumProc callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint pathCount,
        out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] DisplayConfigPathInfo[] pathInfo,
        ref uint modeCount,
        [Out] DisplayConfigModeInfo[] modeInfo,
        nint currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName deviceInfo);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigAdvancedColorInfo deviceInfo);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSdrWhiteLevel deviceInfo);
}
