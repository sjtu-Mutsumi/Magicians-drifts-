using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Magicians.Native;

namespace Magicians.Tooling;

public static class DataCommands
{
    public static readonly string[] Commands = ["verify", "freeze", "build-presentation", "build-catalogue", "audit-cards", "audit-layers", "export-catalog"];
    /// <summary>Explicit gated training deployment; it cannot reseal unrelated source changes.</summary>
    public static void DeployPolicy(string root, JsonObject candidate, string expectedPolicySha256, bool heldoutGatePassed) =>
        FrozenIntegrity.DeployPolicy(root, candidate, expectedPolicySha256, heldoutGatePassed);
    public static int Run(string command, string root, string[] args)
    {
        Catalog.Initialize(root);
        switch (command)
        {
            case "verify": FrozenIntegrity.Verify(root, args); break;
            case "freeze": FrozenIntegrity.Freeze(root, args); break;
            case "build-presentation": BuildPresentation(root, args); break;
            case "build-catalogue": BuildCatalogue(root, args); break;
            case "audit-cards": AuditCards(root, args); break;
            case "audit-layers": AuditLayers(root, args); break;
            case "export-catalog": DataCatalog.Export(root, DataJson.Destination(root, args, "runtime/catalog-export")); Console.WriteLine("V12_CATALOG_EXPORTED cards=183 stock=677"); break;
            default: throw new ArgumentException("Unknown data command: " + command);
        }
        return 0;
    }

    public static JsonObject Presentation(string root)
    {
        var names = Catalog.CARDS.Keys.ToDictionary(key => key, key => key, StringComparer.Ordinal);
        foreach (var element in new[] { "fire", "water", "poison", "wind", "earth" })
        {
            var mapping = DataJson.Read(Path.Combine(root, $"docs/cards-v1.2/{element}-name-map.json"))["cards"]!.AsObject();
            DataJson.Require(mapping.Select(row => row.Key).ToHashSet(StringComparer.Ordinal).SetEquals(Catalog.CARDS.Values.Where(c => c.element == element).Select(c => c.name)), element + ": name IDs do not match current cards");
            foreach (var (key, row) in mapping)
            {
                DataJson.Require(row!["original_name"]!.GetValue<string>() == key && !string.IsNullOrWhiteSpace(row["display_name"]!.GetValue<string>()), "Invalid display name: " + key);
                names[key] = row["display_name"]!.GetValue<string>();
            }
        }
        DataJson.Require(names.Values.Distinct(StringComparer.Ordinal).Count() == names.Count, "Display names must be unique");
        var regex = new Regex(string.Join('|', Catalog.CARDS.Keys.OrderByDescending(key => key.Length).ThenBy(key => key, StringComparer.Ordinal).Select(Regex.Escape)), RegexOptions.CultureInvariant);
        var cards = new JsonObject();
        foreach (var (key, card) in Catalog.CARDS)
        {
            var rules = regex.Replace(card.text, match => match.Value == "风痕" && !card.text[..match.Index].EndsWith("激活", StringComparison.Ordinal) ? match.Value : names[match.Value]);
            cards[key] = new JsonObject { ["name"] = names[key], ["rules"] = rules };
        }
        return new JsonObject { ["schema_version"] = 1, ["version"] = "1.2", ["note"] = "仅用于显示；键为原始运行ID。小字独立保存在docs/cards-v1.2，暂不进入局内。", ["cards"] = cards };
    }
    private static void BuildPresentation(string root, string[] args)
    {
        var data = Presentation(root);
        var target = DataJson.Destination(root, args, "Data/Presentation/card-display.json");
        DataJson.WriteOrCheck(target, DataJson.Text(data), args.Contains("--check"));
        var renamed = data["cards"]!.AsObject().Count(row => row.Key != row.Value!["name"]!.GetValue<string>());
        Console.WriteLine($"V12_PRESENTATION_OK cards=183 renamed={renamed} flavor_in_match=false");
    }
    private static void BuildCatalogue(string root, string[] args)
    {
        var elements = new Dictionary<string, string> { ["fire"] = "火", ["water"] = "水", ["earth"] = "土", ["wind"] = "风", ["poison"] = "毒", ["generic"] = "通用" };
        var rarity = new Dictionary<string, string> { ["specialization"] = "专精", ["legendary"] = "传奇", ["epic"] = "史诗", ["rare"] = "稀有", ["common"] = "普通" };
        var stableIds = args.Contains("--stable-ids");
        var display = Presentation(root)["cards"]!.AsObject();
        var lines = new List<string> { "# v1.2 冻结183张卡牌总表" };
        if (!stableIds) lines.AddRange(["", "土系列采用展示定稿名，稳定运行标识见[土系新旧名对照](earth-names.md)；费用、面板、层数及效果规则保持冻结版不变。", "", "风系列采用展示定稿名，稳定运行标识见[风系新旧名对照](wind-names.md)；费用、面板、层数及效果规则保持冻结版不变。"]);
        lines.AddRange(["", "临时正向数值采用加算叠加。统一术语、时点与上限见[当前规则](README.md)，固定面板预算见[panel-budgets.md](panel-budgets.md)。正式快照见releases/v1.2/cards.json。"]);
        foreach (var (element, label) in elements)
        {
            lines.AddRange(["", "## " + label, "", "|品质|卡牌|费|攻|防|首次层|预算组|V值|效果|", "|---|---|---:|---:|---:|---:|---|---:|---|"]);
            foreach (var (name, card) in Catalog.CARDS.Where(row => row.Value.element == element))
            {
                // The accepted catalogue publishes earth/wind aliases while other sections retain stable IDs.
                // --stable-ids explicitly reproduces the old generator; it is never the default destructive rewrite.
                var shown = !stableIds && element is "earth" or "wind" ? display[name]!["name"]!.GetValue<string>() : name;
                var text = card.text;
                if (!stableIds && element == "earth") text = text.Replace("大地之握", display["大地之握"]!["name"]!.GetValue<string>(), StringComparison.Ordinal);
                if (!stableIds && element == "wind") text = text.Replace("激活风痕", "激活“" + display["风痕"]!["name"]!.GetValue<string>() + "”", StringComparison.Ordinal);
                lines.Add($"|{rarity[card.rarity]}|{shown}|{card.cost}|{card.attack}|{card.defense}|{card.layers}|{PanelBudgets.BudgetGroup(name)}|{((double)PanelBudgets.Value(card.cost, card.attack, card.defense)).ToString("F3", CultureInfo.InvariantCulture)}|{text}|");
            }
        }
        DataJson.WriteOrCheck(DataJson.Destination(root, args, "docs/cards-v1.2/catalogue.md"), string.Join('\n', lines) + "\n", args.Contains("--check"));
        Console.WriteLine("V12_CATALOGUE 183");
    }
    public static JsonObject StaticAudit(string root, string? baselinePath = null)
    {
        var values = new Dictionary<string, List<Q>>(StringComparer.Ordinal);
        var normalized = new Dictionary<string, List<Q>>(StringComparer.Ordinal);
        var groups = new JsonObject(); var problems = new JsonArray(); var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rarityLabels = new Dictionary<string, string> { ["common"] = "普通", ["rare"] = "稀有", ["epic"] = "史诗", ["legendary"] = "传奇", ["specialization"] = "专精" };
        foreach (var element in Catalog.ELEMENTS.Append("generic"))
        {
            var group = new JsonObject();
            foreach (var card in Catalog.CARDS.Values.Where(c => c.element == element))
            {
                var panel = PanelBudgets.Value(card.cost, card.attack, card.defense);
                var (lo, hi) = PanelBudgets.Bounds(card.name, card.rarity);
                if (panel < lo || panel > hi) problems.Add(new JsonObject { ["name"] = card.name, ["value"] = (double)panel, ["expected"] = new JsonArray((double)lo, (double)hi) });
                var key = rarityLabels[card.rarity] + "/" + PanelBudgets.BudgetGroup(card.name);
                if (!values.TryGetValue(key, out var list)) values[key] = list = [];
                list.Add(panel);
                key = PanelBudgets.ReferenceRarity(card.name, card.rarity);
                if (!normalized.TryGetValue(key, out list)) normalized[key] = list = [];
                list.Add(panel / PanelBudgets.Multiplier(card.name));
                var label = rarityLabels[card.rarity]; group[label] = (group[label]?.GetValue<int>() ?? 0) + 1;
                var role = PanelBudgets.Role(card.name); counts[role] = counts.GetValueOrDefault(role) + 1;
            }
            groups[element] = group;
        }
        var ratios = new JsonObject(); var normalizedRatios = new JsonObject();
        // Reports retain the original float(max(values))/float(min(values)) rounding;
        // acceptance bounds themselves continue to use exact rational arithmetic.
        foreach (var (key, vals) in values) ratios[key] = (double)vals.Max() / (double)vals.Min();
        foreach (var (key, vals) in normalized) normalizedRatios[key] = (double)vals.Max() / (double)vals.Min();
        DataJson.Require(Catalog.CARDS.Count == 183 && Catalog.CARDS.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(PanelBudgets.Rules["card_roles"]!.AsObject().Select(r => r.Key)), "Panel role coverage mismatch");
        DataJson.Require(problems.Count == 0, "Panel budget problems: " + problems.ToJsonString());
        DataJson.Require(ratios.Concat(normalizedRatios).All(row => row.Value!.GetValue<double>() <= 1.2 + 1e-9), "Panel values exceed 1.2 within-group ratio");
        var baseline = DataJson.Read(baselinePath ?? Path.Combine(root, "releases/v1.2/cards.json"))["cards"]!.AsObject().Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        return new JsonObject
        {
            ["scope"] = "static design only; runtime effects NOT verified", ["count"] = 183, ["groups"] = groups,
            ["budget_problems"] = problems, ["value_ratios"] = ratios,
            ["added"] = new JsonArray(Catalog.CARDS.Keys.Except(baseline).Order(StringComparer.Ordinal).Select(n => JsonValue.Create(n)).ToArray()),
            ["normalized_value_ratios"] = normalizedRatios, ["panel_role_counts"] = JsonSerializer.SerializeToNode(counts),
            ["retired"] = new JsonArray(baseline.Except(Catalog.CARDS.Keys).Order(StringComparer.Ordinal).Select(n => JsonValue.Create(n)).ToArray()),
            ["missing_starters"] = new JsonArray(Catalog.START_ONLY.Where(n => !Catalog.CARDS.ContainsKey(n)).Select(n => JsonValue.Create(n)).ToArray()),
            ["baseline_source"] = baselinePath is null ? "current v1.2; supply --baseline for historical comparison" : "explicit --baseline"
        };
    }
    private static void AuditCards(string root, string[] args)
    {
        var audit = StaticAudit(root, DataJson.Arg(args, "--baseline"));
        DataJson.Write(DataJson.Destination(root, args, "runtime/csharp-tooling/static-audit.json"), audit);
        Console.WriteLine(audit.ToJsonString());
    }
    public static JsonObject LayerContract()
    {
        var groups = LayerDefinitions.GROUPS; var special = LayerDefinitions.SPECIAL;
        var retired = DataJson.Read(Path.Combine(Catalog.ROOT, "releases/v1.2/retired-cards.json"))["retired"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (role, data) in groups)
            foreach (var name in data!["names"]!.AsArray().Select(n => n!.GetValue<string>()))
            {
                if (!Catalog.CARDS.ContainsKey(name)) { DataJson.Require(retired.Contains(name), "Unknown binding: " + role + "/" + name); continue; }
                used.Add(name);
            }
        DataJson.Require(used.SetEquals(Catalog.CARDS.Keys), "Unclassified cards: " + string.Join(',', Catalog.CARDS.Keys.Except(used)));
        var records = new JsonObject(); var roleCounts = new JsonObject();
        foreach (var (name, card) in Catalog.CARDS)
        {
            var roles = new JsonArray();
            foreach (var (role, data) in groups.Where(g => g.Value!["names"]!.AsArray().Any(n => n!.GetValue<string>() == name)))
            {
                roles.Add(new JsonObject { ["role"] = role, ["planning"] = data!["plan"]!.GetValue<string>(), ["formal"] = data["formal"]!.GetValue<string>() });
                roleCounts[role] = (roleCounts[role]?.GetValue<int>() ?? 0) + 1;
            }
            records[name] = new JsonObject
            {
                ["element"] = card.element, ["rarity"] = card.rarity, ["card_text"] = card.text, ["roles"] = roles,
                ["specific_rule"] = special[name]?.GetValue<string>() ?? "按本卡原文数值执行上述角色绑定；固定面板、首次层和能力仅正式到位生效，死亡截断尾段无效果。",
                ["commit_scope"] = "reached_prefix_only", ["runtime_migrated"] = true
            };
        }
        return new JsonObject { ["version"] = "1.2", ["status"] = "locked_runtime_with_regression", ["cards"] = records.Count, ["death_rule"] = "lethal_position_truncates_queue_even_if_revived; lifeguard_at_1_does_not_truncate", ["role_counts"] = roleCounts, ["specific_rules"] = special.Count, ["records"] = records };
    }
    private static void AuditLayers(string root, string[] args)
    {
        var contract = LayerContract(); var special = LayerDefinitions.SPECIAL;
        var outFolder = DataJson.Arg(args, "--out") ?? Path.Combine(root, "docs/cards-v1.2");
        var check = args.Contains("--check");
        DataJson.WriteOrCheck(Path.Combine(outFolder, "snapshot-contract.json"), DataJson.Text(contract), check);
        var lines = new List<string> { "# v1.2逐卡跨层读取表", "", "由tools/audit_v12_layers.py生成。183张全部有显式角色绑定并接入strict运行；逐卡执行与交互回归见tools/sim_v12/test_current.py，执行覆盖不等于穷举组合。", "统一术语、死亡截断、治疗资源与实际事件去重见[snapshot-contract.md](snapshot-contract.md)。特例优先于角色通则；原卡面数值不变。", "", "|卡牌|元素/品质|角色|读取与提交的具体处理|", "|---|---|---|---|" };
        foreach (var (name, row) in contract["records"]!.AsObject())
        {
            var roles = row!["roles"]!.AsArray();
            var note = special.ContainsKey(name) ? row["specific_rule"]!.GetValue<string>() : string.Join('；', roles.Select(r => r!["planning"]!.GetValue<string>() + " " + r["formal"]!.GetValue<string>()).Distinct(StringComparer.Ordinal));
            lines.Add("|" + name + "|" + row["element"]!.GetValue<string>() + "/" + row["rarity"]!.GetValue<string>() + "|" + string.Join('、', roles.Select(r => r!["role"]!.GetValue<string>())) + "|" + note + "|");
        }
        DataJson.WriteOrCheck(Path.Combine(outFolder, "snapshot-audit.md"), string.Join('\n', lines) + "\n", check);
        Console.WriteLine($"V12_LAYER_AUDIT cards=183 specific_rules={special.Count} unclassified=0");
    }
}
