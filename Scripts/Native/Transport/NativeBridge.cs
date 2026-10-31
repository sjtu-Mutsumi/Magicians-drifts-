using System.Numerics;
using System.Text.Json;

namespace Magicians.Native;

public static class NativeBridge
{
    public static readonly HashSet<string> PUBLIC_EVENTS = new(StringComparer.Ordinal)
        { "play", "damage", "heal", "shield", "poison", "revive", "auto_execute", "shuffle" };

    public static Dictionary<string, object?> card_view(Instance x, int? cost = null)
    {
        var result = card_data(Catalog.CARDS[x.name]);
        result["uid"] = x.uid;
        result["payable_cost"] = cost;
        return result;
    }

    public static Dictionary<string, object?> card_data(Card c) => NativeJson.Obj(
        ("name", c.name), ("cost", c.cost), ("attack", c.attack), ("defense", c.defense),
        ("layers", c.layers), ("element", c.element), ("rarity", c.rarity),
        ("text", c.text), ("kind", c.kind), ("ability", c.ability));

    public static List<Dictionary<string, object?>> public_state(Battle b)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var s in b.players)
        {
            var stats = b.stats(s.seat);
            var bonuses = new Dictionary<string, object?>();
            foreach (var key in new[] { "bonus", "delay", "temp_attack", "main_def_pen", "main_shield_pen", "main_attack" })
                if (s.fx[key] != 0) bonuses[key] = s.fx[key].ToString();
            result.Add(NativeJson.Obj(("seat", s.seat), ("hp", s.hp), ("shield", s.shield_total),
                ("attack", stats.Item1.ToString()), ("defense", stats.Item2.ToString()),
                ("energy", s.energy), ("layers", s.layers), ("ember", s.ember), ("tide", s.tide),
                ("poison", s.poison), ("active", s.active.OrderBy(x => x, StringComparer.Ordinal).ToArray()),
                ("next_fire", s.next_fire),
                ("modifiers", s.mods.Select(m => NativeJson.Obj(("kind", m.kind), ("value", m.value.ToString()))).ToArray()),
                ("missile_bonuses", bonuses)));
        }
        return result;
    }

    public static List<Dictionary<string, object?>> public_log(Battle b) => b.log.Where(e => PUBLIC_EVENTS.Contains((string)e["kind"]!)).ToList();

    private sealed class Pending : Exception
    {
        public readonly Dictionary<string, object?> Decision;
        public Pending(Dictionary<string, object?> decision) => Decision = decision;
    }

    private sealed class ReplayBattle : StrictBattle
    {
        private sealed class CommandCursor { public int Value; }
        private readonly JsonElement[] _commands;
        private readonly CommandCursor _cursor = new();
        public ReplayBattle(string[][] decks, string[] specializations, BigInteger seed, JsonElement[] commands)
            : base(decks, specializations, seed, true) => _commands = commands;

        private JsonElement Consume(Dictionary<string, object?> decision)
        {
            if (_cursor.Value == _commands.Length) throw new Pending(decision);
            var answer = _commands[_cursor.Value++];
            NativeJson.Require(NativeJson.String(answer, "kind") == (string)decision["kind"]!, "Command does not match current decision");
            return answer;
        }

        public override Instance? choose_play(int seat)
        {
            if (seat != 0) return base.choose_play(seat);
            var s = players[seat];
            var legal = s.hand.Where(x => cost(s, x) <= s.energy).ToDictionary(x => x.uid);
            if (legal.Count == 0) return null;
            var answer = Consume(NativeJson.Obj(("kind", "play"), ("time", time), ("energy", s.energy),
                ("hand", s.hand.Select(x => card_view(x, cost(s, x))).ToArray())));
            var uid = NativeJson.String(answer, "uid");
            if (uid is null) return null;
            NativeJson.Require(legal.ContainsKey(uid), "Card is not in hand or cannot be paid for");
            return legal[uid];
        }

        public override List<Instance> choose(Fighter s, List<Instance> candidates, int count, string purpose, bool optional = false)
        {
            if (s.seat != 0) return base.choose(s, candidates, count, purpose, optional);
            var answer = Consume(NativeJson.Obj(("kind", "select"), ("purpose", purpose), ("count", count),
                ("optional", optional), ("candidates", candidates.Select(x => card_view(x)).ToArray())));
            var ids = answer.TryGetProperty("uids", out var raw) && raw.ValueKind == JsonValueKind.Array
                ? raw.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
            var legal = candidates.ToDictionary(x => x.uid);
            var required = Math.Min(count, candidates.Count);
            NativeJson.Require(ids.Distinct().Count() == ids.Length && ids.All(legal.ContainsKey) &&
                ids.Length <= required && (optional || ids.Length == required), "Invalid selection");
            return ids.Select(id => legal[id]).ToList();
        }

        public Dictionary<string, object?> Respond()
        {
            try
            {
                var result = run();
                NativeJson.Require(_cursor.Value == _commands.Length, "Unused commands after battle ended");
                return NativeJson.Obj(("version", "1.2"), ("status", "finished"), ("time", time),
                    ("public", public_state(this)), ("winner", result["winner"]), ("log", public_log(this)), ("executed", result["executed"]));
            }
            catch (Pending pending)
            {
                return NativeJson.Obj(("version", "1.2"), ("status", "decision"), ("time", time),
                    ("public", public_state(this)), ("decision", pending.Decision), ("log", public_log(this)));
            }
        }
    }

    public static JsonElement Respond(string root, JsonElement request)
    {
        Catalog.Initialize(root);
        if (NativeJson.String(request, "op") == "catalog")
            return NativeJson.Element(NativeJson.Obj(("version", "1.2"),
                ("cards", Catalog.CARDS.Values.Select(card_data).ToArray()), ("routes", Policy.TARGETS)));
        var decks = request.GetProperty("decks").EnumerateArray().Select(d => d.EnumerateArray().Select(n => n.GetString()!).ToArray()).ToArray();
        NativeJson.Require(decks.Length == 2 && decks.All(d => d.Length is > 0 and <= 256), "Expected two nonempty decks of at most 256 cards");
        var commands = request.TryGetProperty("commands", out var raw) ? raw.EnumerateArray().Select(x => x.Clone()).ToArray() : Array.Empty<JsonElement>();
        NativeJson.Require(commands.Length <= 10000, "Too many commands");
        var specs = request.TryGetProperty("specializations", out var supplied)
            ? supplied.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : decks.Select(d => Catalog.SPECIAL.FirstOrDefault(pair => d.Contains(pair.Value)).Key ?? "").ToArray();
        var seed = request.TryGetProperty("seed", out var seedValue) ? BigInteger.Parse(seedValue.GetRawText()) : BigInteger.One;
        var battle = new ReplayBattle(decks, specs, seed, commands);
        if (NativeJson.String(request, "ai_style") is { } style)
            battle.policy_configs = new JsonElement?[] { SixStyles.load_policy("flexible"), SixStyles.load_policy(style) };
        return NativeJson.Element(battle.Respond());
    }
}
