using System.Windows;
using System.Windows.Input;
using HdrCapture.Configuration;
using HdrCapture.Core;
using Microsoft.Win32;

namespace HdrCapture.Ui;

internal partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly Func<int, int, bool> _tryRegisterHotkey;
    private HotkeySettings _hotkey;

    internal SettingsWindow(AppSettings settings, Func<int, int, bool> tryRegisterHotkey)
    {
        InitializeComponent();

        _original = settings;
        _tryRegisterHotkey = tryRegisterHotkey;
        _hotkey = new HotkeySettings
        {
            Modifiers = settings.Hotkey.Modifiers,
            VirtualKey = settings.Hotkey.VirtualKey
        };

        HotkeyBox.Text = HotkeyFormatter.Format(_hotkey.Modifiers, _hotkey.VirtualKey);
        DirectoryBox.Text = settings.EffectiveSaveDirectory;
        ExposureSlider.Value = settings.ExposureEv;
        StartWithWindowsBox.IsChecked = settings.StartWithWindows;
        IncludeCursorBox.IsChecked = settings.IncludeCursor;
        SaveExrBox.IsChecked = settings.SaveExr;
        PreviewQualityBox.SelectedIndex = (int)settings.PreviewQuality;
        UpdateSaveExrState();
        UpdateExposureText();
    }

    internal AppSettings? Result { get; private set; }

    private void OnHotkeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var modifiers = (int)Keyboard.Modifiers;
        if (!HotkeyFormatter.IsAllowed(modifiers, virtualKey))
        {
            return;
        }

        _hotkey = new HotkeySettings { Modifiers = modifiers, VirtualKey = virtualKey };
        HotkeyBox.Text = HotkeyFormatter.Format(modifiers, virtualKey);
    }

    private void OnHotkeyPreviewKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 EXR 保存目录",
            Multiselect = false
        };

        var current = DirectoryBox.Text.Trim();
        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            DirectoryBox.Text = dialog.FolderName;
        }
    }

    private void OnExposureChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateExposureText();

    private void OnSaveExrChanged(object sender, RoutedEventArgs e) =>
        UpdateSaveExrState();

    private void UpdateSaveExrState()
    {
        if (DirectoryBox is null || BrowseButton is null || SaveExrHelpText is null ||
            DirectoryHelpText is null || DirectoryLabel is null)
        {
            return;
        }

        var saveExr = SaveExrBox.IsChecked == true;
        DirectoryBox.IsEnabled = saveExr;
        BrowseButton.IsEnabled = saveExr;
        DirectoryLabel.Opacity = saveExr ? 1.0 : 0.55;
        SaveExrHelpText.Text = saveExr
            ? "同时保存 EXR 文件，并复制 LDR 图片到剪贴板。"
            : "默认只复制 LDR 图片到剪贴板，不写入 EXR 文件。";
        DirectoryHelpText.Text = saveExr
            ? "EXR 会保存在此目录，目录不存在时自动创建。"
            : "当前不会写入此目录；勾选上方选项后可修改。";
    }

    private void UpdateExposureText()
    {
        if (ExposureText is null)
        {
            return;
        }

        ExposureText.Text = $"{ExposureSlider.Value:+0.0;-0.0;0.0} EV";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var saveExr = SaveExrBox.IsChecked == true;
        var directory = DirectoryBox.Text.Trim();
        if (saveExr && string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show(this, "请填写保存目录。", "HDRCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string resolved = string.Empty;
        if (saveExr)
        {
            try
            {
                resolved = AppSettings.ResolveSaveDirectory(directory);
                EnsureWritable(resolved);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"目录不可用：{ex.Message}",
                    "HDRCapture",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            try
            {
                resolved = AppSettings.NormalizeDirectory(directory);
            }
            catch
            {
                resolved = _original.SaveDirectory;
            }
        }

        if (!_tryRegisterHotkey(_hotkey.Modifiers, _hotkey.VirtualKey))
        {
            MessageBox.Show(
                this,
                "该快捷键已被其他程序占用，请换一个组合键。",
                "HDRCapture",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var result = _original.Clone();
        result.Hotkey = new HotkeySettings
        {
            Modifiers = _hotkey.Modifiers,
            VirtualKey = _hotkey.VirtualKey
        };
        result.SaveDirectory = resolved;
        result.ExposureEv = Math.Round(ExposureSlider.Value, 1);
        result.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        result.IncludeCursor = IncludeCursorBox.IsChecked == true;
        result.SaveExr = saveExr;
        result.PreviewQuality = PreviewQualityBox.SelectedIndex switch
        {
            0 => PreviewQuality.Full,
            1 => PreviewQuality.Half,
            _ => PreviewQuality.Low
        };
        result.Normalize();
        Result = result;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void EnsureWritable(string directory)
    {
        Directory.CreateDirectory(directory);
        var probePath = Path.Combine(directory, ".hdrcapture-write-test");
        File.WriteAllBytes(probePath, [0]);
        File.Delete(probePath);
    }
}
