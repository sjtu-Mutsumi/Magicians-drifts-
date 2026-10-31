using System.Text.Json;
using Magicians.Native;

internal static class NativeFixtures
{
    private static object? Normalize(object? value) => value switch
    {
        Instance item => NativeJson.Obj(("uid", item.uid), ("name", item.name)),
        Modifier mod => NativeJson.Obj(("kind", mod.kind), ("value", mod.value), ("owner", mod.owner), ("expires", mod.expires), ("stamp", mod.stamp ?? Array.Empty<object>())),
        Operation operation => new[] { (object)operation.kind, operation.target, Normalize(operation.value) },
        _ => value,
    };

    private static Dictionary<string, object?> FighterState(Fighter s)
    {
        var pending = s.pending.ToDictionary(p => p.Key, p => (object?)p.Value);
        if (s.pending.heal_batches.Count > 0) pending["heal_batches"] = s.pending.heal_batches;
        var effects = s.fx.ToDictionary(p => p.Key, p => (object?)p.Value);
        if (s.strips.Count > 0) effects["strip"] = s.strips;
        var random = s.rng.getstate();
        return NativeJson.Obj(("seat", s.seat), ("deck", s.deck), ("specialization", s.specialization),
            ("seed", s.seed), ("hp", s.hp), ("max_hp", s.max_hp), ("attack", s.attack), ("defense", s.defense),
            ("shield", s.shield), ("temporary", s.temporary), ("layers", s.layers),
            ("active", s.active.OrderBy(x => x, StringComparer.Ordinal).ToArray()),
            ("seen", s.seen.OrderBy(x => x, StringComparer.Ordinal).ToArray()),
            ("pile", s.pile.Select(x => Normalize(x)).ToArray()), ("hand", s.hand.Select(x => Normalize(x)).ToArray()),
            ("discard", s.discard.Select(x => Normalize(x)).ToArray()), ("removed", s.removed.Select(x => Normalize(x)).ToArray()),
            ("resolving", s.resolving.Select(x => Normalize(x)).ToArray()), ("discounts", s.discounts),
            ("drawn", s.drawn.OrderBy(x => x, StringComparer.Ordinal).ToArray()), ("fx", effects), ("pending", pending),
            ("mods", s.mods.Select(x => Normalize(x)).ToArray()), ("subs", s.subs), ("permanent_subs", s.permanent_subs),
            ("counts", s.counts), ("stage", s.stage), ("played", s.played), ("spent", s.spent), ("energy", s.energy),
            ("next_fire", s.next_fire), ("last_fire", s.last_fire), ("last_main", s.last_main), ("poison", s.poison),
            ("poison_start", s.poison_start), ("poison_due", s.poison_due), ("wind_marks", s.wind_marks), ("tide", s.tide),
            ("spent_tide", s.spent_tide), ("ember", s.ember), ("spent_ember", s.spent_ember), ("rewind", s.rewind),
            ("revived", s.revived), ("guard_used", s.guard_used), ("guard_time", s.guard_time), ("shield_events", s.shield_events),
            ("known_top", s.known_top), ("instances", s.instances.Select(x => Normalize(x)).ToArray()),
            ("rng", new object?[] { 3, random.words.Select(x => (object)x).Append(random.index).ToArray(), null }));
    }

    private static object Snapshot(Battle b) => NativeJson.Obj(("players", b.players.Select(FighterState).ToArray()),
        ("time", b.time), ("log", b.log), ("operations", b.operations.Select(x => Normalize(x)).ToArray()),
        ("executed", b.executed), ("executed_by_seat", b.executed_by_seat), ("flow_by_seat", b.flow_by_seat),
        ("cycle_decisions", b.cycle_decisions), ("sequence_skips", b.sequence_skips), ("planned_count", b.planned_count));

    public static JsonElement Card(JsonElement request)
    {
        var name = request.GetProperty("name").GetString()!;
        var rich = request.GetProperty("rich").GetBoolean();
        var battle = new StrictBattle(new[] { new[] { name, "火花", "掠影", "护盾", "短促" }, Catalog.START }, seed: 19, trace: true);
        battle.begin_stage();
        var s = battle.players[0]; var enemy = battle.players[1];
        s.pile = s.instances.ToList(); s.hand = s.pile.Take(3).ToList(); s.pile = s.pile.Skip(3).ToList();
        s.discard = new(); s.drawn = s.hand.Select(x => x.uid).ToHashSet(StringComparer.Ordinal); s.energy = 100;
        if (rich)
        {
            s.hp = 400; s.shield = 250; enemy.shield = 150; enemy.poison = 6;
            var element = Catalog.ELEMENTS.Contains(Catalog.CARDS[name].element) ? Catalog.CARDS[name].element : "fire";
            s.specialization = element; s.active.Add(Catalog.SPECIAL[element]);
            foreach (var el in Catalog.ELEMENTS) s.layers[el] = 5;
            s.ember = element == "fire" ? 3 : 0; s.tide = element == "water" ? 75 : 0;
        }
        var before = NativeJson.Element(Snapshot(battle));
        battle.play_local(0, "0:0");
        return NativeJson.Element(NativeJson.Obj(("kind", "card"), ("name", name), ("rich", rich), ("before", before), ("after", Snapshot(battle))));
    }

    public static JsonElement Battle(JsonElement request)
    {
        var decks = request.GetProperty("decks").EnumerateArray().Select(x => x.EnumerateArray().Select(y => y.GetString()!).ToArray()).ToArray();
        var specs = request.GetProperty("specializations").EnumerateArray().Select(x => x.GetString()!).ToArray();
        return NativeJson.Element(new StrictBattle(decks, specs, request.GetProperty("seed").GetInt32(), true).run());
    }
}
