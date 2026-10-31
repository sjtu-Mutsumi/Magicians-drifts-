using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Magicians.Native;

public sealed record Card(string name, int cost, int attack, int defense, int layers,
    string element, string rarity, string text, string kind, bool ability)
{
    public bool enchantment => element == "fire" || element != "generic" && !ability;
    public Dictionary<string, object?> AsDictionary() => new(StringComparer.Ordinal)
    {
        ["name"] = name, ["cost"] = cost, ["attack"] = attack, ["defense"] = defense,
        ["layers"] = layers, ["element"] = element, ["rarity"] = rarity, ["text"] = text,
        ["kind"] = kind, ["ability"] = ability
    };
}

/// <summary>Frozen v1.2 data and stable IDs; editorial names never become dispatch keys.</summary>
public static class Catalog
{
    public static string ROOT { get; private set; } = "";
    public static readonly string[] ELEMENTS = { "fire", "water", "earth", "wind", "poison" };
    public static readonly string[] START = { "攻击强化", "攻击强化", "防御强化", "防御强化", "护盾", "检索附魔", "短促", "重洗" };
    public static readonly HashSet<string> START_ONLY = new(START, StringComparer.Ordinal);
    public static readonly HashSet<string> STACKING = new(new[] { "燎原", "深渊潮", "山崩", "飓风", "瘟潮" }, StringComparer.Ordinal);
    public static readonly HashSet<string> SHIELD_CARDS = new(new[] {
        "护盾", "永恒壁垒", "盾之共鸣", "坚壁", "护盾强化", "余烬护心", "潮涌", "反震岩核", "万钧碎盾", "不坏壁垒",
        "回震岩甲", "山岳壁垒", "岩潮壁垒", "临时壁垒", "回岩", "守岩", "山岳", "岩拳", "岩核共鸣", "握盾", "盾脉",
        "碎岩印记", "碎盾术", "磐岩壁", "裂岩护壁", "坚岩", "岩甲", "磐石", "开岩", "毒囊" }, StringComparer.Ordinal);
    public static readonly Dictionary<string, int> RANK = new(StringComparer.Ordinal)
        { ["common"] = 0, ["rare"] = 1, ["epic"] = 2, ["legendary"] = 3, ["specialization"] = 4 };
    public static Dictionary<string, Card> CARDS { get; private set; } = new(StringComparer.Ordinal);
    public static Dictionary<string, string> SPECIAL { get; private set; } = new(StringComparer.Ordinal);
    public static Dictionary<string, string[]> ROUTES { get; private set; } = new(StringComparer.Ordinal);
    public static string CardsSha256 { get; private set; } = "";

    public static void Initialize(string root)
    {
        ROOT = Path.GetFullPath(root);
        var path = Path.Combine(ROOT, "releases", "v1.2", "cards.json");
        var raw = File.ReadAllBytes(path);
        CardsSha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ROOT, "releases", "v1.2", "freeze-manifest.json")));
        if (manifest.RootElement.GetProperty("sha256").GetProperty("releases/v1.2/cards.json").GetString() != CardsSha256)
            throw new InvalidDataException("Frozen v1.2 card snapshot checksum mismatch");
        using var json = JsonDocument.Parse(raw);
        var result = new Dictionary<string, Card>(StringComparer.Ordinal);
        foreach (var row in json.RootElement.GetProperty("cards").EnumerateObject())
        {
            var value = row.Value;
            var card = new Card(value.GetProperty("name").GetString()!, value.GetProperty("cost").GetInt32(),
                value.GetProperty("attack").GetInt32(), value.GetProperty("defense").GetInt32(), value.GetProperty("layers").GetInt32(),
                value.GetProperty("element").GetString()!, value.GetProperty("rarity").GetString()!, value.GetProperty("text").GetString()!,
                value.GetProperty("kind").GetString()!, value.GetProperty("ability").GetBoolean());
            if (row.Name != card.name) throw new InvalidDataException("Frozen card identity mismatch");
            result.Add(row.Name, card);
        }
        if (result.Count != 183 || START_ONLY.Any(name => !result.ContainsKey(name))) throw new InvalidDataException("Invalid v1.2 card inventory");
        CARDS = result;
        SPECIAL = result.Values.Where(card => card.kind == "specialization").ToDictionary(card => card.element, card => card.name, StringComparer.Ordinal);
        ROUTES = ELEMENTS.ToDictionary(element => element, element => result.Values.Where(card => card.element == element && card.kind == "route_legendary").Select(card => card.name).ToArray(), StringComparer.Ordinal);
        if (result.Values.Sum(copies) != 677 || SPECIAL.Count != 5 || ROUTES.Values.Sum(routes => routes.Length) != 15)
            throw new InvalidDataException("Invalid v1.2 stock or route inventory");
    }

    public static int copies(Card card)
    {
        if (START_ONLY.Contains(card.name) || card.kind == "specialization") return 0;
        if (card.rarity == "legendary") return card.element == "generic" ? 2 : 1;
        return card.rarity switch { "common" => card.element == "generic" ? 8 : 6, "rare" => card.element == "generic" ? 6 : 4, "epic" => card.element == "generic" ? 4 : 3, _ => throw new InvalidDataException("Unknown card rarity") };
    }

    public static Dictionary<string, Card> load_sources()
    {
        var rarities = new Dictionary<string, string>(StringComparer.Ordinal) { ["专精"] = "specialization", ["传奇"] = "legendary", ["史诗"] = "epic", ["稀有"] = "rare", ["普通"] = "common" };
        var result = new Dictionary<string, Card>(StringComparer.Ordinal);
        foreach (var element in ELEMENTS.Append("generic"))
        {
            string? rarity = null;
            foreach (var line in File.ReadLines(Path.Combine(ROOT, "docs", "cards-v1.2", element + ".md")))
            {
                var match = Regex.Match(line, "^## (专精|传奇|史诗|稀有|普通)");
                if (match.Success) rarity = rarities[match.Groups[1].Value];
                if (!line.StartsWith('|')) continue;
                var fields = line.Split('|')[1..^1].Select(cell => cell.Trim()).ToArray();
                var costIndex = fields.Length == 6 ? 1 : 2;
                if (fields.Length is not (6 or 7 or 8) || !int.TryParse(fields[costIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var cost)) continue;
                var name = fields[costIndex - 1];
                if (rarity is null) throw new InvalidDataException("Missing card rarity");
                var kind = rarity == "specialization" ? "specialization" : rarity == "legendary" && element != "generic" && !STACKING.Contains(name) ? "route_legendary" : "normal";
                var ability = rarity == "specialization" || rarity == "legendary" && !STACKING.Contains(name);
                result.Add(name, new(name, cost, int.Parse(fields[costIndex + 1], CultureInfo.InvariantCulture), int.Parse(fields[costIndex + 2], CultureInfo.InvariantCulture), int.Parse(fields[costIndex + 3], CultureInfo.InvariantCulture), element, rarity, fields[costIndex + 4], kind, ability));
            }
        }
        return result;
    }
    public static void validate_sources()
    {
        var source = load_sources();
        if (!source.SequenceEqual(CARDS)) throw new InvalidDataException("Frozen v1.2 card source or iteration order changed");
    }
}

public static class SixStyles
{
    public static readonly string[] IDS = { "fire", "water", "earth", "wind", "poison", "flexible" };
    public static string DefaultBundle => Path.Combine(Catalog.ROOT, "releases", "ai-v1.2-six-styles-20261029");
    public static JsonElement available_styles(string? bundle = null)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(bundle ?? DefaultBundle, "manifest.json")));
        var manifest = json.RootElement;
        if (manifest.GetProperty("game_version").GetString() != "1.2" || !manifest.GetProperty("models").EnumerateObject().Select(row => row.Name).ToHashSet(StringComparer.Ordinal).SetEquals(IDS)) throw new InvalidDataException("Invalid six-style registry");
        if (manifest.GetProperty("cards_sha256").GetString() != Catalog.CardsSha256) throw new InvalidDataException("Card snapshot mismatch");
        return manifest.Clone();
    }
    public static JsonElement load_policy(string style, string? bundle = null)
    {
        if (!IDS.Contains(style, StringComparer.Ordinal)) throw new ArgumentException("Unknown strategic style: " + style);
        var root = Path.GetFullPath(bundle ?? DefaultBundle);
        var manifest = available_styles(root);
        var item = manifest.GetProperty("models").GetProperty(style);
        var path = Path.GetFullPath(Path.Combine(root, item.GetProperty("file").GetString()!));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Policy path leaves bundle");
        var raw = File.ReadAllBytes(path);
        if (Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() != item.GetProperty("sha256").GetString()) throw new InvalidDataException("Policy hash mismatch");
        using var json = JsonDocument.Parse(raw); var cfg = json.RootElement;
        if (cfg.GetProperty("kind").GetString() != "explorer" || !cfg.TryGetProperty("weights", out _) || !cfg.TryGetProperty("construction", out _) || !cfg.TryGetProperty("element_weights", out _)) throw new InvalidDataException("Unsupported policy schema");
        var intent = cfg.TryGetProperty("element_intent", out var elementIntent) ? elementIntent.GetString() : null;
        var routeIntent = cfg.TryGetProperty("route_intent", out var routeValue) ? routeValue.GetString() : null;
        if (style == "flexible" ? !string.IsNullOrEmpty(intent) || !string.IsNullOrEmpty(routeIntent) : intent != style) throw new InvalidDataException("Policy intent mismatch");
        return cfg.Clone();
    }
}

public static class PanelBudget
{
    private static JsonElement Rules { get { using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Catalog.ROOT, "tools", "sim_v12", "rules", "panel-budgets.json"))); return document.RootElement.Clone(); } }
    public static string role(string name) => Rules.GetProperty("card_roles").GetProperty(name).GetString()!;
    public static (string key, JsonElement data)? mechanism_band(string name)
    {
        var rules = Rules;
        if (!rules.TryGetProperty("mechanism_bands", out var bands)) return null;
        var matches = bands.EnumerateObject().Where(row => row.Value.GetProperty("cards").EnumerateArray().Any(value => value.GetString() == name)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Overlapping mechanism budgets: " + name);
        return matches.Length == 0 ? null : (matches[0].Name, matches[0].Value.Clone());
    }
    public static string budget_group(string name) => mechanism_band(name)?.key ?? role(name);
    public static string reference_rarity(string name, string rarity) => mechanism_band(name)?.data.GetProperty("reference_rarity").GetString() ?? rarity;
    public static Q multiplier(string name)
    {
        var band = mechanism_band(name);
        return Q.Parse((band is null ? Rules.GetProperty("multipliers").GetProperty(role(name)) : band.Value.data.GetProperty("multiplier")).GetRawText());
    }
    public static Q value(int cost, int attack, int defense) => new(2L * attack + 3L * defense, cost + 1);
    public static (Q low, Q high) bounds(string name, string rarity)
    {
        var values = Rules.GetProperty("base_bands").GetProperty(reference_rarity(name, rarity)).EnumerateArray().Select(value => Q.Parse(value.GetRawText()) * multiplier(name)).ToArray();
        return (values[0], values[1]);
    }
}
