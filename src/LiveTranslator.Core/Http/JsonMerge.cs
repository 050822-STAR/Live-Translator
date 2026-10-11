using System.Text.Json.Nodes;

namespace LiveTranslator.Core.Http;

public static class JsonMerge
{
    /// <summary>
    /// Recursively merges <paramref name="source"/> into <paramref name="target"/>.
    /// Objects merge key-by-key, everything else replaces, and an explicit <c>null</c> removes the key.
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
