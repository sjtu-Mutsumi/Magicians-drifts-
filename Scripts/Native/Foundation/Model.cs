using System.Numerics;

namespace Magicians.Native;

public sealed class NumMap : Dictionary<string, Q>
{
    public NumMap() : base(StringComparer.Ordinal) { }
    public new Q this[string key] { get => TryGetValue(key, out var value) ? value : Q.Zero; set => base[key] = value; }
    public Q get(string key, Q fallback = default) => TryGetValue(key, out var value) ? value : fallback;
    public NumMap Clone() { var result = new NumMap(); foreach (var pair in this) result.Add(pair.Key, pair.Value); return result; }
}

public sealed class IntCounter : Dictionary<string, int>
{
    public IntCounter() : base(StringComparer.Ordinal) { }
    public IntCounter(IEnumerable<string> values) : this() { foreach (var value in values) this[value]++; }
    public new int this[string key] { get => TryGetValue(key, out var value) ? value : 0; set => base[key] = value; }
    public int get(string key, int fallback = 0) => TryGetValue(key, out var value) ? value : fallback;
    public IntCounter Clone() { var result = new IntCounter(); foreach (var pair in this) result.Add(pair.Key, pair.Value); return result; }
}

public sealed class PendingState : Dictionary<string, Q>
{
    public List<Q> heal_batches = new();
    public PendingState() : base(StringComparer.Ordinal) { }
    public new Q this[string key] { get => TryGetValue(key, out var value) ? value : Q.Zero; set => base[key] = value; }
    public Q get(string key, Q fallback = default) => TryGetValue(key, out var value) ? value : fallback;
    public new void Clear() { base.Clear(); heal_batches.Clear(); }
    public PendingState Clone() { var result = new PendingState(); foreach (var pair in this) result.Add(pair.Key, pair.Value); result.heal_batches = new(heal_batches); return result; }
}

public sealed record Instance(string uid, string name);
public sealed record PlannedCard
{
    public string uid { get; init; }
    public string name { get; init; }
    public int cost { get; init; }
    public List<(string purpose, string[] uids)> choices { get; init; }
    public PlannedCard(string uid, string name, int cost, List<(string purpose, string[] uids)>? choices = null)
    { this.uid = uid; this.name = name; this.cost = cost; this.choices = choices ?? new(); }
}
public sealed record Modifier(string kind, Q value, int owner, long expires, object[]? stamp = null);

public sealed class Fighter
{
    public int seat;
    public string[] deck;
    public string specialization;
    public BigInteger seed;
    public int hp = 1000, max_hp = 1000, shield, stage, played, spent, energy, last_main, poison, wind_marks, tide, spent_tide, ember, spent_ember;
    public long next_fire = 100, last_fire = -1, poison_start, poison_due = 1_000_000_000_000, guard_time = -1;
    public Q attack = new(100), defense = new(40);
    public List<Q[]> temporary = new();
    public IntCounter layers = new(), discounts = new(), counts = new();
    public HashSet<string> active = new(StringComparer.Ordinal), seen = new(StringComparer.Ordinal), drawn = new(StringComparer.Ordinal);
    public List<Instance> pile = new(), hand = new(), discard = new(), removed = new(), resolving = new();
    public NumMap fx = new();
    public PendingState pending = new();
    public List<Q> heal_batches => pending.heal_batches;
    public List<Modifier> mods = new();
    public List<(Q fixedDamage, Q ratio, int marks, bool leech)> subs = new(), permanent_subs = new();
    public List<(Q ratio, int speed)> strips = new();
    public Q[] rewind = Array.Empty<Q>();
    public bool revived, guard_used;
    public List<(Q amount, bool temporary, bool no_decay, Q conversion)> shield_events = new();
    public List<string> known_top = new();
    public PythonRandom rng;
    public Instance[] instances;

    public Fighter(int seat, IEnumerable<string> deck, string specialization = "", BigInteger seed = default)
    {
        this.seat = seat; this.deck = deck.ToArray(); this.specialization = specialization; this.seed = seed;
        foreach (var element in Catalog.ELEMENTS) layers[element] = 0;
        rng = new PythonRandom(seed);
        instances = this.deck.Select((name, index) => new Instance($"{seat}:{index}", name)).ToArray();
        pile = instances.ToList(); rng.shuffle(pile);
    }
    private Fighter(Fighter source) { deck = source.deck; specialization = source.specialization; rng = source.rng.Clone(); instances = source.instances; CopyFrom(source, false); }
    public bool has(string name) => active.Contains(name);
    public int shield_total => checked(shield + temporary.Sum(batch => (int)batch[0]));
    public Q value(string kind) { Q result = 0; foreach (var modifier in mods) if (modifier.kind == kind) result += modifier.value; return result; }
    public bool guarded => value("negative_guard") > 0;
    public Fighter Clone() => new(this);
    public void CopyFrom(Fighter source, bool preserveContainers = true)
    {
        if (ReferenceEquals(this, source)) return;
        seat = source.seat; deck = source.deck; specialization = source.specialization; seed = source.seed;
        hp = source.hp; max_hp = source.max_hp; attack = source.attack; defense = source.defense; shield = source.shield;
        stage = source.stage; played = source.played; spent = source.spent; energy = source.energy; next_fire = source.next_fire; last_fire = source.last_fire; last_main = source.last_main;
        poison = source.poison; poison_start = source.poison_start; poison_due = source.poison_due; wind_marks = source.wind_marks; tide = source.tide; spent_tide = source.spent_tide;
        ember = source.ember; spent_ember = source.spent_ember; rewind = source.rewind; revived = source.revived; guard_used = source.guard_used; guard_time = source.guard_time;
        rng = source.rng.Clone(); instances = source.instances;
        if (!preserveContainers)
        {
            layers = new(); discounts = new(); counts = new(); active = new(StringComparer.Ordinal); seen = new(StringComparer.Ordinal); drawn = new(StringComparer.Ordinal);
            pile = new(); hand = new(); discard = new(); removed = new(); resolving = new(); fx = new(); pending = new(); mods = new(); temporary = new();
            subs = new(); permanent_subs = new(); strips = new(); shield_events = new(); known_top = new();
        }
        CopyMap(layers, source.layers); CopyMap(discounts, source.discounts); CopyMap(counts, source.counts); CopyMap(fx, source.fx);
        CopyMap(pending, source.pending); Replace(pending.heal_batches, source.pending.heal_batches);
        active.Clear(); active.UnionWith(source.active); seen.Clear(); seen.UnionWith(source.seen); drawn.Clear(); drawn.UnionWith(source.drawn);
        Replace(pile, source.pile); Replace(hand, source.hand); Replace(discard, source.discard); Replace(removed, source.removed); Replace(resolving, source.resolving);
        Replace(mods, source.mods.Select(modifier => modifier with { stamp = modifier.stamp?.ToArray() }));
        Replace(temporary, source.temporary.Select(batch => batch.ToArray()));
        Replace(subs, source.subs); Replace(permanent_subs, source.permanent_subs); Replace(strips, source.strips); Replace(shield_events, source.shield_events); Replace(known_top, source.known_top);
    }
    private static void Replace<T>(List<T> destination, IEnumerable<T> source) { var values = source.ToArray(); destination.Clear(); destination.AddRange(values); }
    private static void CopyMap<T>(Dictionary<string, T> destination, Dictionary<string, T> source) { var values = source.ToArray(); destination.Clear(); foreach (var pair in values) destination.Add(pair.Key, pair.Value); }
    public void assert_instances()
    {
        var expected = instances.GroupBy(instance => instance.uid).ToDictionary(group => group.Key, group => group.Count());
        var actual = pile.Concat(hand).Concat(discard).Concat(removed).Concat(resolving).GroupBy(instance => instance.uid).ToDictionary(group => group.Key, group => group.Count());
        if (expected.Count != actual.Count || expected.Any(pair => actual.GetValueOrDefault(pair.Key) != pair.Value)) throw new InvalidOperationException("Instance conservation");
        if (energy < 0 || shield < 0 || temporary.Any(batch => batch[0] < 0) || ember < 0 || ember > 5) throw new InvalidOperationException("Invalid fighter resources");
    }
}
