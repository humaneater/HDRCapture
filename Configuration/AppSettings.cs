using System.Text.Json.Serialization;
using HdrCapture.Core;
using Microsoft.Win32;

namespace HdrCapture.Configuration;

internal sealed class AppSettings
{
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;

    public HotkeySettings Hotkey { get; set; } = HotkeySettings.CreateDefault();

    public string SaveDirectory { get; set; } = string.Empty;

    public double ExposureEv { get; set; }

    public bool StartWithWindows { get; set; }

    public bool IncludeCursor { get; set; }

    public bool SaveExr { get; set; }

    public PreviewQuality PreviewQuality { get; set; } = PreviewQuality.Low;

    public DenoiseSettings Denoise { get; set; } = new();

    public PortraitSettings Portrait { get; set; } = new();

    public ComfyUiSettings ComfyUi { get; set; } = new();

    [JsonIgnore]
    public string EffectiveSaveDirectory => ResolveSaveDirectory(SaveDirectory);

    public AppSettings Clone()
    {
        return new AppSettings
        {
            Version = Version,
            Hotkey = new HotkeySettings
            {
                Modifiers = Hotkey.Modifiers,
                VirtualKey = Hotkey.VirtualKey
            },
            SaveDirectory = SaveDirectory,
            ExposureEv = ExposureEv,
            StartWithWindows = StartWithWindows,
            IncludeCursor = IncludeCursor,
            SaveExr = SaveExr,
            PreviewQuality = PreviewQuality,
            Denoise = Denoise.Clone(),
            Portrait = Portrait.Clone(),
            ComfyUi = ComfyUi.Clone()
        };
    }

    public void Normalize()
    {
        Version = CurrentVersion;
        Hotkey ??= HotkeySettings.CreateDefault();
        if (Hotkey.VirtualKey <= 0)
        {
            Hotkey = HotkeySettings.CreateDefault();
        }

        SaveDirectory = NormalizeDirectory(SaveDirectory);
        ExposureEv = Math.Clamp(ExposureEv, -5.0, 5.0);
        if (!Enum.IsDefined(PreviewQuality))
        {
            PreviewQuality = PreviewQuality.Low;
        }

        Denoise ??= new DenoiseSettings();
        Portrait ??= new PortraitSettings();
        ComfyUi ??= new ComfyUiSettings();
        Denoise.Normalize();
        Portrait.Normalize();
        ComfyUi.Normalize();
    }

    public static string DefaultSaveDirectory =>
        Path.Combine(AppContext.BaseDirectory, "picture");

    public static string ResolveSaveDirectory(string? configuredDirectory)
    {
        var value = string.IsNullOrWhiteSpace(configuredDirectory)
            ? DefaultSaveDirectory
            : Environment.ExpandEnvironmentVariables(configuredDirectory.Trim());

        if (!Path.IsPathFullyQualified(value))
        {
            value = Path.Combine(AppContext.BaseDirectory, value);
        }

        return Path.GetFullPath(value);
    }

    public static string NormalizeDirectory(string? configuredDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return string.Empty;
        }

        return ResolveSaveDirectory(configuredDirectory);
    }
}

internal sealed class DenoiseSettings
{
    public double Strength { get; set; } = 0.35;

    public double DetailPreservation { get; set; } = 0.60;

    public bool UseOidnForFinal { get; set; } = true;

    public bool SaveDenoisedExr { get; set; }

    public DenoiseSettings Clone()
    {
        return new DenoiseSettings
        {
            Strength = Strength,
            DetailPreservation = DetailPreservation,
            UseOidnForFinal = UseOidnForFinal,
            SaveDenoisedExr = SaveDenoisedExr
        };
    }

    public void Normalize()
    {
        Strength = Math.Clamp(Strength, 0.0, 1.0);
        DetailPreservation = Math.Clamp(DetailPreservation, 0.0, 1.0);
    }
}

internal sealed class PortraitSettings
{
    public string Checkpoint { get; set; } = string.Empty;

    public double SmoothSkin { get; set; } = 0.35;

    public double Denoise { get; set; } = 0.35;

    public double BrightnessEv { get; set; }

    public double DetailPreservation { get; set; } = 0.60;

    public bool UseIpAdapter { get; set; } = true;

    public PortraitSettings Clone()
    {
        return new PortraitSettings
        {
            Checkpoint = Checkpoint,
            SmoothSkin = SmoothSkin,
            Denoise = Denoise,
            BrightnessEv = BrightnessEv,
            DetailPreservation = DetailPreservation,
            UseIpAdapter = UseIpAdapter
        };
    }

    public void Normalize()
    {
        Checkpoint = NormalizeModelName(Checkpoint);
        SmoothSkin = Math.Clamp(SmoothSkin, 0.0, 1.0);
        Denoise = Math.Clamp(Denoise, 0.0, 1.0);
        BrightnessEv = Math.Clamp(BrightnessEv, -2.0, 2.0);
        DetailPreservation = Math.Clamp(DetailPreservation, 0.0, 1.0);
    }

    private static string NormalizeModelName(string? value)
    {
        var name = Path.GetFileName((value ?? string.Empty).Trim());
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name;
    }
}

internal sealed class ComfyUiSettings
{
    public string RootPath { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    public int Port { get; set; } = 8188;

    public int IdleMinutes { get; set; } = 10;

    public bool AutoStart { get; set; } = true;

    public ComfyUiSettings Clone()
    {
        return new ComfyUiSettings
        {
            RootPath = RootPath,
            BaseUrl = BaseUrl,
            Port = Port,
            IdleMinutes = IdleMinutes,
            AutoStart = AutoStart
        };
    }

    public void Normalize()
    {
        RootPath = NormalizeRootPath(RootPath);
        Port = Port is >= 1024 and <= 65535 ? Port : 8188;
        IdleMinutes = Math.Clamp(IdleMinutes, 1, 240);

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !IsLoopback(uri.Host))
        {
            BaseUrl = "http://127.0.0.1:8188";
        }

        BaseUrl = BaseUrl.TrimEnd('/');
    }

    private static string NormalizeRootPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
            return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
}

internal enum PreviewQuality
{
    Full = 0,
    Half = 1,
    Low = 2
}

internal sealed class HotkeySettings
{
    public int Modifiers { get; set; }

    public int VirtualKey { get; set; }

    public static HotkeySettings CreateDefault()
    {
        return new HotkeySettings
        {
            Modifiers = HotkeyManager.ControlModifier | HotkeyManager.AltModifier,
            VirtualKey = 0x79 // F10
        };
    }
}

internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HDRCapture";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "HDRCapture.exe");
            key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
