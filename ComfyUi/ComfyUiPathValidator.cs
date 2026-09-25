using HdrCapture.Configuration;

namespace HdrCapture.ComfyUi;

internal sealed record ComfyUiInstallation(
    string RootDirectory,
    string ComfyUiDirectory,
    string MainPythonFile,
    string PythonExecutable,
    string CheckpointDirectory,
    string UltralyticsDirectory,
    string SamDirectory,
    string InputDirectory,
    string OutputDirectory);

internal sealed record ComfyUiValidationResult(
    ComfyUiInstallation? Installation,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Installation is not null && Missing.Count == 0;
}

internal static class ComfyUiPathValidator
{
    private const string ImpactPackRelativePath =
        @"ComfyUI\custom_nodes\ComfyUI-Impact-Pack";

    public static string? FindDefaultRoot()
    {
        var candidates = new[]
        {
            @"D:\AI\ComfyUI",
            Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\ComfyUI"),
            Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Documents\ComfyUI"),
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Programs\ComfyUI")
        };

        return candidates.FirstOrDefault(candidate => Validate(candidate).IsValid);
    }

    public static ComfyUiValidationResult Validate(string? configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return new ComfyUiValidationResult(null, ["尚未配置 ComfyUI 根目录。"], []);
        }

        string configured;
        try
        {
            configured = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredRoot.Trim()));
        }
        catch (Exception ex)
        {
            return new ComfyUiValidationResult(null, [$"路径无效：{ex.Message}"], []);
        }

        var candidates = new[] { configured, Path.Combine(configured, "ComfyUI") };
        foreach (var candidate in candidates)
        {
            var result = ValidateCandidate(candidate);
            if (result.IsValid)
            {
                return result;
            }
        }

        return ValidateCandidate(configured);
    }

    public static ComfyUiValidationResult Validate(ComfyUiSettings settings) =>
        Validate(settings.RootPath);

    private static ComfyUiValidationResult ValidateCandidate(string root)
    {
        var missing = new List<string>();
        var warnings = new List<string>();
        var comfyDirectory = Path.Combine(root, "ComfyUI");
        var main = Path.Combine(comfyDirectory, "main.py");
        var python = Path.Combine(root, "python_embeded", "python.exe");

        if (!Directory.Exists(root))
        {
            missing.Add($"根目录不存在：{root}");
        }

        if (!File.Exists(main))
        {
            missing.Add(@"缺少 ComfyUI\main.py");
        }

        if (!File.Exists(python))
        {
            missing.Add(@"缺少 python_embeded\python.exe");
        }

        if (!Directory.Exists(Path.Combine(root, ImpactPackRelativePath)))
        {
            missing.Add("缺少 ComfyUI-Impact-Pack");
        }

        var ultralytics = Path.Combine(comfyDirectory, "models", "ultralytics", "bbox");
        var faceModel = Path.Combine(ultralytics, "face_yolov8m.pt");
        if (!File.Exists(faceModel))
        {
            missing.Add(@"缺少 models\ultralytics\bbox\face_yolov8m.pt");
        }

        var samDirectory = Path.Combine(comfyDirectory, "models", "sams");
        var samModel = Path.Combine(samDirectory, "sam_vit_b_01ec64.pth");
        if (!File.Exists(samModel))
        {
            missing.Add(@"缺少 models\sams\sam_vit_b_01ec64.pth");
        }

        var checkpoints = Path.Combine(comfyDirectory, "models", "checkpoints");
        if (!Directory.Exists(checkpoints))
        {
            missing.Add(@"缺少 models\checkpoints");
        }
        else if (!Directory.EnumerateFiles(
                     checkpoints,
                     "*.*",
                     SearchOption.AllDirectories)
                 .Any(IsCheckpointFile))
        {
            warnings.Add("检查点目录中没有可用的模型文件。");
        }

        if (missing.Count > 0)
        {
            return new ComfyUiValidationResult(null, missing, warnings);
        }

        var installation = new ComfyUiInstallation(
            root,
            comfyDirectory,
            main,
            python,
            checkpoints,
            ultralytics,
            samDirectory,
            Path.Combine(comfyDirectory, "input"),
            Path.Combine(comfyDirectory, "output"));
        var optional = ComfyUiOptionalComponents.Inspect(installation);
        if (!optional.IsInstalled)
        {
            warnings.Add(
                "可选 IP-Adapter Face 未安装；人像美化仍可使用 FaceDetailer，" +
                "但身份一致性保护会较弱。");
        }

        return new ComfyUiValidationResult(installation, [], warnings);
    }

    public static bool IsCheckpointFile(string path) =>
        Path.GetExtension(path).Equals(".safetensors", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".ckpt", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".pt", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".pth", StringComparison.OrdinalIgnoreCase);
}
