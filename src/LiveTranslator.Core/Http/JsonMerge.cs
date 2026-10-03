using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveTranslator.Core.Http;

public static class JsonMerge
{
    /// <summary>Parses a user-supplied JSON object; blank input yields an empty object.</summary>
    /// <exception cref="FormatException">The text is not a JSON object.</exception>
    public static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"JSON 格式错误: {ex.Message}", ex);
        }
        return node as JsonObject ?? throw new FormatException("必须是 JSON 对象，例如 {\"key\": \"value\"}");
    }

    /// <summary>
    /// Recursively merges <paramref name="source"/> into <paramref name="target"/>.
    /// Objects merge key-by-key, everything else replaces, and an explicit <c>null</c> removes the key
    /// (lets users strip a default parameter that a particular vendor rejects).
    /// </summary>
    public static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is null)
            {
                target.Remove(key);
            }
            else if (value is JsonObject sourceObj && target[key] is JsonObject targetObj)
            {
                DeepMerge(targetObj, sourceObj);
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
    }
}
