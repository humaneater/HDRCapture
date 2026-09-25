using System.Diagnostics;
using System.Text.Json.Nodes;
using HdrCapture.Configuration;
using HdrCapture.Imaging;
using HdrCapture.Infrastructure;

namespace HdrCapture.ComfyUi;

internal sealed record PortraitGenerationResult(
    BgraImage? Image,
    bool FaceDetected,
    string Message);

internal sealed record ComfyUiRuntimeCapabilities(
    bool IsReady,
    bool FaceDetailerAvailable,
    bool IpAdapterAdvancedAvailable,
    IReadOnlyList<string> Checkpoints,
    string? Error);

/// <summary>
/// Owns the optional local ComfyUI process. Existing services are always reused and never
/// stopped; only a process launched by HDRCapture is eligible for idle shutdown.
/// </summary>
internal sealed class ComfyUiService : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(4);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Timer _idleTimer;
    private readonly object _stateGate = new();
    private Process? _ownedProcess;
    private ComfyUiSettings? _ownedSettings;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private int _idleMinutes = 10;
    private bool _disposed;

    public ComfyUiService()
    {
        _idleTimer = new Timer(
            _ => _ = StopOwnedProcessIfIdleAsync(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    public static ComfyUiValidationResult Validate(ComfyUiSettings settings) =>
        ComfyUiPathValidator.Validate(settings);

    public async Task<bool> IsReadyAsync(
        ComfyUiSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(settings);
        var ready = await client.IsReadyAsync(cancellationToken).ConfigureAwait(false);
        if (ready)
        {
            Touch(settings.IdleMinutes);
        }

        return ready;
    }

    public bool TryGetOwnedProcess(out int processId)
    {
        lock (_stateGate)
        {
            if (_ownedProcess is { } process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        processId = process.Id;
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        processId = 0;
        return false;
    }

    public async Task<ComfyUiRuntimeCapabilities> ProbeAsync(
        ComfyUiSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(settings);
        if (!await client.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return new ComfyUiRuntimeCapabilities(false, false, false, [], null);
        }

        Touch(settings.IdleMinutes);
        try
        {
            var objectInfo = await client.GetObjectInfoAsync(cancellationToken)
                .ConfigureAwait(false);
            return new ComfyUiRuntimeCapabilities(
                true,
                objectInfo.ContainsKey("FaceDetailer"),
                objectInfo.ContainsKey("IPAdapterAdvanced"),
                ParseCheckpointNames(objectInfo),
                null);
        }
        catch (Exception ex)
        {
            return new ComfyUiRuntimeCapabilities(true, false, false, [], ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> GetCheckpointsAsync(
        ComfyUiSettings settings,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(settings);
        using var client = CreateClient(settings);
        if (await client.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            Touch(settings.IdleMinutes);
            var objectInfo = await client.GetObjectInfoAsync(cancellationToken)
                .ConfigureAwait(false);
            return ParseCheckpointNames(objectInfo);
        }

        if (!validation.IsValid || validation.Installation is null)
        {
            throw new InvalidOperationException(FormatValidationError(validation));
        }

        return Directory.EnumerateFiles(
                validation.Installation.CheckpointDirectory,
                "*.*",
                SearchOption.AllDirectories)
            .Where(ComfyUiPathValidator.IsCheckpointFile)
            .Select(path => Path.GetRelativePath(
                    validation.Installation.CheckpointDirectory,
                    path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<PortraitGenerationResult> GeneratePortraitAsync(
        BgraImage source,
        AppSettings settings,
        string checkpoint,
        double smoothSkin,
        double detailPreservation,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(checkpoint))
        {
            throw new InvalidOperationException("请选择一个检查点模型。");
        }

        var comfySettings = settings.ComfyUi;
        var validation = Validate(comfySettings);
        if (!validation.IsValid || validation.Installation is null)
        {
            throw new InvalidOperationException(FormatValidationError(validation));
        }

        await EnsureServerAsync(comfySettings, validation.Installation, progress, cancellationToken)
            .ConfigureAwait(false);
        using var client = CreateClient(comfySettings);
        var installedCheckpoints = ParseCheckpointNames(
            await client.GetObjectInfoAsync(cancellationToken).ConfigureAwait(false));
        if (!installedCheckpoints.Contains(checkpoint, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ComfyUI 中找不到检查点 {checkpoint}，请刷新模型列表。");
        }

        var runId = Guid.NewGuid().ToString("N");
        var uploadName = $"HDRCapture_input_{runId}.png";
        ComfyUiUploadResult? upload = null;
        ComfyUiImageReference? finalImage = null;
        ComfyUiImageReference? maskImage = null;
        string? promptId = null;
        try
        {
            progress?.Report("正在上传处理后的 LDR 图片…");
            upload = await client.UploadImageAsync(
                    source.EncodePng(),
                    uploadName,
                    cancellationToken)
                .ConfigureAwait(false);
            Touch(comfySettings.IdleMinutes);

            var smooth = Math.Clamp(smoothSkin, 0.0, 1.0);
            var detail = Math.Clamp(detailPreservation, 0.0, 1.0);
            var optional = ComfyUiOptionalComponents.Inspect(validation.Installation);
            var useIpAdapter =
                settings.Portrait.UseIpAdapter &&
                optional.IsInstalled &&
                optional.NodeAvailable;
            var parameters = new PortraitWorkflowParameters(
                checkpoint,
                upload.Filename,
                Random.Shared.NextInt64(1, long.MaxValue),
                0.18 + (smooth * 0.42),
                (int)Math.Round(5 + ((1 - detail) * 15)),
                8 + (int)Math.Round((1 - detail) * 20),
                0.20 + (smooth * 0.80),
                $"HDRCapture_final_{runId}",
                $"HDRCapture_mask_{runId}",
                useIpAdapter,
                optional.IpAdapterRelativePath,
                optional.ClipVisionRelativePath);
            var workflow = ComfyUiWorkflowBuilder.BuildPortraitWorkflow(parameters);

            progress?.Report("正在提交人像美化任务…");
            promptId = await client.QueuePromptAsync(
                    workflow,
                    Environment.ProcessId.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);
            Touch(comfySettings.IdleMinutes);

            var history = await client.WaitForCompletionAsync(
                    promptId,
                    text => progress?.Report(text),
                    cancellationToken)
                .ConfigureAwait(false);
            Touch(comfySettings.IdleMinutes);

            finalImage = FindImage(history, "31");
            maskImage = FindImage(history, "11");
            if (finalImage is null)
            {
                throw new InvalidDataException("ComfyUI 已完成，但没有返回最终图片。");
            }

            var faceDetected = true;
            if (maskImage is not null)
            {
                try
                {
                    var mask = BgraImage.FromPng(
                        await client.DownloadImageAsync(maskImage, cancellationToken)
                            .ConfigureAwait(false));
                    faceDetected = BgraProcessor.HasVisiblePixels(mask);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to inspect the FaceDetailer mask; assuming a face exists.", ex);
                }
            }

            if (!faceDetected)
            {
                return new PortraitGenerationResult(
                    null,
                    false,
                    "未检测到人脸，已执行整体轻处理。");
            }

            progress?.Report("正在下载人像美化结果…");
            var result = BgraImage.FromPng(
                await client.DownloadImageAsync(finalImage, cancellationToken)
                    .ConfigureAwait(false));
            if (result.Width != source.Width || result.Height != source.Height)
            {
                throw new InvalidDataException(
                    $"ComfyUI 输出尺寸 {result.Width}x{result.Height} 与原图 " +
                    $"{source.Width}x{source.Height} 不一致。");
            }

            return new PortraitGenerationResult(result, true, "人像美化完成。");
        }
        catch (OperationCanceledException)
        {
            if (promptId is not null)
            {
                await client.InterruptAsync(promptId, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (promptId is not null)
            {
                await client.DeleteHistoryAsync([promptId], CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (upload is not null)
            {
                DeleteComfyFile(
                    validation.Installation.InputDirectory,
                    upload.Subfolder,
                    upload.Filename);
            }

            if (finalImage is not null)
            {
                DeleteComfyFile(
                    validation.Installation.OutputDirectory,
                    finalImage.Subfolder,
                    finalImage.Filename);
            }

            if (maskImage is not null)
            {
                DeleteComfyFile(
                    validation.Installation.OutputDirectory,
                    maskImage.Subfolder,
                    maskImage.Filename);
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
        _idleTimer.Dispose();

        Process? process;
        lock (_stateGate)
        {
            process = _ownedProcess;
            _ownedProcess = null;
            _ownedSettings = null;
        }

        StopProcess(process);
    }

    private async Task EnsureServerAsync(
        ComfyUiSettings settings,
        ComfyUiInstallation installation,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = CreateClient(settings);
            if (await client.IsReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                Touch(settings.IdleMinutes);
                return;
            }

            if (!settings.AutoStart)
            {
                throw new InvalidOperationException(
                    "ComfyUI 服务未运行，且设置中已关闭自动启动。");
            }

            Process? process;
            lock (_stateGate)
            {
                process = _ownedProcess;
                if (process is not null && process.HasExited)
                {
                    process.Dispose();
                    process = null;
                    _ownedProcess = null;
                }
            }

            if (process is null)
            {
                process = StartProcess(installation, settings.Port);
                lock (_stateGate)
                {
                    _ownedProcess = process;
                    _ownedSettings = settings.Clone();
                }
            }

            progress?.Report("正在启动隐藏的 ComfyUI 服务…");
            var started = Stopwatch.StartNew();
            var delay = 400;
            while (started.Elapsed < StartupTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"ComfyUI 启动失败，进程已退出（代码 {process.ExitCode}）。请查看日志。");
                }

                if (await client.IsReadyAsync(cancellationToken).ConfigureAwait(false))
                {
                    Touch(settings.IdleMinutes);
                    progress?.Report("ComfyUI 已就绪。");
                    return;
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = Math.Min(1500, delay + 200);
            }

            throw new TimeoutException("ComfyUI 启动超时，请检查路径、端口和自定义节点。");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private Process StartProcess(ComfyUiInstallation installation, int port)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installation.PythonExecutable,
            WorkingDirectory = installation.ComfyUiDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("main.py");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--disable-auto-launch");
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var outputLines = 0;
        void LogLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line) || Interlocked.Increment(ref outputLines) > 160)
            {
                return;
            }

            Log.Info($"[ComfyUI] {line}");
        }

        process.OutputDataReceived += (_, args) => LogLine(args.Data);
        process.ErrorDataReceived += (_, args) => LogLine(args.Data);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("无法启动 ComfyUI 进程。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Log.Info(
            $"Started hidden ComfyUI process {process.Id} from {installation.RootDirectory} " +
            $"on port {port}.");
        return process;
    }

    private async Task StopOwnedProcessIfIdleAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!await _lifecycleGate.WaitAsync(TimeSpan.Zero).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            Process? process;
            ComfyUiSettings? settings;
            lock (_stateGate)
            {
                process = _ownedProcess;
                settings = _ownedSettings;
                if (process is not null && process.HasExited)
                {
                    process.Dispose();
                    process = null;
                    _ownedProcess = null;
                    _ownedSettings = null;
                }
            }

            if (process is null || settings is null || _disposed)
            {
                return;
            }

            var idleMinutes = settings.IdleMinutes;
            var idle = DateTime.UtcNow - _lastActivityUtc;
            if (idle < TimeSpan.FromMinutes(idleMinutes))
            {
                return;
            }

            using (var client = CreateClient(settings))
            {
                await client.InterruptAsync(null, CancellationToken.None).ConfigureAwait(false);
            }

            lock (_stateGate)
            {
                _ownedProcess = null;
                _ownedSettings = null;
            }

            StopProcess(process);
            Log.Info($"Stopped idle ComfyUI process after {idle.TotalMinutes:0.0} minutes.");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to stop the idle ComfyUI process.", ex);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static ComfyUiClient CreateClient(ComfyUiSettings settings)
    {
        settings.Normalize();
        return new ComfyUiClient(settings.BaseUrl);
    }

    private void Touch(int idleMinutes)
    {
        lock (_stateGate)
        {
            _lastActivityUtc = DateTime.UtcNow;
            _idleMinutes = Math.Clamp(idleMinutes, 1, 240);
        }
    }

    private static IReadOnlyList<string> ParseCheckpointNames(JsonObject objectInfo)
    {
        if (objectInfo["CheckpointLoaderSimple"] is not JsonObject checkpointNode ||
            checkpointNode["input"]?["required"]?["ckpt_name"] is not JsonArray definition ||
            definition.Count == 0)
        {
            return [];
        }

        JsonArray? names = definition[0] as JsonArray;
        if (names is null &&
            definition.Count > 1 &&
            definition[1] is JsonObject options &&
            options["options"] is JsonArray optionNames)
        {
            names = optionNames;
        }

        return names?
            .Select(node => node?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];
    }

    private static ComfyUiImageReference? FindImage(JsonObject history, string nodeId)
    {
        if (history["outputs"] is not JsonObject outputs ||
            outputs[nodeId] is not JsonObject node ||
            node["images"] is not JsonArray images ||
            images.Count == 0 ||
            images[0] is not JsonObject image ||
            image["filename"] is not JsonValue filenameValue ||
            !filenameValue.TryGetValue<string>(out var filename))
        {
            return null;
        }

        return new ComfyUiImageReference(
            filename,
            image["subfolder"]?.GetValue<string>() ?? string.Empty,
            image["type"]?.GetValue<string>() ?? "output");
    }

    private static void DeleteComfyFile(string root, string subfolder, string filename)
    {
        try
        {
            var rootPath = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(
                Path.Combine(rootPath, subfolder ?? string.Empty, filename));
            if (!candidate.StartsWith(
                    rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to delete temporary ComfyUI file {filename}.", ex);
        }
    }

    private static string FormatValidationError(ComfyUiValidationResult validation)
    {
        if (validation.Missing.Count == 0)
        {
            return "ComfyUI 配置无效。";
        }

        return "ComfyUI 配置不完整：" + string.Join("；", validation.Missing);
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to stop the owned ComfyUI process.", ex);
        }
        finally
        {
            process.Dispose();
        }
    }
}
