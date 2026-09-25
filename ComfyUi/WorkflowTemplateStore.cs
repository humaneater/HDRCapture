using System.Text.Json.Nodes;
using HdrCapture.Infrastructure;

namespace HdrCapture.ComfyUi;

/// <summary>
/// Keeps HDRCapture's minimal ComfyUI prompt templates in its own application-data
/// directory. User workflows and ComfyUI settings are never read or modified.
/// </summary>
internal static class WorkflowTemplateStore
{
    public const string PortraitTemplateFileName = "PortraitApi.v1.2.json";
    private const string EmbeddedSuffix =
        ".Assets.ComfyWorkflows.PortraitApi.json";
    private static readonly object Gate = new();

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HDRCapture",
        "comfy-workflows");

    public static string PortraitTemplatePath =>
        Path.Combine(DirectoryPath, PortraitTemplateFileName);

    public static string EnsurePortraitTemplate()
    {
        lock (Gate)
        {
            var destination = PortraitTemplatePath;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var embedded = ReadEmbeddedTemplate();
                if (!File.Exists(destination) ||
                    !string.Equals(
                        File.ReadAllText(destination),
                        embedded,
                        StringComparison.Ordinal))
                {
                    WriteAtomic(destination, embedded);
                    Log.Info($"Updated built-in ComfyUI workflow template: {destination}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(
                    "Failed to persist the built-in ComfyUI workflow template; " +
                    "the embedded copy will be used.",
                    ex);
            }

            return destination;
        }
    }

    public static JsonObject LoadPortraitTemplate()
    {
        var path = EnsurePortraitTemplate();
        try
        {
            if (File.Exists(path) &&
                JsonNode.Parse(File.ReadAllText(path)) is JsonObject fromDisk)
            {
                return fromDisk;
            }
        }
        catch (Exception ex)
        {
            Log.Error("The local ComfyUI workflow template is invalid.", ex);
        }

        return JsonNode.Parse(ReadEmbeddedTemplate()) as JsonObject
            ?? throw new InvalidDataException("内置人像工作流不是 JSON 对象。");
    }

    internal static string ReadEmbeddedTemplate()
    {
        var assembly = typeof(WorkflowTemplateStore).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(EmbeddedSuffix, StringComparison.Ordinal))
            ?? throw new FileNotFoundException("程序资源中缺少内置人像工作流。");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("无法读取内置人像工作流。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteAtomic(string destination, string content)
    {
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, destination, overwrite: true);
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
}
