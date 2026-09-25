using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HdrCapture.ComfyUi;

internal sealed record ComfyUiImageReference(string Filename, string Subfolder, string Type);

internal sealed record ComfyUiUploadResult(string Filename, string Subfolder, string Type);

internal static class ComfyUiErrorText
{
    public static string Translate(string raw)
    {
        if (raw.Contains("clip input is invalid", StringComparison.OrdinalIgnoreCase))
        {
            return
                "所选模型不包含 FaceDetailer 可用的 CLIP。请选择 SD 或 SDXL 类检查点，" +
                "不要选择只有 UNet/扩散模型的专用权重。";
        }

        if (raw.Contains("IPAdapter model not found", StringComparison.OrdinalIgnoreCase))
        {
            return
                "所选检查点与已安装的 IP-Adapter Face 不兼容，或 IP-Adapter Face 文件缺失。";
        }

        return raw;
    }
}

/// <summary>Small HTTP client for the documented ComfyUI prompt/history/view APIs.</summary>
internal sealed class ComfyUiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private bool _disposed;

    public ComfyUiClient(string baseUrl)
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HDRCapture/1.2");
    }

    public string BaseUrl => _http.BaseAddress!.ToString().TrimEnd('/');

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync("system_stats", timeout.Token)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<JsonObject> GetObjectInfoAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("object_info", cancellationToken)
            .ConfigureAwait(false);
        var json = await ReadSuccessfulStringAsync(response, cancellationToken)
            .ConfigureAwait(false);
        return JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("ComfyUI object_info 返回值不是 JSON 对象。");
    }

    public async Task<string> QueuePromptAsync(
        JsonObject workflow,
        string clientId,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["prompt"] = workflow,
            ["client_id"] = clientId
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("prompt", content, cancellationToken)
            .ConfigureAwait(false);
        var json = await ReadSuccessfulStringAsync(response, cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("prompt_id", out var promptId)
            ? promptId.GetString() ?? throw new InvalidDataException("ComfyUI 未返回 prompt_id。")
            : throw new InvalidDataException("ComfyUI 未返回 prompt_id。");
    }

    public async Task<JsonObject> WaitForCompletionAsync(
        string promptId,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var poll = 700;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await _http.GetAsync($"history/{Uri.EscapeDataString(promptId)}", cancellationToken)
                .ConfigureAwait(false);
            var json = await ReadSuccessfulStringAsync(response, cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(promptId, out var history))
            {
                var completed = !history.TryGetProperty("status", out var statusNode) ||
                                !statusNode.TryGetProperty("completed", out var completedNode) ||
                                completedNode.GetBoolean();
                var statusText = statusNode.ValueKind == JsonValueKind.Object &&
                                 statusNode.TryGetProperty("status_str", out var statusString)
                    ? statusString.GetString()
                    : null;
                if (string.Equals(statusText, "error", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"ComfyUI 执行失败：{ExtractExecutionError(history)}");
                }

                if (completed)
                {
                    return JsonNode.Parse(history.GetRawText()) as JsonObject
                        ?? throw new InvalidDataException("ComfyUI history 条目不是 JSON 对象。");
                }
            }

            status?.Invoke($"ComfyUI 正在生成… {DateTime.UtcNow - started:hh\\:mm\\:ss}");
            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            poll = Math.Min(2500, poll + 200);
        }
    }

    public async Task<ComfyUiUploadResult> UploadImageAsync(
        byte[] png,
        string filename,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "image", filename);
        content.Add(new StringContent("true"), "overwrite");
        content.Add(new StringContent("input"), "type");

        using var response = await _http.PostAsync("upload/image", content, cancellationToken)
            .ConfigureAwait(false);
        var json = await ReadSuccessfulStringAsync(response, cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new ComfyUiUploadResult(
            root.GetProperty("name").GetString() ?? filename,
            root.TryGetProperty("subfolder", out var subfolder)
                ? subfolder.GetString() ?? string.Empty
                : string.Empty,
            root.TryGetProperty("type", out var type)
                ? type.GetString() ?? "input"
                : "input");
    }

    public async Task<byte[]> DownloadImageAsync(
        ComfyUiImageReference image,
        CancellationToken cancellationToken)
    {
        var query =
            $"view?filename={Uri.EscapeDataString(image.Filename)}" +
            $"&subfolder={Uri.EscapeDataString(image.Subfolder)}" +
            $"&type={Uri.EscapeDataString(image.Type)}";
        using var response = await _http.GetAsync(query, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            throw new HttpRequestException(
                $"ComfyUI 下载图片失败 ({(int)response.StatusCode})：{body}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task InterruptAsync(string? promptId, CancellationToken cancellationToken)
    {
        try
        {
            var body = promptId is null
                ? new JsonObject()
                : new JsonObject { ["prompt_id"] = promptId };
            using var content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json");
            using var response = await _http.PostAsync("interrupt", content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Cancellation must never fail because the local service has already exited.
        }
    }

    public async Task DeleteHistoryAsync(
        IReadOnlyCollection<string> promptIds,
        CancellationToken cancellationToken)
    {
        if (promptIds.Count == 0)
        {
            return;
        }

        try
        {
            var body = new JsonObject
            {
                ["delete"] = new JsonArray(promptIds.Select(id => JsonValue.Create(id)).ToArray())
            };
            using var content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json");
            using var response = await _http.PostAsync("history", content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // History cleanup is best-effort.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }

    private static async Task<string> ReadSuccessfulStringAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"ComfyUI API 返回 {(int)response.StatusCode}：{body}");
        }

        return body;
    }

    private static string ExtractExecutionError(JsonElement history)
    {
        if (history.TryGetProperty("status", out var status) &&
            status.TryGetProperty("messages", out var messages) &&
            messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray().Reverse())
            {
                if (message.ValueKind == JsonValueKind.Array &&
                    message.GetArrayLength() >= 2 &&
                    message[1].ValueKind == JsonValueKind.Object &&
                    message[1].TryGetProperty("exception_message", out var exception))
                {
                    return ComfyUiErrorText.Translate(
                        exception.GetString() ?? exception.ToString());
                }
            }
        }

        return "未知执行错误";
    }
}
