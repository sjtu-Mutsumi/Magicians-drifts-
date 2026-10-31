using System.Text.Json;

namespace Magicians.Core.Cards;

/// <summary>Presentation-only copy, keyed by the immutable simulator card ID.</summary>
public sealed class CardDisplayNames
{
    private sealed record Copy(string Name, string Rules);
    private readonly Dictionary<string, Copy> _cards;

    private CardDisplayNames(Dictionary<string, Copy> cards) => _cards = cards;

    public static CardDisplayNames Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported card display schema: " + path);
        var cards = new Dictionary<string, Copy>(StringComparer.Ordinal);
        foreach (var row in root.GetProperty("cards").EnumerateObject())
            cards.Add(row.Name, new Copy(row.Value.GetProperty("name").GetString()!, row.Value.GetProperty("rules").GetString()!));
        return new(cards);
    }

    public string Name(string id, string fallback) => _cards.TryGetValue(id, out var card) ? card.Name : fallback;
    public string Rules(string id, string fallback) => _cards.TryGetValue(id, out var card) ? card.Rules : fallback;
}
