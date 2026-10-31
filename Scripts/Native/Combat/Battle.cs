using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Magicians.Native;

public sealed class Packet : Dictionary<string,object?>
{
    public Q q(string key,Q? fallback=null) => TryGetValue(key,out var v) ? v switch { Q value=>value,int value=>new Q(value),long value=>new Q(value),_=>throw new InvalidDataException(key) } : fallback??new Q(0);
    public int n(string key,int fallback=0)=>ContainsKey(key)?(int)q(key):fallback;
    public bool b(string key,bool fallback=false)=>TryGetValue(key,out var v)?v is bool flag?flag:q(key)!=0:fallback;
    public string str(string key)=> (string)this[key]!;
}
public sealed record Operation(string kind,int target,object value);
public sealed record Before(int hp,int shield,int total,Q defense,int spent,int played,int fire,int wind,bool first);
public sealed record PrivateState(Fighter state);
public sealed record Receipt(string name,object? result,PrivateState state,Q shuffles);
public sealed class Entry
{
    public string uid,name;public int cost,energy,spent;
    public List<Receipt> receipts=new(); public Dictionary<string,object?> flags=new();
    public PrivateState after=null!;
    public List<(string purpose,string[] uids)> choices=new();
    public Entry(string uid,string name,int cost){this.uid=uid;this.name=name;this.cost=cost;}
}

/// <summary>Direct native port of the reviewed v1.2 exact transaction simulator.</summary>
public partial class Battle
{
    public const long INF=1000000000000L;
    public static readonly HashSet<string> NEGATIVE=new(){"atk_down","def_down","fear","heal_block","heal_block_max"};
    public List<Fighter> players=new();public long time;public bool trace,projecting;
    public List<Dictionary<string,object?>> log=new();public List<Operation> operations=new();
    public NumMap executed=new(),sequence_skips=new();
    public NumMap[] executed_by_seat={new(),new()},flow_by_seat={new(),new()},cycle_decisions={new(),new()};
    public string[] styles;public string sequence_mode;public int planned_count;
    public string _phase="actual";public Dictionary<string,CycleRecord> _economic_visits=new();
    public JsonElement?[]? policy_configs;
    public Func<Dictionary<string,object?>,List<Dictionary<string,object?>>,int,string,IEnumerable<string>>? chooser;
    public Dictionary<int,List<(string purpose,string[] uids)>>? choice_records,replay_decisions;
    public Dictionary<int,int> decision_cursors=new();
    public virtual List<Fighter>? slot_snapshot=>null;
    public virtual string snapshot_rule=>"legacy_position_projection";
    protected static int I(Q q)=>(int)q;
    protected static int Ceil(Q q)=>(int)q.Ceiling();
    protected static int Floor(Q q)=>(int)q.Floor();
    protected static Q Min(Q a,Q b)=>Q.Min(a,b);
    protected static Q Max(Q a,Q b)=>Q.Max(a,b);
    protected static Q Qn(Q a,Q b)=>a/b;
    public Battle(IEnumerable<IEnumerable<string>> decks,IEnumerable<string>? specializations=null,BigInteger? seed=null,bool trace=false,
        Func<Dictionary<string,object?>,List<Dictionary<string,object?>>,int,string,IEnumerable<string>>? chooser=null,
        string[]? styles=null,string sequence_mode="live")
    {
        this.trace=trace;this.chooser=chooser;this.styles=styles??new[]{"balanced","balanced"};this.sequence_mode=sequence_mode;
        if(!new[]{"live","private_positions_experimental","locked"}.Contains(sequence_mode))throw new ArgumentException("Unknown sequence rule");
        var all=decks.Select(d=>d.ToArray()).ToArray();var specs=(specializations??new[]{"",""}).ToArray();
        if(all.Length!=2)throw new ArgumentException("Expected two decks");
        for(int i=0;i<2;i++)
        {
            if(all[i].Any(n=>!Catalog.CARDS.ContainsKey(n)))throw new ArgumentException("Unknown v1.2 card");
            var salt=PyCompat.BattleSeed(seed??BigInteger.One,all[i]);
            players.Add(new Fighter(i,all[i],specs[i],salt));
        }
    }
    private static string PyString(string text)
    {
        var quote=text.Contains('\'')&&!text.Contains('"')?'"':'\'';
        return quote+text.Replace("\\","\\\\").Replace(quote.ToString(),"\\"+quote).Replace("\n","\\n").Replace("\r","\\r").Replace("\t","\\t")+quote;
    }
    public virtual Battle CloneShell()=> (Battle)MemberwiseClone();
    protected static List<Fighter> ClonePlayers(IEnumerable<Fighter> source)=>source.Select(s=>s.Clone()).ToList();
    public virtual T plan_read<T>(int i,string key,T value)=>value;
    public virtual Instance? choose_play(int seat)=>Policy.choose_play(this,seat);
    public virtual void card_panel(Fighter s,Card c,bool first){s.attack+=c.attack;s.defense+=c.defense;if(first&&c.element!="generic")s.layers[c.element]+=c.layers;}
    public virtual void finish_card_panel(Fighter s,Card c,bool first){}
    public virtual List<Fighter> damage_snapshot()=>ClonePlayers(players);
    public virtual int take_damage_shield(Fighter s,int amount)=>take_shield(s,amount);
    public virtual void state_changed(string kind,int seat){}
    public virtual void begin_atomic(){}
    public virtual void end_atomic(){}
    public static object? exact(object? value)=>value switch {
        Q q=>q.denominator==1?(object)(long)q:q.ToString(),
        Dictionary<string,object?> d=>d.ToDictionary(x=>x.Key,x=>exact(x.Value)),
        IEnumerable<object?> seq when value is not string=>seq.Select(exact).ToArray(),_=>value};
    public void @event(string kind,int seat,params (string key,object? value)[] data)
    {
        if(!trace)return;var row=new Dictionary<string,object?>{{"time",time},{"kind",kind},{"seat",seat}};
        foreach(var (key,value) in data)row.Add(key,exact(value));log.Add(row);
    }
    public virtual (Q attack,Q defense) stats(int i)
    {
        var s=players[i];var enemy=players[1-i];var a=s.attack+s.fx["temp_attack"]+3*s.fx["abyss"]*(s.spent_tide/50);var d=s.defense;
        if(!s.guarded){a-=s.value("atk_down");d-=s.value("def_down")+s.fx["permanent_def_down"];
            if(s.poison!=0&&enemy.has("万毒之源")){var ac=enemy.has("蚀骨")?new Q(6,5):new Q(3,5);var dc=enemy.has("蚀骨")?new Q(9,10):new Q(1,2);a-=Floor(5+ac*enemy.layers["poison"]*s.poison);d-=Floor(5+dc*enemy.layers["poison"]*s.poison);}
            var factor=new Q(4,5).Pow((int)s.value("fear"));a*=factor;d*=factor;}
        return(Max(0,a),Max(0,d));
    }
    public virtual Q fire_factor(Fighter s)=>1+new Q(s.layers["fire"]*(s.specialization=="fire"?30:15),1000);
    public virtual int gap(Fighter s)=>base_gap(s);
    public int base_gap(Fighter s)
    {
        Q fixedGap=50+s.fx["permanent_delay"]+s.fx["delay"];
        if(s.has("生命之泉"))fixedGap-=5+s.layers["water"];
        fixedGap-=s.layers["wind"]*(s.specialization=="wind"?new Q(1):new Q(1,2));
        if(s.has("疾风之刃"))fixedGap-=4+new Q(s.layers["wind"],2);
        var factor=Max(0,1+(s.has("燃烧血统")?1:0)-s.fx["flow"]/20);
        return Math.Max(1,Floor(fixedGap*factor));
    }
    protected virtual object? Primitive(string name,object?[] args,Func<object?> run)=>run();
    protected T Primitive<T>(string name,object?[] args,Func<T> run)=>(T)Primitive(name,args,()=> (object?)run())!;
    public virtual void temp(int i,string kind,Q value,int? target=null)=>Primitive<object?>("temp",new object?[]{i,kind,value,target},()=>{TempCore(i,kind,value,target);return null;});
    protected virtual void TempCore(int i,string kind,Q value,int? target)
    {var s=players[i];var mod=new Modifier(kind,value,i,s.stage+1,new object[]{i,time,s.stage,s.played,kind});if(target is null||target==i)s.mods.Add(mod);else operations.Add(new("modifier",target.Value,mod));}
    public virtual void add_ember(Fighter s,int n)=>Primitive<object?>("add_ember",new object?[]{s,n},()=>{if(s.has("炽焰王冠"))s.ember=Math.Min(5,s.ember+n);return null;});
    public int consume_ember(Fighter s,int n)
    {
        n=s.has("炽焰王冠")?Math.Min(n,s.ember):0;s.ember-=n;
        for(int k=0;k<n;k++){s.spent_ember++;if(s.has("余烬之核"))heal(s,new Q(s.max_hp-s.hp,10));if(s.has("灰烬循环")&&s.fx["ash_reward"]<3){s.pending["draw"]++;s.pending["energy"]++;s.fx["ash_reward"]++;}}
        return n;
    }
    public virtual void add_tide(Fighter s,Q n)=>Primitive<object?>("add_tide",new object?[]{s,n},()=>{if(s.has("潮汐之心"))s.tide+=Math.Max(0,Ceil(n));return null;});
    public virtual int consume_tide(Fighter s,int n)=>Primitive("consume_tide",new object?[]{s,n},()=>{var amount=s.has("潮汐之心")?Math.Min(s.tide,n):0;s.tide-=amount;s.spent_tide+=amount;return amount;});
    public virtual int heal(Fighter s,Q amount,bool record=true,Q bonus=default)=>Primitive("heal",new object?[]{s,amount,record,bonus},()=>HealCore(s,amount,record,bonus));
    protected virtual int HealCore(Fighter s,Q amount,bool record,Q bonus)
    {
        var highest=s.mods.Where(m=>m.kind=="heal_block_max").Select(m=>m.value).DefaultIfEmpty(0).Max();
        var block=s.guarded?new Q(0):Min(60,s.value("heal_block")+highest);
        var actual=Math.Min(Math.Max(0,Ceil(amount*(1-block/100))),Math.Max(0,s.max_hp-s.hp));s.hp+=actual;
        if(actual!=0){if(record&&s.has("潮汐之心")){var rate=s.has("潮汐回响")?10:6;add_tide(s,new Q(actual*rate*s.layers["water"],100)*(1+bonus/100));}
            if(s.has("潮汐之心")&&s.fx["still_water"]!=0){add_tide(s,s.fx["still_water"]*s.layers["water"]*6);s.fx["still_water"]=0;}}
        @event("heal",s.seat,("amount",actual));if(actual!=0)state_changed("hp",s.seat);return actual;
    }
    public virtual void shield_gain(Fighter s,Q amount,bool temporary=false,bool no_decay=false,Q conversion=default)=>Primitive<object?>("shield_gain",new object?[]{s,amount,temporary,no_decay,conversion},()=>{ShieldGainCore(s,amount,temporary,no_decay,conversion);return null;});
    protected void ShieldGainRaw(Fighter s,Q amount,bool temporary,bool no_decay,Q conversion){if(amount>0)s.shield_events.Add((amount,temporary,no_decay,conversion));}
    protected virtual void ShieldGainCore(Fighter s,Q amount,bool temporary,bool no_decay,Q conversion)=>ShieldGainRaw(s,amount,temporary,no_decay,conversion);
    public virtual int flush_shields(Fighter s)=>Primitive("flush_shields",new object?[]{s},()=>FlushShieldsCore(s));
    protected virtual int FlushShieldsCore(Fighter s)=>FlushShieldsRaw(s);
    protected int FlushShieldsRaw(Fighter s)
    {
        var events=s.shield_events;s.shield_events=new();if(events.Count==0)return 0;
        var ordinary=events.Where(x=>!x.temporary).ToList();var decay=ordinary.Any(x=>!x.no_decay);
        var loss=decay?s.shield-Ceil(new Q(s.shield*3,4)):0;
        if(loss!=0){s.shield-=loss;if(s.has("大地之握"))s.fx["grip_loss"]+=loss;}
        var amount=ordinary.Aggregate(new Q(0),(a,x)=>a+x.amount);
        if(ordinary.Count>0&&s.fx["shield_pulse"]!=0){amount+=40;s.fx["shield_pulse"]--;}
        var multiplier=1+s.fx["shield_structure"]*new Q(8,100);
        if(ordinary.Count>0)s.shield+=Ceil(amount*multiplier);
        foreach(var x in events)if(x.temporary)s.temporary.Add(new[]{new Q(Ceil(x.amount*multiplier)),new Q(s.stage+1),x.conversion});
        @event("shield",s.seat,("ordinary",s.shield),("temporary",s.temporary.Sum(b=>(int)b[0])),("decay",loss));state_changed("shield",s.seat);return loss;
    }
    public virtual int take_shield(Fighter s,int n,bool ordinary_only=false)=>Primitive("take_shield",new object?[]{s,n,ordinary_only},()=>TakeShieldCore(s,n,ordinary_only));
    protected virtual int TakeShieldCore(Fighter s,int n,bool ordinary_only)
    {n=Math.Min(n,ordinary_only?s.shield:s.shield_total);var left=n;if(!ordinary_only)foreach(var batch in s.temporary){var use=Math.Min(left,(int)batch[0]);batch[0]-=use;left-=use;}s.shield-=left;if(n!=0)state_changed("shield",s.seat);return n;}
    public virtual int lose_life(Fighter s,int amount,bool payment=false)=>Primitive("lose_life",new object?[]{s,amount,payment},()=>LoseLifeCore(s,amount,payment));
    protected virtual int LoseLifeCore(Fighter s,int amount,bool payment)
    {amount=Math.Max(0,amount);if(!payment){if(s.guard_time==time)amount=Math.Min(amount,Math.Max(0,s.hp-1));else if(amount>=s.hp&&s.hp>0&&s.fx["lifeguard"]!=0&&!s.guard_used){s.guard_used=true;s.guard_time=time;amount=Math.Max(0,s.hp-1);}}
        var actual=Math.Min(Math.Max(0,s.hp),amount);s.hp-=amount;if(actual!=0&&s.fx["eternal"]!=0)shield_gain(s,actual*s.fx["eternal"]);if(amount!=0)state_changed("hp",s.seat);return actual;}
    public int pay_life(Fighter s,Q rate,bool from_card=true){var paid=Math.Max(0,Math.Min(Math.Max(0,s.hp-1),Ceil(s.hp*rate)));lose_life(s,paid,true);if(paid!=0&&from_card&&s.fx["trickle"]!=0){var count=s.fx["trickle"];s.fx["trickle"]=0;heal(s,paid*count/2);}return paid;}
    public long poison_interval(Fighter source,int layers)=>layers<=0||(layers==1&&source.has("蚀骨"))?INF:Math.Max(1,Ceil(Math.Max(1,100/layers)*(1+source.fx["slow_poison"]/5)));
    public virtual void change_poison(Fighter source,Fighter target,int delta)
    {var old=target.poison;target.poison=Math.Max(0,old+delta);if(source.has("毒爆"))source.attack+=3*Math.Abs(target.poison-old);if(target.poison!=old){target.poison_start=time;target.poison_due=time+poison_interval(source,target.poison);}@event("poison",target.seat,("before",old),("after",target.poison));}
    public virtual void target_poison(int i,int delta)=>Primitive<object?>("target_poison",new object?[]{i,delta},()=>{operations.Add(new("poison",1-i,(i,delta)));return null;});
    public virtual (int poison,int kinds) cleanse(Fighter s,int poison=0)=>Primitive("cleanse",new object?[]{s,poison},()=>CleanseCore(s,poison));
    protected virtual (int poison,int kinds) CleanseCore(Fighter s,int poison)
    {var removed=s.mods.Where(m=>NEGATIVE.Contains(m.kind)).Select(m=>m.kind).ToHashSet();s.mods=s.mods.Where(m=>!NEGATIVE.Contains(m.kind)).ToList();s.wind_marks=0;var actual=Math.Min(s.poison,poison);if(actual!=0){change_poison(players[1-s.seat],s,-actual);if(projecting&&players[1-s.seat].has("毒爆"))operations.Add(new("poison_growth",1-s.seat,actual));}return(actual,removed.Count);}
    public double rank_card(Fighter s,Instance inst)
    {var c=Catalog.CARDS[inst.name];var style=Policy.STYLES[styles[s.seat]];return(c.kind=="specialization"&&!s.has(c.name)?100:c.kind=="route_legendary"?65:0)+c.layers*8+c.attack*style["attack"]+c.defense*style["defense"]-c.cost*3;}
    public virtual List<Instance> choose(Fighter s,List<Instance> candidates,int count,string purpose,bool optional=false)=>Primitive("choose",new object?[]{s,candidates,count,purpose,optional},()=>ChooseCore(s,candidates,count,purpose,optional));
    protected virtual List<Instance> ChooseCore(Fighter s,List<Instance> candidates,int count,string purpose,bool optional)
    {
        List<Instance> chosen;
        if(replay_decisions is not null){var script=replay_decisions.GetValueOrDefault(s.seat)??new();var cursor=decision_cursors.GetValueOrDefault(s.seat);while(cursor<script.Count&&script[cursor].purpose!=purpose)cursor++;var ids=cursor<script.Count?script[cursor].uids:Array.Empty<string>();decision_cursors[s.seat]=cursor+1;var legal=candidates.ToDictionary(x=>x.uid);chosen=ids.Where(legal.ContainsKey).Select(uid=>legal[uid]).Take(count).ToList();if(chosen.Count!=ids.Length)sequence_skips["choice_target_unavailable"]+=ids.Length-chosen.Count;return chosen;}
        if(chooser is not null){var visible=candidates.Select(x=>new Dictionary<string,object?>{{"uid",x.uid},{"name",x.name},{"cost",Catalog.CARDS[x.name].cost}}).ToList();var ids=chooser(new(){{"hp",s.hp},{"energy",s.energy},{"specialization",s.specialization},{"optional",optional}},visible,count,purpose);chosen=ids.Select(uid=>candidates.First(x=>x.uid==uid)).ToList();if(chosen.Select(x=>x.uid).Distinct().Count()!=chosen.Count||chosen.Count>Math.Min(count,candidates.Count)||!optional&&chosen.Count!=Math.Min(count,candidates.Count))throw new ArgumentException("Invalid revealed-card choice");}
        else chosen=Policy.choose_cards(this,s,candidates,count,purpose);
        if(choice_records is not null){if(!choice_records.ContainsKey(s.seat))choice_records[s.seat]=new();choice_records[s.seat].Add((purpose,chosen.Select(x=>x.uid).ToArray()));}return chosen;
    }
    public void leave_hand(Fighter s,Instance x){s.hand.Remove(x);s.discounts.Remove(x.uid);}
    public virtual void random_insert(Fighter s,List<Instance> instances)=>Primitive<object?>("random_insert",new object?[]{s,instances},()=>{if(instances.Count>0)s.known_top.Clear();foreach(var x in instances){s.discounts.Remove(x.uid);s.pile.Insert(s.rng.randrange(s.pile.Count+1),x);@event("random_insert",s.seat,("uid",x.uid));}return null;});
    public virtual void shuffle(Fighter s)=>Primitive<object?>("shuffle",new object?[]{s},()=>{s.known_top.Clear();s.pile.AddRange(s.discard);s.discard.Clear();s.rng.shuffle(s.pile);s.fx["shuffled"]++;if(s.has("灰烬循环"))add_ember(s,2);@event("shuffle",s.seat);return null;});
    public virtual List<Instance> draw(Fighter s,int count)=>Primitive("draw",new object?[]{s,count},()=>{
        var drawn=new List<Instance>();for(int k=0;k<Math.Max(0,count);k++){if(s.pile.Count==0&&s.discard.Count>0)shuffle(s);if(s.pile.Count==0)break;var x=s.pile[0];s.pile.RemoveAt(0);if(s.known_top.Count>0&&s.known_top[0]==x.uid)s.known_top.RemoveAt(0);else s.known_top.Clear();s.hand.Add(x);s.drawn.Add(x.uid);drawn.Add(x);@event("draw",s.seat,("uid",x.uid),("name",x.name));}return drawn;});
    public virtual List<Instance> search(Fighter s,Func<Card,bool> predicate,int sample,int count=1,bool instant=false,bool prioritize_unseen=false,bool unseen_only=false,bool optional=false)=>Primitive("search",new object?[]{s,predicate,sample,count,instant,prioritize_unseen,unseen_only,optional},()=>{
        var legal=s.pile.Where(x=>predicate(Catalog.CARDS[x.name])&&(!unseen_only||!s.seen.Contains(x.uid))).ToList();List<Instance> candidates;
        if(prioritize_unseen){var fresh=legal.Where(x=>!s.seen.Contains(x.uid)).ToList();var rest=legal.Where(x=>s.seen.Contains(x.uid)).ToList();candidates=s.rng.sample(fresh,Math.Min(sample,fresh.Count));candidates.AddRange(s.rng.sample(rest,Math.Min(sample-candidates.Count,rest.Count)));}else candidates=s.rng.sample(legal,Math.Min(sample,legal.Count));
        var chosen=choose(s,candidates,count,instant?"draw":"topdeck",optional);foreach(var x in chosen){s.pile.Remove(x);s.known_top.Remove(x.uid);}if(instant){s.hand.AddRange(chosen);foreach(var x in chosen){s.drawn.Add(x.uid);@event("draw",s.seat,("uid",x.uid),("name",x.name),("directed",true));}}else{s.pile.InsertRange(0,chosen);s.known_top.InsertRange(0,chosen.Select(x=>x.uid));}return chosen;});
    public virtual int move_hand(Fighter s,int count,string mode)=>Primitive("move_hand",new object?[]{s,count,mode},()=>{var chosen=choose(s,s.hand,count,mode=="discard"?"discard":"topdeck");foreach(var x in chosen)leave_hand(s,x);if(mode=="discard")s.discard.AddRange(chosen);else if(mode=="topdeck"){s.pile.InsertRange(0,chosen);s.known_top.InsertRange(0,chosen.Select(x=>x.uid));}else if(mode=="insert")random_insert(s,chosen);return chosen.Count;});
    public virtual List<Instance> recover(Fighter s,int count,string? element=null)=>Primitive("recover",new object?[]{s,count,element},()=>{var legal=s.discard.Where(x=>Catalog.CARDS[x.name].enchantment&&(element is null||Catalog.CARDS[x.name].element==element)).ToList();var chosen=choose(s,legal,count,"recover");foreach(var x in chosen){s.discard.Remove(x);s.hand.Add(x);}return chosen;});
    public virtual int cost(Fighter s,Instance inst){var c=Catalog.CARDS[inst.name];Q discount=s.discounts[inst.uid];if(c.enchantment)discount+=s.fx["discount_"+c.element];if(c.element=="earth"&&c.ability)discount+=s.fx["earth_ability_discount"];return Math.Max(0,(int)(c.cost-discount));}
    public void play_local(int i,string uid)
    {
        var s=players[i];var x=s.hand.First(x=>x.uid==uid);var c=Catalog.CARDS[x.name];var fee=cost(s,x);if(fee>s.energy)throw new ArgumentException("Unaffordable card");
        var before=new Before(s.hp,s.shield,s.shield_total,stats(i).defense,s.spent,s.played,s.counts["fire"],s.counts["wind"],!s.seen.Contains(x.uid));s.energy-=fee;s.spent+=fee;leave_hand(s,x);s.resolving.Add(x);
        if(c.enchantment)s.fx["discount_"+c.element]=0;if(c.element=="earth"&&c.ability)s.fx["earth_ability_discount"]=0;card_panel(s,c,before.first);if(before.first)s.seen.Add(x.uid);var springs=(int)s.fx["springs"];
        Effects.Apply(this,i,x,before);
        if(c.name is "炽焰王冠" or "灰烬循环"){var counterpart=c.name=="炽焰王冠"?"灰烬循环":"炽焰王冠";var target=s.pile.FirstOrDefault(a=>a.name==counterpart&&!s.drawn.Contains(a.uid));if(target is not null){s.pile.Remove(target);s.pile.Insert(0,target);s.known_top.Remove(target.uid);s.known_top.Insert(0,target.uid);}}
        if(c.enchantment&&c.element=="water")for(int k=0;k<springs;k++)heal(s,20);finish_card_panel(s,c,before.first);s.played++;if(c.enchantment)s.counts[c.element]++;s.resolving.Remove(x);(c.ability?s.removed:s.discard).Add(x);flush_shields(s);executed[c.name]++;@event("play",i,("name",c.name),("uid",x.uid),("cost",fee),("energy",s.energy));
    }
}
