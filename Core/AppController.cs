using System.Diagnostics;
using System.IO;
using System.Windows;
using HdrCapture.Configuration;
using HdrCapture.Infrastructure;
using HdrCapture.Ui;

namespace HdrCapture.Core;

internal sealed class AppController : IDisposable
{
    private readonly SettingsStore _settingsStore = new();
    private readonly HotkeyManager _hotkeyManager = new();
    private readonly CaptureWorkflow _workflow = new();
    private AppSettings _settings = new();
    private TrayIcon? _tray;
    private bool _disposed;

    public void Start()
    {
        _settings = _settingsStore.Load();
        ApplyAutoStart(_settings.StartWithWindows);

        _hotkeyManager.Initialize();
        _hotkeyManager.Pressed += OnCaptureRequested;

        var registered = TryRegisterHotkey(_settings.Hotkey.Modifiers, _settings.Hotkey.VirtualKey);
        _tray = new TrayIcon(HotkeyFormatter.Format(_settings.Hotkey.Modifiers, _settings.Hotkey.VirtualKey));
        _tray.CaptureRequested += OnCaptureRequested;
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.OpenFolderRequested += (_, _) => OpenSaveFolder();
        _tray.ExitRequested += (_, _) => System.Windows.Application.Current?.Shutdown(0);

        if (!registered)
        {
            _tray.ShowError(
                $"快捷键 {HotkeyFormatter.Format(_settings.Hotkey.Modifiers, _settings.Hotkey.VirtualKey)} " +
                "注册失败，可能已被其他程序占用。可在设置中更换。");
        }

        Log.Info(
            $"HDRCapture started. Save EXR: {_settings.SaveExr}; " +
            $"Save directory: {_settings.EffectiveSaveDirectory}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkeyManager.Pressed -= OnCaptureRequested;
        _hotkeyManager.Dispose();
        _tray?.Dispose();
        _tray = null;
    }

    private bool TryRegisterHotkey(int modifiers, int virtualKey)
    {
        var registered = _hotkeyManager.TryRegister(modifiers, virtualKey, out var error);
        if (!registered)
        {
            Log.Error($"RegisterHotKey failed: {error}");
        }

        return registered;
    }

    private async void OnCaptureRequested(object? sender, EventArgs e)
    {
        if (_disposed || _workflow.IsBusy)
        {
            return;
        }

        try
        {
            var outcome = await _workflow.RunInteractiveAsync(_settings).ConfigureAwait(true);
            Report(outcome);
        }
        catch (Exception ex)
        {
            Log.Error("Capture failed.", ex);
            _tray?.ShowError($"截图失败：{ex.Message}");
        }
    }

    private void Report(CaptureOutcome outcome)
    {
        if (outcome.SaveDirectoryError is { Length: > 0 } directoryError)
        {
            System.Windows.MessageBox.Show(
                $"无法写入保存目录：{directoryError}\n\n请在设置中选择其他目录。",
                "HDRCapture",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ShowSettings();
            return;
        }

        if (outcome.Cancelled)
        {
            return;
        }

        if (outcome.ExrError is { Length: > 0 } exrError)
        {
            System.Windows.MessageBox.Show(
                $"保存 EXR 失败：{exrError}" +
                (outcome.ClipboardCopied ? "\n\n图片已复制到剪贴板。" : string.Empty),
                "HDRCapture",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ShowSettings();
            return;
        }

        if (outcome.ClipboardError is { Length: > 0 } clipboardError)
        {
            var exrStatus = outcome.ExrSaved
                ? $"\nEXR 已保存为 {Path.GetFileName(outcome.ExrPath)}。"
                : "\n本次未保存 EXR。";
            _tray?.ShowError(
                $"复制到剪贴板失败：{clipboardError}{exrStatus}");
            return;
        }

        if (outcome.ExrSaved)
        {
            _tray?.ShowInfo(
                $"已保存 {Path.GetFileName(outcome.ExrPath)} " +
                $"({outcome.Region.Width}x{outcome.Region.Height})，并已复制到剪贴板。");
        }
        else if (outcome.ClipboardCopied)
        {
            _tray?.ShowInfo(
                $"已复制 {outcome.Region.Width}x{outcome.Region.Height} 图片到剪贴板（未保存 EXR）。");
        }
    }

    internal void ShowSettings()
    {
        if (_disposed)
        {
            return;
        }

        var window = new SettingsWindow(_settings, TryRegisterHotkey);
        var accepted = window.ShowDialog() == true && window.Result is not null;
        if (!accepted)
        {
            TryRegisterHotkey(_settings.Hotkey.Modifiers, _settings.Hotkey.VirtualKey);
            return;
        }

        _settings = window.Result!;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings.", ex);
        }

        ApplyAutoStart(_settings.StartWithWindows);
        _tray?.UpdateHotkey(HotkeyFormatter.Format(_settings.Hotkey.Modifiers, _settings.Hotkey.VirtualKey));
        _tray?.ShowInfo("设置已保存。");
        Log.Info(
            $"Settings updated. Save EXR: {_settings.SaveExr}; " +
            $"Preview: {_settings.PreviewQuality}; " +
            $"Save directory: {_settings.EffectiveSaveDirectory}");
    }

    private void OpenSaveFolder()
    {
        var directory = AppSettings.ResolveSaveDirectory(_settings.SaveDirectory);
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to open the save directory.", ex);
            _tray?.ShowError($"无法打开目录：{ex.Message}");
        }
    }

    private void ApplyAutoStart(bool enabled)
    {
        try
        {
            AutoStartManager.Apply(enabled);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update the auto start entry.", ex);
        }
    }
}
