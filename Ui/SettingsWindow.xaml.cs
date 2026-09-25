using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HdrCapture.ComfyUi;
using HdrCapture.Configuration;
using HdrCapture.Core;
using Microsoft.Win32;

namespace HdrCapture.Ui;

internal partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly Func<int, int, bool> _tryRegisterHotkey;
    private HotkeySettings _hotkey;

    internal SettingsWindow(
        AppSettings settings,
        Func<int, int, bool> tryRegisterHotkey,
        SettingsTab initialTab = SettingsTab.General)
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
        DenoiseStrengthSlider.Value = settings.Denoise.Strength;
        DenoiseDetailSlider.Value = settings.Denoise.DetailPreservation;
        UseOidnBox.IsChecked = settings.Denoise.UseOidnForFinal;
        SaveDenoisedExrBox.IsChecked = settings.Denoise.SaveDenoisedExr;
        ComfyRootBox.Text = string.IsNullOrWhiteSpace(settings.ComfyUi.RootPath)
            ? ComfyUiPathValidator.FindDefaultRoot() ?? string.Empty
            : settings.ComfyUi.RootPath;
        ComfyBaseUrlBox.Text = settings.ComfyUi.BaseUrl;
        ComfyPortBox.Text = settings.ComfyUi.Port.ToString(CultureInfo.InvariantCulture);
        ComfyIdleBox.Text = settings.ComfyUi.IdleMinutes.ToString(CultureInfo.InvariantCulture);
        ComfyAutoStartBox.IsChecked = settings.ComfyUi.AutoStart;
        UpdateExposureText();
        UpdateDenoiseText();
        SettingsTabs.SelectedIndex = (int)initialTab;
        Loaded += (_, _) => ClampToWorkArea();
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

    private void OnHotkeyPreviewKeyUp(object sender, KeyEventArgs e) => e.Handled = true;

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择文件保存目录",
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

    private void OnBrowseComfyClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 ComfyUI 根目录",
            Multiselect = false
        };

        var current = ComfyRootBox.Text.Trim();
        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            ComfyRootBox.Text = dialog.FolderName;
            UpdateComfyStatus(ComfyUiPathValidator.Validate(dialog.FolderName));
        }
    }

    private void OnDetectComfyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ComfyRootBox.Text))
        {
            var detected = ComfyUiPathValidator.FindDefaultRoot();
            if (detected is not null)
            {
                ComfyRootBox.Text = detected;
            }
        }

        UpdateComfyStatus(ComfyUiPathValidator.Validate(ComfyRootBox.Text));
    }

    private void UpdateComfyStatus(ComfyUiValidationResult result)
    {
        if (result.IsValid && result.Installation is not null)
        {
            ComfyStatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            ComfyStatusText.Text =
                $"检测通过：{result.Installation.RootDirectory}" +
                (result.Warnings.Count == 0
                    ? string.Empty
                    : $"\n{string.Join("\n", result.Warnings)}");
            return;
        }

        ComfyStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
        ComfyStatusText.Text =
            result.Missing.Count == 0
                ? "路径无效。"
                : string.Join("\n", result.Missing);
    }

    private void OnExposureChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e) =>
        UpdateExposureText();

    private void UpdateExposureText()
    {
        if (ExposureText is not null)
        {
            ExposureText.Text = $"{ExposureSlider.Value:+0.0;-0.0;0.0} EV";
        }
    }

    private void OnDenoiseSliderChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e) =>
        UpdateDenoiseText();

    private void UpdateDenoiseText()
    {
        if (DenoiseStrengthText is null)
        {
            return;
        }

        DenoiseStrengthText.Text = $"{DenoiseStrengthSlider.Value:P0}";
        DenoiseDetailText.Text = $"{DenoiseDetailSlider.Value:P0}";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string resolvedDirectory;
        try
        {
            resolvedDirectory = AppSettings.ResolveSaveDirectory(DirectoryBox.Text.Trim());
            EnsureWritable(resolvedDirectory);
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

        if (!TryReadComfySettings(out var comfySettings, out var comfyError))
        {
            SettingsTabs.SelectedIndex = (int)SettingsTab.ComfyUi;
            MessageBox.Show(
                this,
                comfyError,
                "ComfyUI 设置",
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
        result.SaveDirectory = resolvedDirectory;
        result.ExposureEv = Math.Round(ExposureSlider.Value, 1);
        result.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        result.IncludeCursor = IncludeCursorBox.IsChecked == true;
        result.SaveExr = SaveExrBox.IsChecked == true;
        result.PreviewQuality = PreviewQualityBox.SelectedIndex switch
        {
            0 => PreviewQuality.Full,
            1 => PreviewQuality.Half,
            _ => PreviewQuality.Low
        };
        result.Denoise.Strength = DenoiseStrengthSlider.Value;
        result.Denoise.DetailPreservation = DenoiseDetailSlider.Value;
        result.Denoise.UseOidnForFinal = UseOidnBox.IsChecked == true;
        result.Denoise.SaveDenoisedExr = SaveDenoisedExrBox.IsChecked == true;
        result.Portrait.Denoise = DenoiseStrengthSlider.Value;
        result.Portrait.DetailPreservation = DenoiseDetailSlider.Value;
        result.ComfyUi = comfySettings;
        result.Normalize();
        Result = result;
        DialogResult = true;
    }

    private bool TryReadComfySettings(
        out ComfyUiSettings comfySettings,
        out string error)
    {
        comfySettings = _original.ComfyUi.Clone();
        error = string.Empty;
        var root = ComfyRootBox.Text.Trim();
        if (root.Length > 0)
        {
            var validation = ComfyUiPathValidator.Validate(root);
            if (!validation.IsValid || validation.Installation is null)
            {
                error = validation.Missing.Count == 0
                    ? "ComfyUI 路径无效。"
                    : string.Join("\n", validation.Missing);
                return false;
            }

            comfySettings.RootPath = validation.Installation.RootDirectory;
        }
        else
        {
            comfySettings.RootPath = string.Empty;
        }

        if (!int.TryParse(
                ComfyPortBox.Text.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port) ||
            port is < 1024 or > 65535)
        {
            error = "ComfyUI 端口必须是 1024 到 65535 之间的整数。";
            return false;
        }

        if (!int.TryParse(
                ComfyIdleBox.Text.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var idle) ||
            idle is < 1 or > 240)
        {
            error = "空闲关闭时间必须是 1 到 240 分钟。";
            return false;
        }

        var baseUrl = ComfyBaseUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = $"http://127.0.0.1:{port}";
        }

        comfySettings.Port = port;
        comfySettings.IdleMinutes = idle;
        comfySettings.BaseUrl = baseUrl;
        comfySettings.AutoStart = ComfyAutoStartBox.IsChecked == true;
        comfySettings.Normalize();
        if (!Uri.TryCreate(comfySettings.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Host != "localhost" && uri.Host != "127.0.0.1"))
        {
            error = "ComfyUI 服务地址只能是本机 localhost 或 127.0.0.1。";
            return false;
        }

        return true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void EnsureWritable(string directory)
    {
        Directory.CreateDirectory(directory);
        var probePath = Path.Combine(directory, ".hdrcapture-write-test");
        File.WriteAllBytes(probePath, [0]);
        File.Delete(probePath);
    }

    private void ClampToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
    }
}

internal enum SettingsTab
{
    General = 0,
    Denoise = 1,
    ComfyUi = 2
}
