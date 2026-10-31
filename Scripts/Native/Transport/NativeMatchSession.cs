using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;

namespace Magicians.Native;

/// <summary>One native authority thread per match, paused only at player decisions.</summary>
public sealed class NativeMatchSession : IDisposable
{
    private readonly BlockingCollection<JsonElement> _inputs = new(), _outputs = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;
    private readonly BigInteger _seed;
    private readonly string[] _styles;
    private readonly JsonElement?[] _configs;
    private List<Player> _players = new(), _dead = new();
    private List<(int a, object b)> _pairs = new();
    private List<Dictionary<string, object?>> _events = new(), _lastBattles = new();
    private readonly List<Dictionary<string, object?>> _history = new();
    private int _round, _battleCursor, _humanFace, _deleted;
    private bool _autoAll, _autoRound, _disposed;
    private Dictionary<string, object?>? _result;
    public long Revision { get; private set; }

    public NativeMatchSession(string root, BigInteger seed, string[] styles)
    {
        NativeJson.Require(styles.Length == 3 && styles.All(SixStyles.IDS.Contains), "Expected three valid v1.2 AI styles");
        Catalog.Initialize(root);
        _seed = seed; _styles = new[] { "flexible" }.Concat(styles).ToArray();
        _configs = _styles.Select(s => (JsonElement?)SixStyles.load_policy(s)).ToArray();
        _worker = new Thread(Run) { IsBackground = true, Name = "Magicians native match" };
        _worker.Start();
    }

    public JsonElement Receive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        return _outputs.Take(timeout.Token);
    }

    public JsonElement Send(JsonElement command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeJson.Require(command.TryGetProperty("revision", out var revision) && revision.TryGetInt64(out var number) && number == Revision,
            "Stale match revision; reload the current decision");
        NativeJson.Require(_result is null, "Match has finished");
        _inputs.Add(command.Clone(), _stop.Token);
        return Receive();
    }

    public JsonElement Review(int round)
    {
        NativeJson.Require(round >= 1 && round <= _history.Count, "Round has not finished");
        return NativeJson.Element(NativeJson.Obj(("version", "1.2"), ("status", "review"),
            ("revision", Revision), ("review", _history[round - 1])));
    }

    private static List<Dictionary<string, object?>> Public(IEnumerable<Player> players) =>
        players.Select(p => NativeJson.Obj(("pid", p.pid), ("hp", p.hp), ("spec", p.spec), ("route", p.route))).ToList();

    private Dictionary<string, object?> View()
    {
        var all = _players.Concat(_dead).DistinctBy(p => p.pid).ToArray();
        var own = all.FirstOrDefault(p => p.pid == 0);
        var previous = _history.LastOrDefault(row => (int)row["round"]! < _round);
        return NativeJson.Obj(("version", "1.2"), ("round", _round), ("seed", _seed),
            ("standings", Public(all)), ("ai_styles", _styles.Skip(1).ToArray()),
            ("pairs", _pairs.Select(pair => new object[] { pair.a, pair.b }).ToArray()),
            ("events", _events.ToList()), ("last_battles", _lastBattles),
            ("previous_battles", previous is null ? new List<Dictionary<string, object?>>() : previous["battles"]),
            ("completed_rounds", _history.Select(row => row["round"]).ToArray()),
            ("own", own is null ? null : NativeJson.Obj(("hp", own.hp), ("points", own.points),
                ("bank", own.bank), ("income", own.income), ("carry", own.carry), ("bonus", own.bonus),
                ("spec", own.spec), ("route", own.route), ("deck", own.deck.Select((item, index) =>
                { var card = NativeBridge.card_data(Catalog.CARDS[item.name]); card["index"] = index; return card; }).ToArray()))));
    }

    private void Emit(Dictionary<string, object?> payload)
    {
        var view = View();
        view["revision"] = ++Revision;
        foreach (var (key, value) in payload) view[key] = value;
        _outputs.Add(NativeJson.Element(view), _stop.Token);
    }

    private void RecordRound() => _history.Add(NativeJson.Obj(("round", _round), ("events", _events.ToList()),
        ("standings", Public(_players.Concat(_dead).DistinctBy(p => p.pid))), ("battles", _lastBattles)));

    private JsonElement Ask(Dictionary<string, object?> question, Action<JsonElement> validate,
        Dictionary<string, object?>? context = null)
    {
        string? error = null;
        while (true)
        {
            var payload = NativeJson.Obj(("status", "decision"), ("decision", question), ("error", error));
            if (context is not null) foreach (var (key, value) in context) payload[key] = value;
            Emit(payload);
            var command = _inputs.Take(_stop.Token);
            var kind = NativeJson.String(command, "kind");
            if (kind == "delegate_match") { _autoAll = true; return NativeJson.Element(NativeJson.Obj(("kind", "auto"))); }
            if (kind == "delegate_round" && new[] { "draft", "refresh", "delete" }.Contains((string)question["kind"]!))
            { _autoRound = true; return NativeJson.Element(NativeJson.Obj(("kind", "auto"))); }
            try { validate(command); return command; }
            catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or InvalidOperationException)
            { error = exception.Message; }
        }
    }

    private void Run()
    {
        try
        {
            var tournament = new ClientTournament(this);
            _result = tournament.simulate(_seed, trace: true, policy_by_seat: _configs,
                refresh_threshold: p => p.pid == 0 && !(_autoAll || _autoRound) ? 1 : 2);
            RecordRound();
            var rawPlacements = (IEnumerable<Dictionary<string, object?>>)_result["placements"]!;
            var placements = rawPlacements.Select(row => new[] { "pid", "hp", "round", "spec", "route", "rank", "score" }
                .ToDictionary(key => key, key => row[key], StringComparer.Ordinal)).ToList();
            Emit(NativeJson.Obj(("status", "finished"), ("placements", placements), ("battles", _result["battles"])));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_stop.IsCancellationRequested) Emit(NativeJson.Obj(("status", "error"), ("error", error.Message)));
        }
    }

    private sealed class ClientTournament : Tournament
    {
        private readonly NativeMatchSession _owner;
        private readonly JsonElement _economy;
        public ClientTournament(NativeMatchSession owner)
        {
            _owner = owner;
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Catalog.ROOT, "tools/sim_v12/rules/economy.json")));
            _economy = document.RootElement.Clone();
        }

        public override List<(int a, object b)> schedule(List<Player> players, List<Player> dead,
            Dictionary<object, object> last, Dictionary<(string, string), int> meetings, PythonRandom rng)
        {
            _owner._players = players; _owner._dead = dead;
            if (_owner._round != 0)
            {
                _owner.RecordRound();
                if (!_owner._autoAll)
                    _owner.Ask(NativeJson.Obj(("kind", "round"), ("eliminated", dead.Select(p => p.pid).ToArray())),
                        command => NativeJson.Require(NativeJson.String(command, "kind") == "next_round", "Expected next_round"));
            }
            _owner._round++; _owner._autoRound = false; _owner._humanFace = 0; _owner._deleted = 0;
            _owner._events = new(); _owner._lastBattles = new(); _owner._battleCursor = 0;
            return _owner._pairs = base.schedule(players, dead, last, meetings, rng);
        }

        private (int Fee, int Remaining, List<int> Legal)? Deletion(Player p, int round, int used)
        {
            var rule = _economy.GetProperty("deletions").GetProperty(round <= 3 ? "rounds_1_to_3" : round <= 6 ? "rounds_4_to_6" : "rounds_7_plus");
            var limit = rule[0].GetInt32();
            if (used >= limit || p.deck.Count <= _economy.GetProperty("minimum_deck_size").GetInt32()) return null;
            var fee = rule[1][used].GetInt32();
            if (p.points < fee) return null;
            var main = p.deck.Count(x => Catalog.CARDS[x.name].element == p.spec && Catalog.CARDS[x.name].kind != "specialization");
            var legal = p.deck.Select((x, i) => (Card: Catalog.CARDS[x.name], Index: i))
                .Where(x => !x.Card.ability && x.Card.rarity != "legendary" && !(x.Card.element == p.spec && main <= 3))
                .Select(x => x.Index).ToList();
            return legal.Count == 0 ? null : (fee, limit - used, legal);
        }

        public override HashSet<int> buy_phase(Player p, List<Player> players, Pool pool, int round, List<Dictionary<string, object?>> events)
        {
            _owner._events = events;
            if (p.pid != 0 || _owner._autoAll || _owner._autoRound) return base.buy_phase(p, players, pool, round, events);
            var face = _owner._humanFace++;
            var bought = new HashSet<int>();
            while (true)
            {
                var offers = new List<Dictionary<string, object?>>();
                var legal = new Dictionary<int, (string Name, int Cost, string? Old)>();
                for (var slot = 0; slot < pool.offers[p.pid].Count; slot++)
                {
                    var token = pool.offers[p.pid][slot];
                    if (token is null) continue;
                    var name = pool.tokens[token];
                    var (cost, old) = price_and_replace(p, name, round);
                    var card = NativeBridge.card_data(Catalog.CARDS[name]);
                    card["slot"] = slot; card["price"] = cost; card["replaces"] = old;
                    card["affordable"] = cost.HasValue && cost.Value <= p.points;
                    offers.Add(card);
                    if (cost.HasValue && cost.Value <= p.points) legal[slot] = (name, cost.Value, old);
                }
                var deletion = Deletion(p, round, _owner._deleted);
                var answer = _owner.Ask(NativeJson.Obj(("kind", "draft"), ("offers", offers),
                    ("can_refresh", face == 0 && p.points >= _economy.GetProperty("refresh_cost").GetInt32() && bought.Count < 8),
                    ("can_delete", deletion.HasValue), ("deletion", deletion.HasValue ? NativeJson.Obj(
                        ("fee", deletion.Value.Fee), ("remaining", deletion.Value.Remaining), ("legal", deletion.Value.Legal)) : null)), command =>
                {
                    var kind = NativeJson.String(command, "kind");
                    NativeJson.Require(kind is "buy" or "delete" or "end_face", "Expected purchase, deletion or end_face");
                    if (kind == "buy") NativeJson.Require(NativeJson.Int(command, "slot") is int slot && legal.ContainsKey(slot), "Unavailable or unaffordable offer");
                    if (kind == "delete") NativeJson.Require(deletion.HasValue && NativeJson.Int(command, "index") is int index && deletion.Value.Legal.Contains(index), "Protected or unavailable card");
                });
                switch (NativeJson.String(answer, "kind"))
                {
                    case "auto": bought.UnionWith(base.buy_phase(p, players, pool, round, events)); return bought;
                    case "end_face": return bought;
                    case "delete":
                        var index = NativeJson.Int(answer, "index")!.Value;
                        var item = p.deck[index]; p.deck.RemoveAt(index); pool.remove(item.token);
                        p.points -= deletion!.Value.Fee; _owner._deleted++;
                        p.actions.Add(NativeJson.Obj(("kind", "delete"), ("name", item.name), ("cost", deletion.Value.Fee)));
                        pool.audit(players); break;
                    default:
                        var slot = NativeJson.Int(answer, "slot")!.Value; var candidate = legal[slot];
                        PurchaseSpecific(p, candidate.Name, slot, candidate.Cost, candidate.Old, pool, events);
                        bought.Add(slot); pool.audit(players); break;
                }
            }
        }

        public override bool? refresh_wanted(Player p, int round, List<string> unbought, List<Dictionary<string, object?>> publicPlayers)
        {
            if (p.pid != 0 || _owner._autoAll || _owner._autoRound) return base.refresh_wanted(p, round, unbought, publicPlayers);
            var answer = _owner.Ask(NativeJson.Obj(("kind", "refresh"), ("cost", _economy.GetProperty("refresh_cost").GetInt32())),
                command => NativeJson.Require(NativeJson.String(command, "kind") is "refresh" or "keep_face", "Expected refresh or keep_face"));
            return NativeJson.String(answer, "kind") == "auto" ? base.refresh_wanted(p, round, unbought, publicPlayers) : NativeJson.String(answer, "kind") == "refresh";
        }

        public override void delete_phase(Player p, Pool pool, int round)
        {
            if (p.pid != 0 || ((_owner._autoAll || _owner._autoRound) && _owner._deleted == 0)) { base.delete_phase(p, pool, round); return; }
            if (!(_owner._autoAll || _owner._autoRound)) return;
            while (Deletion(p, round, _owner._deleted) is { } deletion)
            {
                var options = deletion.Legal.Select(index => (Value: Construction.deletion_value(p, p.deck[index].name, deletion.Fee), Index: index))
                    .OrderByDescending(x => x.Value).ThenByDescending(x => x.Index).ToArray();
                if (options[0].Value < 0) return;
                var item = p.deck[options[0].Index]; p.deck.RemoveAt(options[0].Index); _owner._deleted++;
                pool.remove(item.token); p.points -= deletion.Fee;
                p.actions.Add(NativeJson.Obj(("kind", "delete"), ("name", item.name), ("cost", deletion.Fee)));
                pool.audit(_owner._players);
            }
        }

        public override Battle CreateBattle(string[][] decks, string[] specs, BigInteger seed, bool trace, string[] styles, string sequenceMode) =>
            new ClientBattle(_owner, decks, specs, seed, trace, styles, sequenceMode);
    }

    private sealed class ClientBattle : StrictBattle
    {
        private readonly NativeMatchSession _owner;
        private readonly ClientBattle _authority;
        private readonly (int a, object b) _participants;
        private readonly int _humanSeat;
        private bool _delegated;
        private List<string> _userPlan = new();
        private int _userPlanStage = -1;

        public ClientBattle(NativeMatchSession owner, string[][] decks, string[] specs, BigInteger seed, bool trace, string[] styles, string sequenceMode)
            : base(decks, specs, seed, trace, styles: styles, sequence_mode: sequenceMode)
        {
            _owner = owner; _authority = this;
            _participants = owner._pairs[owner._battleCursor++];
            _humanSeat = _participants.a == 0 ? 0 : _participants.b is int pid && pid == 0 ? 1 : -1;
        }

        private Dictionary<string, object?> Context() => NativeJson.Obj(("combat", NativeJson.Obj(
            ("time", _authority.time), ("human_seat", _humanSeat), ("participants", new object[] { _participants.a, _participants.b }),
            ("draw_pile", players[_humanSeat].pile.OrderBy(x => x.name, StringComparer.Ordinal).ThenBy(x => x.uid, StringComparer.Ordinal).Select(x => NativeBridge.card_view(x)).ToArray()),
            ("discard_pile", players[_humanSeat].discard.Select(x => NativeBridge.card_view(x)).ToArray()),
            ("planned", _authority._userPlan.ToArray()), ("public", NativeBridge.public_state(_authority)),
            ("log", NativeBridge.public_log(_authority)))));

        public override List<Instance> choose(Fighter s, List<Instance> candidates, int count, string purpose, bool optional = false)
        {
            if (s.seat != _humanSeat || _owner._autoAll || _authority._delegated) return base.choose(s, candidates, count, purpose, optional);
            if (count <= 0 || candidates.Count == 0) return new();
            if (candidates.Count == 1) return new(candidates);
            var legal = candidates.ToDictionary(x => x.uid);
            var required = Math.Min(count, candidates.Count);
            var answer = _owner.Ask(NativeJson.Obj(("kind", "select"), ("purpose", purpose), ("count", required),
                ("optional", optional), ("energy", s.energy), ("candidates", candidates.Select(x => NativeBridge.card_view(x)).ToArray())), command =>
            {
                var kind = NativeJson.String(command, "kind");
                NativeJson.Require(kind is "select" or "delegate_battle", "Expected selection");
                if (kind == "delegate_battle") return;
                var ids = command.TryGetProperty("uids", out var raw) && raw.ValueKind == JsonValueKind.Array
                    ? raw.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
                NativeJson.Require(ids.Distinct().Count() == ids.Length && ids.All(legal.ContainsKey), "Invalid selection targets");
                NativeJson.Require(ids.Length <= required && (optional || ids.Length == required), "Invalid selection count");
            }, Context());
            if (NativeJson.String(answer, "kind") is "auto" or "delegate_battle")
            { _authority._delegated = true; return base.choose(s, candidates, count, purpose, optional); }
            return answer.TryGetProperty("uids", out var chosen) ? chosen.EnumerateArray().Select(x => legal[x.GetString()!]).ToList() : new();
        }

        public override Instance? choose_play(int seat)
        {
            if (seat != _humanSeat || _owner._autoAll || _authority._delegated) return base.choose_play(seat);
            var s = players[seat];
            if (_authority._userPlanStage != s.stage) { _authority._userPlanStage = s.stage; _authority._userPlan = new(); }
            var legal = s.hand.Where(x => cost(s, x) <= s.energy).ToDictionary(x => x.uid);
            var answer = _owner.Ask(NativeJson.Obj(("kind", "play"), ("energy", s.energy),
                ("hand", s.hand.Select(x => NativeBridge.card_view(x, cost(s, x))).ToArray())), command =>
            {
                var kind = NativeJson.String(command, "kind");
                NativeJson.Require(kind is "play" or "delegate_battle", "Expected play or delegate_battle");
                if (kind == "play") NativeJson.Require(NativeJson.String(command, "uid") is not { } uid || legal.ContainsKey(uid), "Unavailable or unaffordable card");
            }, Context());
            if (NativeJson.String(answer, "kind") is "auto" or "delegate_battle")
            { _authority._delegated = true; return base.choose_play(seat); }
            var chosen = NativeJson.String(answer, "uid") is { } selected ? legal[selected] : null;
            if (chosen is not null) _authority._userPlan.Add(chosen.name);
            return chosen;
        }

        public override Dictionary<string, object?> run(long max_time = 10000, int max_actions = 10000)
        {
            var result = base.run(max_time, max_actions);
            _owner._lastBattles.Add(NativeJson.Obj(("a", _participants.a), ("b", _participants.b),
                ("winner", result["winner"]), ("hp", result["hp"]), ("time", result["time"]), ("log", NativeBridge.public_log(this))));
            return result;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _stop.Cancel();
        if (Thread.CurrentThread != _worker) _worker.Join(TimeSpan.FromSeconds(2));
    }
}
