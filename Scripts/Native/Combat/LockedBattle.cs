using System.Numerics;

namespace Magicians.Native;

public sealed class Frontier : Exception
{
    public readonly string primitive;public readonly object?[] args;
    public Frontier(string primitive,object?[] args):base(primitive){this.primitive=primitive;this.args=args;}
}
public sealed record ProbeCache(string name,object? result,Fighter state,Dictionary<int,int> cursors);

public class LockedBattle : Battle
{
    private static readonly HashSet<string> PrivateCalls=new(){"draw","shuffle","search","move_hand","recover","choose","random_insert"};
    protected int _depth,_atomic,_wave_depth;protected bool _draining;
    protected Queue<(string kind,int seat)> _events=new();protected HashSet<int> _cut=new();
    protected Dictionary<int,Entry> _entries=new();protected Dictionary<int,int> _cursors=new();
    protected List<Fighter>? _slot;protected int[]? _old_left;
    protected Dictionary<int,int> _decay_credit=new();
    protected Entry? _planning_entry;
    protected int _actor,_probe_cursor;protected List<ProbeCache> _cache=new();
    public override List<Fighter>? slot_snapshot=>_slot;
    public LockedBattle(IEnumerable<IEnumerable<string>> decks,IEnumerable<string>? specializations=null,BigInteger? seed=null,bool trace=false,
        Func<Dictionary<string,object?>,List<Dictionary<string,object?>>,int,string,IEnumerable<string>>? chooser=null,string[]? styles=null,string sequence_mode="locked")
        :base(decks,specializations,seed,trace,chooser,styles,sequence_mode){}
    public static PrivateState private_state(Fighter s)=>new(s.Clone());
    public static void restore_private(Fighter s,PrivateState state)
    {
        var value=state.state;s.pile=new(value.pile);s.hand=new(value.hand);s.discard=new(value.discard);s.removed=new(value.removed);s.resolving=new(value.resolving);s.discounts=value.discounts.Clone();s.drawn=new(value.drawn);s.known_top=new(value.known_top);s.rng=value.rng.Clone();
    }
    private static object? CloneResult(object? result)=>result switch
    {List<Instance> items=>new List<Instance>(items),List<Packet> packets=>packets.Select(p=>{var copy=new Packet();foreach(var row in p)copy[row.Key]=row.Value;return copy;}).ToList(),_=>result};
    protected override object? Primitive(string name,object?[] args,Func<object?> run)
    {
        if(_depth>0)return run();
        if(_phase=="probe")
        {
            var k=_probe_cursor++;if(k>=_cache.Count)throw new Frontier(name,args);
            var cache=_cache[k];if(cache.name!=name)throw new InvalidOperationException($"Primitive replay mismatch {cache.name} {name}");
            players[_actor].CopyFrom(cache.state);operations=new();_cursors=new(cache.cursors);return CloneResult(cache.result);
        }
        if(PrivateCalls.Contains(name)&&_phase=="plan")
        {var s=(Fighter)args[0]!;_depth++;object? result;try{result=run();}finally{_depth--;}_planning_entry!.receipts.Add(new(name,CloneResult(result),private_state(s),s.fx["shuffled"]));return result;}
        if(PrivateCalls.Contains(name)&&_phase=="actual"&&_entries.Count>0)
        {
            var s=(Fighter)args[0]!;var i=s.seat;var entry=_entries[i];var k=_cursors.GetValueOrDefault(i);
            if(k>=entry.receipts.Count)throw new InvalidOperationException($"Missing private receipt {entry.name} {name} {k}");var receipt=entry.receipts[k];if(receipt.name!=name)throw new InvalidOperationException($"Private receipt order {entry.name} {receipt.name} {name}");_cursors[i]=k+1;
            var beforeHand=s.hand.Select(x=>x.uid).ToHashSet();var beforeDiscard=s.discard.Select(x=>x.uid).ToHashSet();var previous=s.fx["shuffled"];restore_private(s,receipt.state);var delta=Q.Max(0,receipt.shuffles-previous);s.fx["shuffled"]+=delta;
            if(delta!=0&&s.has("灰烬循环"))add_ember(s,2*(int)delta);var flow=flow_by_seat[i];var gained=s.hand.Select(x=>x.uid).Except(beforeHand).Count();if(name=="recover")flow["recovered"]+=gained;else if(name is "draw" or "search")flow["instant_drawn"]+=gained;flow["discarded"]+=s.discard.Select(x=>x.uid).Except(beforeDiscard).Count();flow["shuffles"]+=delta;if(name is "search" or "move_hand")flow["topdecked"]+=Math.Max(0,s.known_top.Count);return CloneResult(receipt.result);
        }
        _depth++;try{return run();}finally{_depth--;}
    }
    public override T plan_read<T>(int i,string key,T value)
    {if(_phase=="plan"){_planning_entry!.flags[key]=value;return value;}if((_phase is "actual" or "probe")&&_entries.TryGetValue(i,out var entry))return(T)entry.flags[key]!;return value;}
    public override int cost(Fighter s,Instance inst)=>_phase is "actual" or "probe"&&_entries.TryGetValue(s.seat,out var entry)&&entry.uid==inst.uid?entry.cost:base.cost(s,inst);
    public override Packet packet(int i,Q raw,string kind="missile",Packet? extra=null)
    {extra??=new();if(_phase=="probe"&&!extra.ContainsKey("defense"))extra["defense"]=stats(1-i).defense;return base.packet(i,raw,kind,extra);}
    public override List<List<Entry>> plan_stage(int max_actions=10000)
    {
        var sequences=new List<List<Entry>>();for(int i=0;i<2;i++)
        {
            var shadow=(LockedBattle)CloneShell();shadow.players=ClonePlayers(players);var e=shadow.players[1-i];e.deck=Array.Empty<string>();e.instances=Array.Empty<Instance>();e.pile=new();e.hand=new();e.discard=new();e.removed=new();e.resolving=new();e.discounts=new();e.drawn=new();e.seen=new();e.rng=new PythonRandom(0);
            shadow._phase="plan";shadow._depth=0;shadow._entries=new();shadow.trace=false;shadow.log=new();shadow.operations=new();shadow.executed=new();shadow.executed_by_seat=new[]{new NumMap(),new NumMap()};shadow._decay_credit=new();shadow._events=new();shadow._cut=new();shadow._economic_visits=new();
            var sequence=new List<Entry>();while(true){var inst=shadow.choose_play(i);if(inst is null)break;if(sequence.Count>=max_actions)throw new InvalidOperationException("Private plan did not terminate");var entry=new Entry(inst.uid,inst.name,shadow.cost(shadow.players[i],inst));shadow._planning_entry=entry;shadow.operations=new();shadow.play_local(i,inst.uid);
                foreach(var op in shadow.operations){if(op.kind=="poison"){var(source,delta)=((int,int))op.value;shadow.change_poison(shadow.players[source],shadow.players[op.target],delta);}else if(op.kind=="modifier")shadow.players[op.target].mods.Add((Modifier)op.value);else if(op.kind=="def_down_permanent")shadow.players[op.target].fx["permanent_def_down"]+=(Q)op.value;}
                var s=shadow.players[i];entry.after=private_state(s);entry.energy=s.energy;entry.spent=s.spent;sequence.Add(entry);}
            sequences.Add(sequence);
        }
        return sequences;
    }
    public override void begin_atomic(){if(_phase=="actual")_atomic++;}
    public override void end_atomic(){if(_phase=="actual"){_atomic--;if(_atomic<0)throw new InvalidOperationException("Unbalanced atomic transaction");if(_atomic==0)Drain();}}
    public override void state_changed(string kind,int seat){if(_phase!="actual")return;_events.Enqueue((kind,seat));if(_atomic==0)Drain();}
    protected virtual void Death()
    {
        foreach(var s in players){if(s.hp>0)continue;_cut.Add(s.seat);if(s.rewind.Length>0&&!s.revived&&time<=(long)s.rewind[0]+200){s.revived=true;s.attack=s.rewind[1];s.defense=s.rewind[2];s.hp=(int)s.rewind[3];s.shield=(int)s.rewind[4];s.fx["temp_attack"]=s.fx["abyss"]=s.fx["permanent_def_down"]=0;s.temporary=new();var budget=_old_left;_old_left=null;try{base.CleanseCore(s,s.poison);}finally{_old_left=budget;}_events.Enqueue(("hp",s.seat));_events.Enqueue(("shield",s.seat));@event("revive",s.seat);}}
    }
    protected void Drain()
    {
        if(_draining)return;_draining=true;try{Death();int guard=0;while(_events.Count>0){if(++guard>10000)throw new InvalidOperationException("Nonterminating execute reaction");var owners=new HashSet<int>();while(_events.Count>0){var(kind,seat)=_events.Dequeue();owners.Add(kind=="hp"?1-seat:seat);}var packets=new List<Packet>();var committed=new List<int>();foreach(var i in owners.Order()){var s=players[i];var e=players[1-i];if(s.hp<=0||e.hp<=0||!execution_ready(i)||s.shield_total==0)continue;var p=burst(i,s.shield_total);var amount=Math.Max(0,Floor(p.q("raw")*p.q("final")));var(shield,hp)=execution_target(1-i);if(Math.Max(0,amount-shield)<hp)continue;@event("auto_execute",i,("shield",s.shield_total),("target_hp",e.hp));packets.Add(p);committed.Add(i);}
                foreach(var i in committed){var s=players[i];take_shield(s,s.shield_total);s.defense=0;s.fx["defense_zero_position"]=1;}if(packets.Count>0)wave(packets,true);Death();}}
        finally{_draining=false;}
    }
    protected virtual bool execution_ready(int i)=>players[i].has("不周山");
    protected virtual (int shield,int hp) execution_target(int i)=>(players[i].shield_total,players[i].hp);
    protected virtual void slot_begin(List<Fighter> initial){}
    protected virtual void slot_end(){}
    protected override void ShieldGainCore(Fighter s,Q amount,bool temporary,bool no_decay,Q conversion)
    {base.ShieldGainCore(s,amount,temporary,no_decay,conversion);if(_phase=="actual"&&_entries.Count>0&&_wave_depth==0){var loss=base.FlushShieldsCore(s);_decay_credit[s.seat]=_decay_credit.GetValueOrDefault(s.seat)+loss;}}
    public override void wave(List<Packet> packets,bool reaction=false){_wave_depth++;try{base.wave(packets,reaction);}finally{_wave_depth--;}}
    protected override int FlushShieldsCore(Fighter s){var loss=base.FlushShieldsCore(s);_decay_credit.Remove(s.seat,out var credit);return loss+credit;}
    protected override int HealCore(Fighter s,Q amount,bool record,Q bonus)=>_phase=="actual"&&s.hp<=0?0:base.HealCore(s,amount,record,bonus);
    public override void change_poison(Fighter source,Fighter target,int delta)
    {if(_phase=="actual"&&_old_left is not null&&delta<0){var use=Math.Min(-delta,_old_left[target.seat]);_old_left[target.seat]-=use;delta=-use;}base.change_poison(source,target,delta);}
    protected override (int poison,int kinds) CleanseCore(Fighter s,int poison)
    {
        if(_phase!="actual"||_slot is null)return base.CleanseCore(s,poison);var old=_slot[s.seat];var removed=old.mods.Where(m=>NEGATIVE.Contains(m.kind)).Select(m=>m.kind).ToHashSet();s.mods=s.mods.Where(m=>!(old.mods.Any(o=>SameModifier(o,m))&&NEGATIVE.Contains(m.kind))).ToList();s.wind_marks=Math.Max(0,s.wind_marks-old.wind_marks);if(_entries[s.seat].name=="风之躯")poison=old.poison;var confirmed=Math.Min(old.poison,poison);change_poison(players[1-s.seat],s,-confirmed);return(confirmed,removed.Count);
    }
    private static bool SameModifier(Modifier a,Modifier b)=>a.kind==b.kind&&a.value==b.value&&a.owner==b.owner&&a.expires==b.expires&&(a.stamp??Array.Empty<object>()).SequenceEqual(b.stamp??Array.Empty<object>());
    private object? ApplyPrimitive(Frontier frontier)
    {
        var a=frontier.args.Select(value=>value is Fighter s?players[s.seat]:value).ToArray();
        return frontier.primitive switch {
            "draw"=>draw((Fighter)a[0]!, (int)a[1]!),
            "shuffle"=>Void(()=>shuffle((Fighter)a[0]!)),
            "search"=>search((Fighter)a[0]!, (Func<Card,bool>)a[1]!, (int)a[2]!, (int)a[3]!, (bool)a[4]!, (bool)a[5]!, (bool)a[6]!, (bool)a[7]!),
            "move_hand"=>move_hand((Fighter)a[0]!, (int)a[1]!, (string)a[2]!),
            "recover"=>recover((Fighter)a[0]!, (int)a[1]!, (string?)a[2]),
            "choose"=>choose((Fighter)a[0]!, (List<Instance>)a[1]!, (int)a[2]!, (string)a[3]!, (bool)a[4]!),
            "random_insert"=>Void(()=>random_insert((Fighter)a[0]!, (List<Instance>)a[1]!)),
            "heal"=>heal((Fighter)a[0]!, (Q)a[1]!, (bool)a[2]!, (Q)a[3]!),
            "shield_gain"=>Void(()=>shield_gain((Fighter)a[0]!, (Q)a[1]!, (bool)a[2]!, (bool)a[3]!, (Q)a[4]!)),
            "flush_shields"=>flush_shields((Fighter)a[0]!),
            "take_shield"=>take_shield((Fighter)a[0]!, (int)a[1]!, (bool)a[2]!),
            "lose_life"=>lose_life((Fighter)a[0]!, (int)a[1]!, (bool)a[2]!),
            "add_ember"=>Void(()=>add_ember((Fighter)a[0]!, (int)a[1]!)),
            "add_tide"=>Void(()=>add_tide((Fighter)a[0]!, (Q)a[1]!)),
            "consume_tide"=>consume_tide((Fighter)a[0]!, (int)a[1]!),
            "temp"=>Void(()=>temp((int)a[0]!, (string)a[1]!, (Q)a[2]!, (int?)a[3])),
            "cleanse"=>cleanse((Fighter)a[0]!, (int)a[1]!),
            "target_poison"=>Void(()=>target_poison((int)a[0]!, (int)a[1]!)),
            "poison_packets"=>poison_packets((int)a[0]!),
            _=>throw new InvalidOperationException("Unknown primitive "+frontier.primitive)};
    }
    private static object? Void(Action run){run();return null;}
    protected void SlotStep(Dictionary<int,Entry> entries)
    {
        _entries=entries;_cursors=entries.Keys.ToDictionary(i=>i,_=>0);var initial=ClonePlayers(players);_slot=initial;slot_begin(initial);_old_left=initial.Select(s=>s.poison).ToArray();var caches=entries.Keys.ToDictionary(i=>i,_=>new List<ProbeCache>());var done=new HashSet<int>();var packets=new List<Packet>();
        while(done.Count<entries.Count)
        {
            var probes=new Dictionary<int,(LockedBattle probe,Frontier? frontier)>();foreach(var(i,entry) in entries){if(done.Contains(i))continue;var probe=(LockedBattle)CloneShell();probe.players=ClonePlayers(initial);probe._phase="probe";probe._actor=i;probe._cache=caches[i];probe._probe_cursor=0;probe.operations=new();probe.log=new();probe.trace=false;probe.executed=new();probe._cursors=new(){{i,0}};probe._depth=0;Frontier? frontier=null;try{probe.play_local(i,entry.uid);}catch(Frontier stop){frontier=stop;}probes[i]=(probe,frontier);}
            begin_atomic();foreach(var(i,row) in probes){var newly=row.probe.players[i].has("不周山")&&!players[i].has("不周山");players[i]=row.probe.players[i];if(newly)state_changed("shield",i);}packets.AddRange(CommitOperations(probes.Values.SelectMany(row=>row.probe.operations).ToList()));
            var pending=new List<(int seat,string name,object? result)>();foreach(var(i,row) in probes){_cursors[i]=row.probe._cursors.GetValueOrDefault(i);if(row.frontier is null){done.Add(i);var entry=entries[i];var s=players[i];restore_private(s,entry.after);s.energy=entry.energy;s.spent=entry.spent;if(_cursors[i]!=entry.receipts.Count)throw new InvalidOperationException(entry.name+": unconsumed receipt");executed[entry.name]++;executed_by_seat[i][entry.name]++;flow_by_seat[i]["instant_fee"]+=Math.Max(0,entry.energy-(initial[i].energy-entry.cost));flow_by_seat[i]["played"]++;@event("play",i,("name",entry.name),("uid",entry.uid),("cost",entry.cost),("energy",s.energy));}
                else{var result=ApplyPrimitive(row.frontier);pending.Add((i,row.frontier.primitive,CloneResult(result)));}}
            var ops=operations;operations=new();packets.AddRange(CommitOperations(ops));end_atomic();foreach(var(i,name,result) in pending)caches[i].Add(new(name,result,players[i].Clone(),new(_cursors)));
        }
        _entries=new();_cursors=new();if(packets.Count>0)wave(packets);slot_end();_slot=null;_old_left=null;finish_transaction();
    }
    public override int resolve_sequences(List<List<Entry>> sequences)
    {
        _cut=new();int played=0;@event("sequences_locked",0,("sequences",sequences.Select(seq=>seq.Select(p=>p.name).ToArray()).ToArray()));for(int position=0;position<sequences.Max(seq=>seq.Count);position++){var entries=new Dictionary<int,Entry>();for(int i=0;i<sequences.Count;i++)if(position<sequences[i].Count&&!_cut.Contains(i)&&players[i].hp>0)entries[i]=sequences[i][position];if(entries.Count==0)break;SlotStep(entries);played+=entries.Count;if(players.Any(s=>s.hp<=0))break;}return played;
    }
    public override void finish_transaction(){if(_phase!="actual")return;Drain();foreach(var s in players){flush_shields(s);s.assert_instances();}}
}
