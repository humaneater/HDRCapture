using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HdrCapture.Configuration;

namespace HdrCapture.ComfyUi;

internal sealed record ComfyUiIpAdapterStatus(
    bool IsInstalled,
    string? IpAdapterPath,
    string? ClipVisionPath,
    string IpAdapterRelativePath,
    string ClipVisionRelativePath,
    bool NodeAvailable);

internal sealed record ComfyUiModelDownload(
    string DisplayName,
    Uri SourceUri,
    long Size,
    string Sha256,
    string DestinationPath);

internal sealed record ComfyUiDownloadProgress(
    long BytesReceived,
    long TotalBytes)
{
    public double Fraction =>
        TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived / (double)TotalBytes, 0, 1);
}

internal static class ComfyUiOptionalComponents
{
    public const string IpAdapterFaceSdxlFileName =
        "ip-adapter-plus-face_sdxl_vit-h.safetensors";
    private const string Repository = "h94/IP-Adapter";
    private static readonly Uri RepositoryTreeUri = new(
        $"https://huggingface.co/api/models/{Repository}/tree/main/sdxl_models?expand=true");
    private static readonly Uri DownloadBaseUri = new(
        $"https://huggingface.co/{Repository}/resolve/main/sdxl_models/");

    public static ComfyUiIpAdapterStatus Inspect(ComfyUiInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var ipAdapterDirectory = Path.Combine(
            installation.ComfyUiDirectory,
            "models",
            "ipadapter");
        var clipVisionDirectory = Path.Combine(
            installation.ComfyUiDirectory,
            "models",
            "clip_vision");
        var nodeDirectory = Path.Combine(
            installation.ComfyUiDirectory,
            "custom_nodes",
            "ComfyUI_IPAdapter_plus");

        var ipAdapterPath = FindPreferredFile(
            ipAdapterDirectory,
            [
                IpAdapterFaceSdxlFileName,
                "ip-adapter-plus-face_sdxl_vit-h.bin"
            ]);
        var clipVisionPath = FindPreferredFile(
            clipVisionDirectory,
            [
                "CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors",
                "clip_vision_h.safetensors",
                "model.safetensors"
            ],
            ["*.safetensors", "*.bin"]);

        return new ComfyUiIpAdapterStatus(
            ipAdapterPath is not null &&
            clipVisionPath is not null &&
            Directory.Exists(nodeDirectory),
            ipAdapterPath,
            clipVisionPath,
            RelativeModelName(ipAdapterDirectory, ipAdapterPath),
            RelativeModelName(clipVisionDirectory, clipVisionPath),
            Directory.Exists(nodeDirectory));
    }

    public static async Task<ComfyUiModelDownload> ResolveIpAdapterFaceDownloadAsync(
        ComfyUiSettings settings,
        CancellationToken cancellationToken)
    {
        var validation = ComfyUiPathValidator.Validate(settings);
        if (!validation.IsValid || validation.Installation is null)
        {
            throw new InvalidOperationException("ComfyUI 配置无效，无法安装可选组件。");
        }

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HDRCapture/1.2");
        using var response = await client.GetAsync(RepositoryTreeUri, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        var entries = JsonNode.Parse(json) as JsonArray
            ?? throw new InvalidDataException("Hugging Face 返回的模型列表格式无效。");
        var entry = entries
            .OfType<JsonObject>()
            .FirstOrDefault(item =>
                string.Equals(
                    item["path"]?.GetValue<string>(),
                    IpAdapterFaceSdxlFileName,
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(
                $"远程仓库中找不到 {IpAdapterFaceSdxlFileName}。");

        var size = entry["size"] is JsonValue sizeValue &&
                   sizeValue.TryGetValue<long>(out var parsedSize)
            ? parsedSize
            : 0;
        var sha256 = entry["lfs"]?["oid"]?.GetValue<string>()
            ?? entry["oid"]?.GetValue<string>()
            ?? throw new InvalidDataException(
                "远程模型没有提供 SHA-256 校验值，已取消安装。");
        if (size <= 0 || sha256.Length != 64)
        {
            throw new InvalidDataException("远程模型的大小或 SHA-256 元数据无效。");
        }

        var destination = Path.Combine(
            validation.Installation.ComfyUiDirectory,
            "models",
            "ipadapter",
            IpAdapterFaceSdxlFileName);
        return new ComfyUiModelDownload(
            "IP-Adapter Face (SDXL)",
            new Uri(DownloadBaseUri, IpAdapterFaceSdxlFileName + "?download=true"),
            size,
            sha256.ToUpperInvariant(),
            destination);
    }

    public static async Task DownloadAsync(
        ComfyUiModelDownload download,
        IProgress<ComfyUiDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(download);

        if (File.Exists(download.DestinationPath) &&
            await HashMatchesAsync(
                    download.DestinationPath,
                    download.Sha256,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            progress?.Report(new ComfyUiDownloadProgress(download.Size, download.Size));
            return;
        }

        var directory = Path.GetDirectoryName(download.DestinationPath)
            ?? throw new InvalidOperationException("模型保存路径无效。");
        Directory.CreateDirectory(directory);
        var temporary = download.DestinationPath + ".part-" +
                        Guid.NewGuid().ToString("N")[..8];

        try
        {
            using var client = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HDRCapture/1.2");
            using var response = await client.GetAsync(
                    download.SourceUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength is { } contentLength &&
                        contentLength > 0
                ? contentLength
                : download.Size;
            if (total != download.Size)
            {
                throw new InvalidDataException(
                    $"远程模型大小已变化：预期 {download.Size} 字节，实际 {total} 字节。");
            }

            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var target = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long received = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                progress?.Report(new ComfyUiDownloadProgress(received, total));
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Close();
            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!actualHash.Equals(download.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "下载文件的 SHA-256 校验失败，已保留原文件不变。");
            }

            File.Move(temporary, download.DestinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static string? FindPreferredFile(
        string directory,
        IReadOnlyList<string> preferred,
        IReadOnlyList<string>? fallbackPatterns = null)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var name in preferred)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (fallbackPatterns is not null)
        {
            foreach (var pattern in fallbackPatterns)
            {
                var candidate = Directory
                    .EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string RelativeModelName(string root, string? path)
    {
        if (path is null)
        {
            return string.Empty;
        }

        return Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    private static async Task<bool> HashMatchesAsync(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            return Convert.ToHexString(hash)
                .Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
