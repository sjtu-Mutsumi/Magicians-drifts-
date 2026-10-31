using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Magicians.Native;

public static class NativeJson
{
    public static Dictionary<string, object?> Obj(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    public static JsonNode? Node(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement element) return JsonNode.Parse(element.GetRawText());
        if (value is JsonNode node) return node.DeepClone();
        if (value is string text) return JsonValue.Create(text);
        if (value is bool boolean) return JsonValue.Create(boolean);
        if (value is Q rational)
        {
            var exact = rational.ToString();
            return BigInteger.TryParse(exact, out _) ? JsonNode.Parse(exact) : JsonValue.Create(exact);
        }
        if (value is BigInteger integer) return JsonNode.Parse(integer.ToString(CultureInfo.InvariantCulture));
        if (value is byte or short or int or long or uint or ulong or float or double or decimal)
            return JsonSerializer.SerializeToNode(value);
        if (value is IDictionary dictionary)
        {
            var result = new JsonObject();
            foreach (DictionaryEntry pair in dictionary)
                result[Convert.ToString(pair.Key, CultureInfo.InvariantCulture)!] = Node(pair.Value);
            return result;
        }
        if (value is ITuple tuple)
        {
            var result = new JsonArray();
            for (var i = 0; i < tuple.Length; i++) result.Add(Node(tuple[i]));
            return result;
        }
        if (value is IEnumerable sequence)
        {
            var result = new JsonArray();
            foreach (var item in sequence) result.Add(Node(item));
            return result;
        }
        throw new InvalidDataException("Unexpected native protocol value: " + value.GetType().FullName);
    }

    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(Node(value));
    public static string? String(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind != JsonValueKind.Null ? item.GetString() : null;
    public static int? Int(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) ? number : null;
    public static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
}
