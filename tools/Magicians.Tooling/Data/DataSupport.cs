using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magicians.Native;

namespace Magicians.Tooling;

internal static class DataJson
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
    internal static string Text(JsonNode value) => value.ToJsonString(Options).Replace("\r\n", "\n") + "\n";
    internal static void Write(string path, JsonNode value) => WriteText(path, Text(value));
    internal static string Normalize(string value) => value.Replace("\r\n", "\n");
    internal static void WriteText(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var newline = File.Exists(path) && File.ReadAllText(path).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        File.WriteAllText(path, Normalize(value).Replace("\n", newline), new UTF8Encoding(false));
    }
    internal static void WriteOrCheck(string path, string content, bool check)
    {
        if (!check) { WriteText(path, content); return; }
        if (!File.Exists(path) || Normalize(File.ReadAllText(path)) != Normalize(content))
            throw new InvalidDataException("Generated data is stale: " + path);
    }
    internal static string Sha(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    internal static string Sha(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    internal static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    internal static string? Arg(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Missing value for " + key);
        return args[index + 1];
    }
    internal static string Destination(string root, string[] args, string fallback) => Path.GetFullPath(Arg(args, "--out") ?? Path.Combine(root, fallback));
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    internal static string RootPath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes repository: " + relative);
        return full;
    }
}

/// <summary>Exact v1.2 panel budgets; all values remain arbitrary-precision fractions.</summary>
public static class PanelBudgets
{
    private static string _root = "";
    private static JsonObject? _rules;
    public static JsonObject Rules
    {
        get
        {
            if (_rules is null || _root != Catalog.ROOT)
            {
                _root = Catalog.ROOT;
                _rules = DataJson.Read(Path.Combine(_root, "tools/sim_v12/rules/panel-budgets.json"));
            }
            return _rules;
        }
    }
    public static string Role(string name) => Rules["card_roles"]![name]!.GetValue<string>();
    public static (string Name, JsonObject Data)? MechanismBand(string name)
    {
        var bands = Rules["mechanism_bands"]?.AsObject();
        var matches = bands?.Where(row => row.Value!["cards"]!.AsArray().Any(card => card!.GetValue<string>() == name)).ToArray() ?? [];
        if (matches.Length > 1) throw new InvalidDataException("Overlapping mechanism budgets: " + name);
        return matches.Length == 0 ? null : (matches[0].Key, matches[0].Value!.AsObject());
    }
    public static string BudgetGroup(string name) => MechanismBand(name)?.Name ?? Role(name);
    public static string ReferenceRarity(string name, string rarity) => MechanismBand(name)?.Data["reference_rarity"]!.GetValue<string>() ?? rarity;
    public static Q Multiplier(string name) => Q.Parse((MechanismBand(name)?.Data["multiplier"] ?? Rules["multipliers"]![Role(name)])!.ToJsonString());
    public static Q Value(int cost, int attack, int defense) => new(2 * attack + 3 * defense, cost + 1);
    public static (Q Low, Q High) Bounds(string name, string rarity)
    {
        var band = Rules["base_bands"]![ReferenceRarity(name, rarity)]!.AsArray();
        var factor = Multiplier(name);
        return (Q.Parse(band[0]!.ToJsonString()) * factor, Q.Parse(band[1]!.ToJsonString()) * factor);
    }
}

public static class DataCatalog
{
    public static JsonObject Snapshot(string root)
    {
        if (Catalog.ROOT != Path.GetFullPath(root)) Catalog.Initialize(root);
        var cards = new JsonObject();
        foreach (var (name, card) in Catalog.CARDS)
        {
            var row = JsonSerializer.SerializeToNode(card.AsDictionary(), DataJson.Options)!.AsObject();
            row["starter_only"] = Catalog.START_ONLY.Contains(name);
            row["pool_copies"] = Catalog.copies(card);
            row["panel_role"] = PanelBudgets.Role(name);
            row["panel_value"] = (double)PanelBudgets.Value(card.cost, card.attack, card.defense);
            row["panel_budget_group"] = PanelBudgets.BudgetGroup(name);
            row["panel_reference_rarity"] = PanelBudgets.ReferenceRarity(name, card.rarity);
            row["numeric_equivalent_value"] = (double)(PanelBudgets.Value(card.cost, card.attack, card.defense) / PanelBudgets.Multiplier(name));
            cards[name] = row;
        }
        var hashes = new JsonObject();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "docs/cards-v1.2"), "*.md").Where(p => Path.GetFileName(p) != "change-history.md").Order(StringComparer.Ordinal))
            hashes[DataJson.Relative(root, file)] = DataJson.Sha(file);
        return new JsonObject { ["version"] = "1.2", ["scope"] = "independent C# simulator", ["initial_deck"] = new JsonArray(Catalog.START.Select(n => JsonValue.Create(n)).ToArray()), ["cards"] = cards, ["source_sha256"] = hashes };
    }
    public static void Export(string root, string folder)
    {
        Directory.CreateDirectory(folder);
        DataJson.Write(Path.Combine(folder, "cards.json"), Snapshot(root));
        var counts = new JsonObject(); foreach (var (name, card) in Catalog.CARDS) counts[name] = Catalog.copies(card);
        DataJson.Write(Path.Combine(folder, "pool-counts.json"), new JsonObject { ["version"] = "1.2", ["total"] = Catalog.CARDS.Values.Sum(Catalog.copies), ["cards"] = counts });
    }
}
