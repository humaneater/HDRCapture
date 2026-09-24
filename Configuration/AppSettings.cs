using System.Text.Json.Serialization;
using HdrCapture.Core;
using Microsoft.Win32;

namespace HdrCapture.Configuration;

internal sealed class AppSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public HotkeySettings Hotkey { get; set; } = HotkeySettings.CreateDefault();

    public string SaveDirectory { get; set; } = string.Empty;

    public double ExposureEv { get; set; }

    public bool StartWithWindows { get; set; }

    public bool IncludeCursor { get; set; }

    public bool SaveExr { get; set; }

    public PreviewQuality PreviewQuality { get; set; } = PreviewQuality.Low;

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
            PreviewQuality = PreviewQuality
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
