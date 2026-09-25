using System.Text.Json.Nodes;

namespace HdrCapture.ComfyUi;

internal sealed record PortraitWorkflowParameters(
    string Checkpoint,
    string InputImage,
    long Seed,
    double Denoise,
    int Feather,
    int NoiseMaskFeather,
    double BlendFactor,
    string OutputPrefix,
    string MaskPrefix,
    bool UseIpAdapter = false,
    string IpAdapterFile = "",
    string ClipVisionFile = "");

internal static class ComfyUiWorkflowBuilder
{
    public static JsonObject BuildPortraitWorkflow(PortraitWorkflowParameters parameters)
    {
        var root = WorkflowTemplateStore.LoadPortraitTemplate();

        ReplacePlaceholders(root, new Dictionary<string, JsonNode?>
        {
            ["__CHECKPOINT__"] = JsonValue.Create(parameters.Checkpoint),
            ["__INPUT_IMAGE__"] = JsonValue.Create(parameters.InputImage),
            ["__SEED__"] = JsonValue.Create(parameters.Seed),
            ["__DENOISE__"] = JsonValue.Create(parameters.Denoise),
            ["__FEATHER__"] = JsonValue.Create(parameters.Feather),
            ["__NOISE_MASK_FEATHER__"] = JsonValue.Create(parameters.NoiseMaskFeather),
            ["__BLEND_FACTOR__"] = JsonValue.Create(parameters.BlendFactor),
            ["__OUTPUT_PREFIX__"] = JsonValue.Create(parameters.OutputPrefix),
            ["__MASK_PREFIX__"] = JsonValue.Create(parameters.MaskPrefix)
        });

        if (parameters.UseIpAdapter)
        {
            AddIpAdapterFace(root, parameters);
        }

        return root;
    }

    public static string GetPortraitWorkflowJson(PortraitWorkflowParameters parameters) =>
        BuildPortraitWorkflow(parameters).ToJsonString();

    private static void ReplacePlaceholders(JsonNode node, IReadOnlyDictionary<string, JsonNode?> values)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    values.TryGetValue(text, out var replacement))
                {
                    obj[property.Key] = replacement?.DeepClone();
                }
                else if (property.Value is not null)
                {
                    ReplacePlaceholders(property.Value, values);
                }
            }

            return;
        }

        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var item = array[index];
                if (item is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    values.TryGetValue(text, out var replacement))
                {
                    array[index] = replacement?.DeepClone();
                }
                else if (item is not null)
                {
                    ReplacePlaceholders(item, values);
                }
            }
        }
    }

    private static void AddIpAdapterFace(
        JsonObject root,
        PortraitWorkflowParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.IpAdapterFile) ||
            string.IsNullOrWhiteSpace(parameters.ClipVisionFile))
        {
            throw new InvalidOperationException(
                "启用 IP-Adapter Face 时缺少模型文件名。");
        }

        root["40"] = new JsonObject
        {
            ["class_type"] = "IPAdapterModelLoader",
            ["inputs"] = new JsonObject
            {
                ["ipadapter_file"] = parameters.IpAdapterFile
            }
        };
        root["41"] = new JsonObject
        {
            ["class_type"] = "CLIPVisionLoader",
            ["inputs"] = new JsonObject
            {
                ["clip_name"] = parameters.ClipVisionFile
            }
        };
        root["42"] = new JsonObject
        {
            ["class_type"] = "IPAdapterAdvanced",
            ["inputs"] = new JsonObject
            {
                ["model"] = Link("1", 0),
                ["ipadapter"] = Link("40", 0),
                ["image"] = Link("2", 0),
                ["clip_vision"] = Link("41", 0),
                ["weight"] = 0.65,
                ["weight_type"] = "linear",
                ["combine_embeds"] = "concat",
                ["start_at"] = 0.0,
                ["end_at"] = 0.75,
                ["embeds_scaling"] = "V only"
            }
        };

        var faceDetailerInputs = root["20"]?["inputs"] as JsonObject
            ?? throw new InvalidDataException("内置工作流缺少 FaceDetailer 输入。");
        faceDetailerInputs["model"] = Link("42", 0);
    }

    private static JsonArray Link(string nodeId, int outputIndex) =>
        new(JsonValue.Create(nodeId), JsonValue.Create(outputIndex));
}
