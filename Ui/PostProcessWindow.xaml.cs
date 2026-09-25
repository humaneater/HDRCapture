using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using HdrCapture.ComfyUi;
using HdrCapture.Configuration;
using HdrCapture.Core;
using HdrCapture.Exr;
using HdrCapture.Imaging;
using HdrCapture.Infrastructure;
using Microsoft.Win32;

namespace HdrCapture.Ui;

internal partial class PostProcessWindow : Window
{
    private readonly CapturedImageSnapshot _snapshot;
    private readonly ComfyUiService _comfyUi;
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<AppSettings> _applySettings;
    private readonly Func<bool> _openComfySettings;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _operationCancellation;
    private BitmapSource? _originalPreview;
    private BitmapSource? _processedPreview;
    private PostProcessRenderResult? _finalResult;
    private BgraImage? _aiResult;
    private bool _initialized;
    private bool _closing;
    private int _previewGeneration;

    internal PostProcessWindow(
        CapturedImageSnapshot snapshot,
        ComfyUiService comfyUi,
        Func<AppSettings> getSettings,
        Action<AppSettings> applySettings,
        Func<bool> openComfySettings)
    {
        InitializeComponent();
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _comfyUi = comfyUi ?? throw new ArgumentNullException(nameof(comfyUi));
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _applySettings = applySettings ?? throw new ArgumentNullException(nameof(applySettings));
        _openComfySettings = openComfySettings ?? throw new ArgumentNullException(nameof(openComfySettings));

        var settings = _getSettings();
        SmoothSkinSlider.Value = settings.Portrait.SmoothSkin;
        DenoiseSlider.Value = settings.Denoise.Strength;
        BrightnessSlider.Value = settings.Portrait.BrightnessEv;
        DetailSlider.Value = settings.Denoise.DetailPreservation;
        _initialized = true;
        UpdateValueLabels();
        SaveExrButton.IsEnabled = settings.Denoise.SaveDenoisedExr;
        Loaded += (_, _) => ClampToWorkArea();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshPreviewAsync(original: true).ConfigureAwait(true);
        await RefreshPreviewAsync(original: false).ConfigureAwait(true);
        await RefreshModelsAsync(showErrors: false).ConfigureAwait(true);
        RefreshOptionalComponents();
    }

    private async void OnParameterChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized)
        {
            return;
        }

        UpdateValueLabels();
        _finalResult = null;
        _aiResult = null;
        await SchedulePreviewAsync().ConfigureAwait(true);
    }

    private void UpdateValueLabels()
    {
        if (SmoothSkinValueText is null)
        {
            return;
        }

        SmoothSkinValueText.Text = $"{SmoothSkinSlider.Value:P0}";
        DenoiseValueText.Text = $"{DenoiseSlider.Value:P0}";
        BrightnessValueText.Text = $"{BrightnessSlider.Value:+0.0;-0.0;0.0} EV";
        DetailValueText.Text = $"{DetailSlider.Value:P0}";
    }

    private async Task SchedulePreviewAsync()
    {
        if (_closing)
        {
            return;
        }

        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var cancellationToken = _previewCancellation.Token;
        var generation = Interlocked.Increment(ref _previewGeneration);
        try
        {
            await Task.Delay(140, cancellationToken).ConfigureAwait(true);
            var result = await Task.Run(
                    () => PostProcessPipeline.Render(
                        _snapshot.Image,
                        DenoiseSlider.Value,
                        DetailSlider.Value,
                        _getSettings().ExposureEv + BrightnessSlider.Value,
                        useOidn: true,
                        preview: true,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);
            if (_closing || cancellationToken.IsCancellationRequested ||
                generation != Volatile.Read(ref _previewGeneration))
            {
                return;
            }

            _processedPreview = result.Ldr.ToBitmapSource();
            if (ProcessedViewButton.IsChecked == true)
            {
                ShowPreview(_processedPreview);
            }

            StatusText.Text = result.Warning ?? "预览已更新。最终复制或保存时使用 OIDN 分块降噪。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Post-processing preview failed.", ex);
            StatusText.Text = $"预览失败：{ex.Message}";
        }
    }

    private async Task RefreshPreviewAsync(bool original)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            if (original)
            {
                var image = await Task.Run(
                        () => PostProcessPipeline.RenderOriginal(
                            _snapshot.Image,
                            _getSettings().ExposureEv,
                            preview: true,
                            cancellationToken))
                    .ConfigureAwait(true);
                _originalPreview = image.ToBitmapSource();
                if (OriginalViewButton.IsChecked == true)
                {
                    ShowPreview(_originalPreview);
                }
            }
            else
            {
                var result = await Task.Run(
                        () => PostProcessPipeline.Render(
                            _snapshot.Image,
                            DenoiseSlider.Value,
                            DetailSlider.Value,
                            _getSettings().ExposureEv + BrightnessSlider.Value,
                            useOidn: true,
                            preview: true,
                            cancellationToken))
                    .ConfigureAwait(true);
                _processedPreview = result.Ldr.ToBitmapSource();
                if (ProcessedViewButton.IsChecked == true)
                {
                    ShowPreview(_processedPreview);
                }
            }

            ImageInfoText.Text =
                $"{_snapshot.Region.Width} × {_snapshot.Region.Height} · " +
                $"{_snapshot.PrimarySdrWhiteNits:0.#} nit";
        }
        catch (Exception ex)
        {
            Log.Error("Post-processing initial preview failed.", ex);
            StatusText.Text = $"预览失败：{ex.Message}";
        }
    }

    private void OnPreviewViewChanged(object sender, RoutedEventArgs e)
    {
        if (PreviewImage is null)
        {
            return;
        }

        if (OriginalViewButton.IsChecked == true)
        {
            ShowPreview(_originalPreview);
        }
        else
        {
            ShowPreview(_aiResult?.ToBitmapSource() ?? _processedPreview);
        }
    }

    private void ShowPreview(BitmapSource? image)
    {
        PreviewImage.Source = image;
    }

    private async Task<PostProcessRenderResult> EnsureFinalAsync()
    {
        if (_finalResult is not null)
        {
            return _finalResult;
        }

        _previewCancellation?.Cancel();
        var useOidn = _getSettings().Denoise.UseOidnForFinal;
        var operation = BeginOperation(
            useOidn ? "正在执行分块 OIDN 降噪…" : "正在执行快速 HDR 降噪…");
        SetBusy(true);
        try
        {
            var result = await Task.Run(
                    () => PostProcessPipeline.Render(
                        _snapshot.Image,
                        DenoiseSlider.Value,
                        DetailSlider.Value,
                        _getSettings().ExposureEv + BrightnessSlider.Value,
                        useOidn: useOidn,
                        preview: false,
                        operation.Token),
                    operation.Token)
                .ConfigureAwait(true);
            _finalResult = result;
            StatusText.Text = result.Warning ?? "最终图像已就绪。";
            return result;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnPortraitEnhanceClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureComfyConfigured())
            {
                return;
            }

            if (CheckpointBox.Items.Count == 0)
            {
                await RefreshModelsAsync(showErrors: false).ConfigureAwait(true);
            }

            var checkpoint = CheckpointBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(checkpoint))
            {
                MessageBox.Show(
                    this,
                    "请先选择一个检查点模型。",
                    "HDRCapture",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var final = await EnsureFinalAsync().ConfigureAwait(true);
            var operation = BeginOperation("正在连接 ComfyUI…");
            SetBusy(true);
            try
            {
                var progress = new Progress<string>(text => StatusText.Text = text);
                var result = await _comfyUi.GeneratePortraitAsync(
                        final.Ldr,
                        _getSettings(),
                        checkpoint,
                        SmoothSkinSlider.Value,
                        DetailSlider.Value,
                        progress,
                        operation.Token)
                    .ConfigureAwait(true);

                if (!result.FaceDetected || result.Image is null)
                {
                    _aiResult = BgraProcessor.Sharpen(
                        final.Ldr,
                        0.20 + (DetailSlider.Value * 0.35));
                    StatusText.Text = result.Message;
                }
                else
                {
                    _aiResult = BgraProcessor.ApplyDetailPreservation(
                        final.Ldr,
                        result.Image,
                        DetailSlider.Value);
                    StatusText.Text = result.Message;
                }

                ProcessedViewButton.IsChecked = true;
                ShowPreview(_aiResult.ToBitmapSource());
            }
            finally
            {
                SetBusy(false);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消人像美化。";
        }
        catch (Exception ex)
        {
            Log.Error("Portrait enhancement failed.", ex);
            MessageBox.Show(
                this,
                ex.Message,
                "人像美化失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusText.Text = $"人像美化失败：{ex.Message}";
        }
    }

    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e)
    {
        await RefreshModelsAsync(showErrors: true).ConfigureAwait(true);
    }

    private void RefreshOptionalComponents()
    {
        var validation = ComfyUiService.Validate(_getSettings().ComfyUi);
        if (!validation.IsValid || validation.Installation is null)
        {
            IpAdapterStatusText.Text = "配置 ComfyUI 后可检查可选身份保护组件。";
            InstallIpAdapterButton.Visibility = Visibility.Collapsed;
            return;
        }

        var status = ComfyUiOptionalComponents.Inspect(validation.Installation);
        if (status.IsInstalled)
        {
            IpAdapterStatusText.Text =
                $"已启用：{Path.GetFileName(status.IpAdapterPath)}";
            InstallIpAdapterButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (!status.NodeAvailable)
        {
            IpAdapterStatusText.Text =
                "未安装 ComfyUI_IPAdapter_plus 节点；人像美化仍会使用 FaceDetailer。";
            InstallIpAdapterButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (string.IsNullOrWhiteSpace(status.ClipVisionPath))
        {
            IpAdapterStatusText.Text =
                "缺少 CLIP-Vision 编码器；请在 ComfyUI 的 models\\clip_vision 中配置后重试。";
            InstallIpAdapterButton.Visibility = Visibility.Collapsed;
            return;
        }

        IpAdapterStatusText.Text =
            "未安装。FaceDetailer 可正常使用，补充后会更稳定地保留人物身份。";
        InstallIpAdapterButton.Visibility = Visibility.Visible;
    }

    private async void OnInstallIpAdapterClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureComfyConfigured())
            {
                return;
            }

            var operation = BeginOperation("正在获取 IP-Adapter Face 下载信息…");
            SetBusy(true);
            try
            {
                var download = await ComfyUiOptionalComponents
                    .ResolveIpAdapterFaceDownloadAsync(
                        _getSettings().ComfyUi,
                        operation.Token)
                    .ConfigureAwait(true);
                var confirmation = MessageBox.Show(
                    this,
                    $"将安装 {download.DisplayName}。\n\n" +
                    $"来源：{download.SourceUri}\n" +
                    $"大小：{FormatBytes(download.Size)}\n" +
                    $"SHA-256：{download.Sha256}\n" +
                    $"目标：{download.DestinationPath}\n\n" +
                    "下载完成后会校验 SHA-256；校验失败不会替换现有文件。是否继续？",
                    "安装可选身份保护",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (confirmation != MessageBoxResult.Yes)
                {
                    StatusText.Text = "已取消安装 IP-Adapter Face。";
                    return;
                }

                var progress = new Progress<ComfyUiDownloadProgress>(value =>
                {
                    StatusText.Text =
                        $"正在下载 IP-Adapter Face… {value.Fraction:P0} " +
                        $"({FormatBytes(value.BytesReceived)} / {FormatBytes(value.TotalBytes)})";
                });
                await ComfyUiOptionalComponents.DownloadAsync(
                        download,
                        progress,
                        operation.Token)
                    .ConfigureAwait(true);
                RefreshOptionalComponents();
                StatusText.Text = "IP-Adapter Face 已安装，下次人像美化会自动启用。";
            }
            finally
            {
                SetBusy(false);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消安装 IP-Adapter Face。";
        }
        catch (Exception ex)
        {
            Log.Error("Failed to install the optional IP-Adapter Face model.", ex);
            MessageBox.Show(
                this,
                ex.Message,
                "IP-Adapter Face 安装失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusText.Text = $"可选组件安装失败：{ex.Message}";
        }
    }

    private async Task RefreshModelsAsync(bool showErrors)
    {
        try
        {
            var settings = _getSettings();
            var current = CheckpointBox.SelectedItem as string ??
                          settings.Portrait.Checkpoint;
            var checkpoints = await Task.Run(
                    () => _comfyUi.GetCheckpointsAsync(settings.ComfyUi))
                .ConfigureAwait(true);
            CheckpointBox.ItemsSource = checkpoints;
            if (!string.IsNullOrWhiteSpace(current) &&
                checkpoints.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                CheckpointBox.SelectedItem = checkpoints.First(
                    item => item.Equals(current, StringComparison.OrdinalIgnoreCase));
            }
            else if (checkpoints.Count > 0)
            {
                CheckpointBox.SelectedIndex = 0;
            }

            if (checkpoints.Count == 0)
            {
                StatusText.Text = "没有发现可用的检查点模型。";
            }
        }
        catch (Exception ex)
        {
            if (CheckpointBox.Items.Count == 0 &&
                !string.IsNullOrWhiteSpace(_getSettings().Portrait.Checkpoint))
            {
                CheckpointBox.ItemsSource = new[] { _getSettings().Portrait.Checkpoint };
                CheckpointBox.SelectedIndex = 0;
            }

            StatusText.Text = $"ComfyUI 模型列表不可用：{ex.Message}";
            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "ComfyUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private bool EnsureComfyConfigured()
    {
        var validation = ComfyUiService.Validate(_getSettings().ComfyUi);
        if (validation.IsValid)
        {
            return true;
        }

        var details = validation.Missing.Count == 0
            ? "ComfyUI 配置无效。"
            : string.Join(Environment.NewLine, validation.Missing);
        MessageBox.Show(
            this,
            $"首次使用需要配置本机 ComfyUI。{Environment.NewLine}{Environment.NewLine}{details}",
            "HDRCapture",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return _openComfySettings() &&
               ComfyUiService.Validate(_getSettings().ComfyUi).IsValid;
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var image = await GetOutputImageAsync().ConfigureAwait(true);
            await Task.Run(() => ClipboardWriter.SetImage(image)).ConfigureAwait(true);
            StatusText.Text = "已复制到剪贴板。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            Log.Error("Failed to copy the post-processed image.", ex);
            MessageBox.Show(
                this,
                ex.Message,
                "复制失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnSavePngClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var image = await GetOutputImageAsync().ConfigureAwait(true);
            var directory = AppSettings.ResolveSaveDirectory(_getSettings().SaveDirectory);
            Directory.CreateDirectory(directory);
            var dialog = new SaveFileDialog
            {
                Title = "保存 PNG",
                InitialDirectory = directory,
                FileName = $"HDRCapture_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                Filter = "PNG 图片 (*.png)|*.png",
                DefaultExt = ".png",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            await Task.Run(() => WritePngAtomic(dialog.FileName, image)).ConfigureAwait(true);
            StatusText.Text = $"已保存 {Path.GetFileName(dialog.FileName)}。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save the post-processed PNG.", ex);
            MessageBox.Show(
                this,
                ex.Message,
                "保存失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnSaveExrClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await EnsureFinalAsync().ConfigureAwait(true);
            var directory = AppSettings.ResolveSaveDirectory(_getSettings().SaveDirectory);
            Directory.CreateDirectory(directory);
            var fileName =
                $"HDR_denoised_{_snapshot.CapturedAt:yyyyMMdd_HHmmss_fff}.exr";
            var path = CaptureFileNaming.EnsureUniquePath(directory, fileName);
            var metadata = new ExrMetadata(
                _snapshot.CapturedAt,
                _snapshot.Region,
                _snapshot.MonitorSummary,
                _snapshot.PrimarySdrWhiteNits,
                _getSettings().ExposureEv,
                "HDRCapture v1.2 OIDN denoise");
            await Task.Run(
                    () => OpenExrWriter.WriteAtomic(path, result.Hdr, metadata))
                .ConfigureAwait(true);
            StatusText.Text = $"已保存 {Path.GetFileName(path)}。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save the denoised EXR.", ex);
            MessageBox.Show(
                this,
                ex.Message,
                "保存 EXR 失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<BgraImage> GetOutputImageAsync()
    {
        if (_aiResult is not null)
        {
            return _aiResult;
        }

        return (await EnsureFinalAsync().ConfigureAwait(true)).Ldr;
    }

    private CancellationTokenSource BeginOperation(string status)
    {
        var previous = _operationCancellation;
        _operationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        CancelButton.IsEnabled = true;
        StatusText.Text = status;
        return _operationCancellation;
    }

    private void SetBusy(bool busy)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
        PortraitEnhanceButton.IsEnabled = !busy;
        RefreshModelsButton.IsEnabled = !busy;
        InstallIpAdapterButton.IsEnabled = !busy;
        OpenComfyButton.IsEnabled = !busy;
        CopyButton.IsEnabled = !busy;
        SavePngButton.IsEnabled = !busy;
        SaveExrButton.IsEnabled = !busy && _getSettings().Denoise.SaveDenoisedExr;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        StatusText.Text = "正在取消…";
    }

    private async void OnOpenComfySettingsClick(object sender, RoutedEventArgs e)
    {
        if (_openComfySettings())
        {
            await RefreshModelsAsync(showErrors: false).ConfigureAwait(true);
            RefreshOptionalComponents();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();

        var settings = _getSettings().Clone();
        settings.Denoise.Strength = DenoiseSlider.Value;
        settings.Denoise.DetailPreservation = DetailSlider.Value;
        settings.Portrait.SmoothSkin = SmoothSkinSlider.Value;
        settings.Portrait.Denoise = DenoiseSlider.Value;
        settings.Portrait.BrightnessEv = BrightnessSlider.Value;
        settings.Portrait.DetailPreservation = DetailSlider.Value;
        settings.Portrait.Checkpoint =
            CheckpointBox.SelectedItem as string ?? settings.Portrait.Checkpoint;
        settings.Normalize();
        _applySettings(settings);
    }

    private static void WritePngAtomic(string path, BgraImage image)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("PNG 保存路径无效。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, image.EncodePng());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return $"{display:0.##} {units[unit]}";
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
