using System.Text.Json;
namespace Magicians.Native;

public partial class Battle
{
    public virtual void card_step(Dictionary<int,string> actions)
    {
        var snapshot=ClonePlayers(players);var projections=new Dictionary<int,Fighter>();var byActor=new Dictionary<int,List<Operation>>();var logs=new Dictionary<int,List<Dictionary<string,object?>>>();var prefix=log;var savedExecuted=executed;
        foreach(var (i,uid) in actions.OrderBy(x=>x.Key)){players=ClonePlayers(snapshot);operations=new();log=new();executed=new();projecting=true;decision_cursors[i]=0;play_local(i,uid);projections[i]=players[i];byActor[i]=operations;logs[i]=log;}
        var allOps=actions.Keys.Order().SelectMany(i=>byActor[i]).ToList();log=prefix.Concat(actions.Keys.Order().SelectMany(i=>logs[i])).ToList();executed=savedExecuted;
        foreach(var (i,uid) in actions){var name=snapshot[i].hand.First(x=>x.uid==uid).name;executed[name]++;executed_by_seat[i][name]++;}
        players=snapshot;projecting=false;foreach(var (i,p) in projections)players[i]=p;operations=new();var packets=CommitOperations(allOps,true);if(packets.Count>0)wave(packets);finish_transaction();
    }
    protected List<Packet> CommitOperations(List<Operation> ops,bool legacy=false)
    {
        var packets=new List<Packet>();foreach(var op in ops){var s=players[op.target];switch(op.kind){
            case "modifier":s.mods.Add((Modifier)op.value);break;
            case "poison":var (source,delta)=((int,int))op.value;change_poison(players[source],s,delta);break;
            case "poison_growth" when legacy:if(s.has("毒爆"))s.attack+=3*Convert.ToInt32(op.value);break;
            case "decay_reschedule":var src=players[Convert.ToInt32(op.value)];s.poison_due=Math.Max(time+1,s.poison_start+poison_interval(src,s.poison));break;
            case "def_down_permanent":s.fx["permanent_def_down"]+=(Q)op.value;break;
            case "packet":packets.Add((Packet)op.value);break;
            default:throw new ArgumentException("Unknown operation: "+op.kind);}}
        return packets;
    }
    public virtual Packet packet(int i,Q raw,string kind="missile",Packet? extra=null)
    {
        var s=players[i];var p=extra is null?new Packet():new Packet();if(extra is not null)foreach(var row in extra)p[row.Key]=row.Value;
        if(projecting&&!p.ContainsKey("defense"))p["defense"]=stats(1-i).defense;
        if(kind is "poison" or "explosion"&&!p.ContainsKey("poison_suppressed"))p["poison_suppressed"]=players[1-i].guarded;
        p["source"]=i;p["raw"]=raw;p["kind"]=kind;p["final"]=fire_factor(s);return p;
    }
    public virtual List<Packet> poison_packets(int i)=>Primitive("poison_packets",new object?[]{i},()=>{
        var s=players[i];var e=players[1-i];if(e.poison==0||s.layers["poison"]==0||e.guarded)return new List<Packet>();
        var raw=stats(i).attack*s.layers["poison"]*e.poison*new Q(s.specialization=="poison"?4:2,100);
        if(s.has("蚀命")){var hp=Math.Max(0,(slot_snapshot??players)[1-i].hp);raw+=new Q(hp*s.layers["poison"]*e.poison,100);}
        var repeat=(int)s.fx["poison_repeat"];s.fx["poison_repeat"]=0;return Enumerable.Range(0,1+repeat).Select(_=>packet(i,raw,"poison")).ToList();});
    public virtual Packet burst(int i,int shield,Q? multiplier=null)
    {var s=players[i];var raw=Max(0,shield*s.layers["earth"]*(s.has("反震岩核")?new Q(35,1000):new Q(25,1000))-stats(1-i).defense/2);return packet(i,raw*(multiplier??new Q(1)),"burst",new(){{"defense_done",true}});}
    public virtual void wave(List<Packet> packets,bool reaction=false)
    {
        var snap=damage_snapshot();var results=new List<(Packet p,int amount,int absorbed,int life,int actual,int oldShield)>();
        foreach(var p in packets){var i=p.n("source");var s=snap[i];var e=snap[1-i];var defense=p.q("defense",stats(1-i).defense);var raw=p.q("raw");var kind=p.str("kind");var ordinary=kind=="missile";
            if(ordinary){defense*=Max(0,1-p.q("def_pen"));defense*=1-p.q("ignore");raw-=defense;}
            else if(kind is "poison" or "explosion")raw-=defense/2;else if(kind=="water")raw-=defense;
            Q reduction=ordinary&&e.has("壁垒之心")&&e.shield_total!=0?15:0;if(ordinary&&p.b("main")&&e.has("血潮"))reduction+=new Q(30*Math.Max(0,e.hp),e.max_hp);
            var flat=ordinary?e.fx["next_flat"]:new Q(0);var amount=Floor(Max(0,raw-flat)*p.q("final")*Max(0,1-reduction/100));amount=Math.Max(kind=="burst"?0:1,amount);
            if(kind is "poison" or "explosion"&&p.b("poison_suppressed",e.guarded))amount=0;
            var pen=kind is "true" or "poison" or "explosion" or "fatigue"?new Q(1):p.q("pen");var direct=amount*pen;var covered=amount-direct;var shieldFactor=p.q("shield_factor",1);var absorbed=Math.Min(e.shield_total,Floor(covered*shieldFactor));var overflow=Max(0,covered-absorbed/shieldFactor);var life=Floor(direct+overflow);var actual=Math.Min(Math.Max(0,e.hp),life);results.Add((p,amount,absorbed,life,actual,e.shield_total));}
        var reflected=new List<Packet>();var heals=new List<(int seat,Q amount,Q bonus)>();begin_atomic();
        foreach(var (p,amount,absorbed,life,_,oldShield) in results){var i=p.n("source");var s=players[i];var e=players[1-i];take_damage_shield(e,absorbed);var actual=lose_life(e,life);
            if(p.q("active_return")!=0&&e.hp>0)shield_gain(s,p.q("active_return"));var ordinary=p.str("kind")=="missile";var hit=absorbed+actual>0;
            if(ordinary){e.fx["next_flat"]=0;if(e.fx["shield_leech"]!=0){heals.Add((e.seat,absorbed*e.fx["shield_leech"]/100,0));e.fx["shield_leech"]=0;}}
            if(ordinary&&p.b("lifesteal",true)){var rate=s.specialization=="water"?6:3;if(s.layers["water"]!=0)heals.Add((i,new Q(actual*rate*s.layers["water"],100),p.q("record_bonus")));if(p.b("leech"))heals.Add((i,new Q(actual,2),0));}
            if(hit&&p.q("armor_break")!=0)operations.Add(new("def_down_permanent",1-i,p.q("armor_break")));
            if(hit&&ordinary&&s.has("天岚")&&s.has("风痕"))e.wind_marks+=p.n("marks",1);
            if(p.b("main")){s.last_main=absorbed+actual;if(actual!=0&&s.layers["poison"]!=0)change_poison(s,e,1);if(s.has("余烬之核")&&amount>=140)add_ember(s,1);}
            var broken=oldShield>0&&absorbed>=oldShield;if(!reaction&&absorbed!=0&&e.has("不周山")){if(ordinary&&e.has("壁垒之心")){var raw=Max(0,new Q(absorbed*e.layers["earth"]*3,200)-stats(i).defense/2);reflected.Add(packet(1-i,raw,"burst"));}else if(broken)reflected.Add(burst(1-i,oldShield));}
            if(broken){e.fx["broken"]=1;var gain=e.fx["return_flat"]+e.fx["return_once"];e.fx["return_once"]=0;if(e.has("不周山")&&e.has("反震岩核"))gain+=new Q(oldShield*2,5);shield_gain(e,gain);}
            @event("damage",i,("target",1-i),("damage_kind",p.str("kind")),("amount",amount),("shield",absorbed),("life",actual));}
        foreach(var op in operations){if(op.kind!="def_down_permanent")throw new InvalidOperationException("Unexpected damage operation");players[op.target].fx["permanent_def_down"]+=(Q)op.value;}operations=new();end_atomic();
        if(reflected.Count>0)wave(reflected,true);foreach(var (seat,amount,bonus) in heals)heal(players[seat],amount,bonus:bonus);foreach(var s in players)flush_shields(s);
    }
    public virtual void finish_transaction()
    {
        auto_burst();foreach(var s in players)if(s.hp<=0&&s.rewind.Length>0&&!s.revived&&time<=(long)s.rewind[0]+200){s.revived=true;s.attack=s.rewind[1];s.defense=s.rewind[2];s.hp=(int)s.rewind[3];s.shield=(int)s.rewind[4];s.fx["temp_attack"]=0;s.fx["abyss"]=0;s.fx["permanent_def_down"]=0;s.temporary=new();cleanse(s,s.poison);@event("revive",s.seat);}foreach(var s in players){flush_shields(s);s.assert_instances();}
    }
    public void auto_burst()
    {
        var packets=new List<Packet>();var committed=new List<int>();for(int i=0;i<players.Count;i++){var s=players[i];var e=players[1-i];if(s.hp<=0||e.hp<=0||!s.has("不周山")||s.shield_total==0)continue;var p=burst(i,s.shield_total);var amount=Math.Max(0,Floor(p.q("raw")*p.q("final")));if(Math.Max(0,amount-e.shield_total)>=e.hp){packets.Add(p);committed.Add(i);}}
        foreach(var i in committed){var s=players[i];take_shield(s,s.shield_total);s.defense=0;}if(packets.Count>0)wave(packets,true);
    }
    public virtual void begin_stage()
    {
        foreach(var s in players)if(s.pending["poison"]!=0){change_poison(s,players[1-s.seat],(int)s.pending["poison"]);s.pending["poison"]=0;}
        var packets=new List<Packet>();if(time!=0)for(int i=0;i<2;i++)packets.AddRange(poison_packets(i));if(packets.Count>0){wave(packets);finish_transaction();if(players.Any(s=>s.hp<=0))return;}decay();finish_transaction();if(players.Any(s=>s.hp<=0))return;
        foreach(var s in players){var i=s.seat;s.stage++;foreach(var target in players)target.mods=target.mods.Where(m=>!(m.owner==i&&m.expires<=s.stage)).ToList();var oldShield=s.shield_total;var converted=s.temporary.Where(b=>b[1]<=s.stage).Aggregate(new Q(0),(sum,b)=>sum+b[0]*b[2]);s.temporary=s.temporary.Where(b=>b[1]>s.stage).ToList();if(s.shield_total!=oldShield)state_changed("shield",i);shield_gain(s,converted);
            foreach(var key in new[]{"lifeguard","eternal","earth_ability_discount","shuffled","ash_reward","trickle","springs","tactic_used","tailwind_used"})s.fx[key]=0;
            s.drawn.Clear();s.discounts.Clear();s.counts.Clear();s.spent=s.played=0;foreach(var e in Catalog.ELEMENTS)s.fx["discount_"+e]=0;foreach(var amount in s.pending.heal_batches)heal(s,amount);s.fx["earth_ability_discount"]=s.pending["earth_discount"];s.energy=Math.Max(0,3+(int)s.pending["energy"]);var count=Math.Max(0,5+(int)s.pending["draw"]);s.pending.Clear();s.pending.heal_batches.Clear();flush_shields(s);draw(s,count);}
    }
    public virtual void decay()
    {
        var changes=new List<(int seat,bool guarded)>();foreach(var target in players){var i=target.seat;var source=players[1-i];if(target.poison_due>time||target.poison==0)continue;if(target.poison==1&&source.has("蚀骨")){target.poison_due=INF;continue;}changes.Add((i,source.fx["poison_guards"]!=0));}
        foreach(var (i,guarded) in changes){var target=players[i];var source=players[1-i];if(guarded){source.fx["poison_guards"]--;change_poison(source,target,1);}else change_poison(source,target,-1);}
    }
    public virtual void end_stage()
    {
        foreach(var s in players){flow_by_seat[s.seat]["unused_energy"]+=s.energy;flow_by_seat[s.seat]["stages"]++;var unused=s.hand.ToList();s.hand=new();random_insert(s,unused);s.discounts.Clear();s.energy=0;if(s.has("守护者壁垒")&&s.played<=2)s.pending["energy"]++;if(s.has("战神徽记")&&s.played>=3)s.pending["draw"]++;if(s.has("炽焰王冠")&&s.stage>s.fx["fire_activation_stage"])add_ember(s,1);foreach(var key in new[]{"trickle","springs"})s.fx[key]=0;foreach(var e in Catalog.ELEMENTS)s.fx["discount_"+e]=0;}
        using var combatRules=JsonDocument.Parse(File.ReadAllText(Path.Combine(Catalog.ROOT,"tools","sim_v12","rules","combat.json")));
        if(time>=combatRules.RootElement.GetProperty("fatigue_start_av").GetInt64()){var amounts=players.Select(s=>Ceil(s.hp*(new Q(5,100)+new Q(time,10000)))).ToArray();for(int i=0;i<2;i++)lose_life(players[i],amounts[i]);foreach(var s in players)flush_shields(s);}finish_transaction();
    }
    public Packet missile_parameters(int i,bool main=false)
    {
        var s=players[i];Q defensePen=0;if(s.has("穿云箭"))defensePen+=5+2*s.layers["wind"];var shieldPen=s.has("天岚")?new Q(5+2*s.layers["wind"],100):new Q(0);if(s.has("生命之泉"))shieldPen+=new Q(1,5);if(main){defensePen+=s.fx["main_def_pen"];shieldPen+=s.fx["main_shield_pen"]/100;}
        return new(){{"def_pen",Min(1,defensePen/100)},{"pen",Min(1,shieldPen)},{"ignore",s.has("穿云箭")&&s.has("天岚")?new Q(15,100):new Q(0)},{"shield_factor",1+s.fx["melt_shield"]/10+(s.has("穿云箭")?new Q(20,100):new Q(0))}};
    }
    public virtual void fire(List<int> due)
    {
        var groups=new[]{new List<Packet>(),new List<Packet>()};foreach(var i in due){var s=players[i];var(a,d)=stats(i);if(s.layers["earth"]!=0){var rate=s.specialization=="earth"?4:2;shield_gain(s,Ceil((a+d)*s.layers["earth"]*new Q(rate,100))+s.fx["earth_extra"]);}flush_shields(s);}
        foreach(var i in due){var s=players[i];var a=stats(i).attack;var extra=s.fx["direct"];if(s.has("血潮"))extra+=pay_life(s,new Q(10,100),false);var ember=consume_ember(s,s.ember);var tide=consume_tide(s,s.tide);extra+=tide*(1+s.fx["tide_bonus"]/100);extra+=s.fx["grip_loss"]*new Q(s.layers["earth"]*4,100)*(1+s.fx["grip_bonus"]/100);extra+=a*ember*s.fx["hot_core"]/100;if(s.has("疾风之刃")&&s.last_fire>=0)extra+=s.last_main*Max(0,1-new Q(time-s.last_fire,50))*new Q(1,5);
            if(tide!=0&&s.fx["tide_shield"]!=0)shield_gain(s,tide*s.fx["tide_shield"]/100);if(s.has("潮汐回响"))add_tide(s,new Q(tide,4));if(s.fx["pierce_tide"]!=0)s.fx["main_def_pen"]+=s.fx["pierce_tide"]*(tide>=50?70:40);flush_shields(s);
            if(s.strips.Count>0){var e=players[1-i];var strips=s.strips;s.strips=new();var ratio=Min(1,strips.Aggregate(new Q(0),(sum,x)=>sum+x.ratio));var speed=strips.Sum(x=>x.speed);var removed=take_shield(e,Floor(e.shield_total*ratio));if(removed!=0)s.fx["permanent_delay"]-=speed;}
            var parameters=missile_parameters(i,true);parameters["main"]=true;parameters["armor_break"]=s.fx["armor_break"];parameters["record_bonus"]=s.fx["tide_record"];var ratioAttack=s.has("燃烧血统")?new Q(9,5):new Q(1);var main=packet(i,((a+s.fx["main_attack"])*ratioAttack+extra)*(1+s.fx["bonus"]/100),"missile",parameters);main["final"]=main.q("final")*(1+s.layers["fire"]*ember*(s.has("燃烧血统")?new Q(3,100):new Q(25,1000)));groups[i].Add(main);
            foreach(var (fixedDamage,ratio,marks,leech) in s.permanent_subs.Concat(s.subs)){var p=missile_parameters(i);p["marks"]=marks;p["armor_break"]=s.fx["armor_break"];p["leech"]=leech;groups[i].Add(packet(i,fixedDamage+a*ratio,"missile",p));}
            if(s.has("天岚")){s.fx["shots"]++;if(s.fx["shots"]%3==0)groups[i].Add(packet(i,a*(s.has("风痕")?new Q(2,5):new Q(1,5)),"true",new(){{"armor_break",s.fx["armor_break"]},{"detonate",true},{"locked_attack",a}}));}s.last_fire=time;}
        for(int k=0;k<groups.Max(g=>g.Count);k++){var packets=groups.Where(g=>k<g.Count).Select(g=>g[k]).ToList();wave(packets);var detonations=new List<Packet>();foreach(var p in packets){var i=p.n("source");var s=players[i];var e=players[1-i];if(p.b("detonate")&&s.has("风痕")&&!e.guarded){var n=e.wind_marks;e.wind_marks=0;if(n!=0)detonations.Add(packet(i,p.q("locked_attack")*(new Q(12*n,100)+new Q(n*(n-1),100)),"true"));}}if(detonations.Count>0)wave(detonations);}
        foreach(var i in due){var s=players[i];s.next_fire=time+gap(s);s.subs.Clear();foreach(var key in new[]{"direct","bonus","delay","main_def_pen","main_shield_pen","temp_attack","main_attack","grip_loss","grip_bonus","hot_core","tide_bonus","tide_record","pierce_tide","tide_shield","armor_break"})s.fx[key]=0;}finish_transaction();
    }
    public virtual Dictionary<string,object?> run(long max_time=10000,int max_actions=10000)
    {
        int actions=0;while(true){if(time%100==0){begin_stage();if(players.Any(s=>s.hp<=0))break;
            if(sequence_mode is "private_positions_experimental" or "locked"){var sequences=plan_stage(max_actions);planned_count+=sequences.Sum(s=>s.Count);if(planned_count>max_actions)throw new InvalidOperationException("Nonterminating planned card sequence; no fabricated result");actions+=resolve_sequences(sequences);}
            else{while(true){var picks=new Dictionary<int,string>();for(int i=0;i<2;i++){var x=choose_play(i);if(x is not null)picks[i]=x.uid;}if(picks.Count==0)break;actions+=picks.Count;if(actions>max_actions)throw new InvalidOperationException("Nonterminating card sequence; no fabricated result");card_step(picks);if(players.Any(s=>s.hp<=0))break;}}end_stage();}
            else{decay();finish_transaction();}if(players.Any(s=>s.hp<=0))break;var due=players.Where(s=>s.next_fire==time).Select(s=>s.seat).ToList();if(due.Count>0)fire(due);if(players.Any(s=>s.hp<=0))break;var next=new[]{(time/100+1)*100,players[0].next_fire,players[1].next_fire,players[0].poison_due,players[1].poison_due}.Min();if(next<=time||next>max_time)throw new InvalidOperationException($"Nonterminating scheduler; no fabricated winner {time} {next}");time=next;}
        return new(){{"version","1.2"},{"winner",players.All(s=>s.hp<=0)?null:players[0].hp<=0?1:0},{"combat_rule",snapshot_rule},{"sequence_rule",sequence_mode},{"time",time},{"hp",players.Select(s=>s.hp).ToArray()},{"actions",actions},{"planned_actions",planned_count},{"sequence_skips",sequence_skips.ToDictionary(x=>x.Key,x=>exact(x.Value))},{"executed_by_seat",executed_by_seat.Select(c=>c.ToDictionary(x=>x.Key,x=>exact(x.Value))).ToArray()},{"flow_by_seat",flow_by_seat.Select(c=>c.ToDictionary(x=>x.Key,x=>exact(x.Value))).ToArray()},{"cycle_decisions",cycle_decisions.Select(c=>c.ToDictionary(x=>x.Key,x=>exact(x.Value))).ToArray()},{"executed",executed.ToDictionary(x=>x.Key,x=>exact(x.Value))},{"log",trace?log:new()}};
    }
    public virtual List<List<Entry>> plan_stage(int max_actions=10000)
    {
        var sequences=new List<List<Entry>>();for(int i=0;i<2;i++)
        {
            var shadow=CloneShell();shadow.players=ClonePlayers(players);var enemy=shadow.players[1-i];enemy.deck=Array.Empty<string>();enemy.instances=Array.Empty<Instance>();enemy.pile=new();enemy.hand=new();enemy.discard=new();enemy.removed=new();enemy.resolving=new();enemy.discounts=new();enemy.drawn=new();enemy.seen=new();enemy.rng=new PythonRandom(0);shadow.log=new();shadow.trace=false;shadow.executed=new();shadow.executed_by_seat=new[]{new NumMap(),new NumMap()};shadow.operations=new();shadow.replay_decisions=null;shadow.sequence_skips=new();shadow.decision_cursors=new();
            var sequence=new List<Entry>();while(true){var inst=shadow.choose_play(i);if(inst is null)break;if(sequence.Count>=max_actions)throw new InvalidOperationException("Private planning did not terminate");var fee=shadow.cost(shadow.players[i],inst);shadow.choice_records=new(){{i,new()}};shadow.card_step(new(){{i,inst.uid}});sequence.Add(new Entry(inst.uid,inst.name,fee){choices=shadow.choice_records[i].ToList()});if(shadow.players.Any(s=>s.hp<=0))break;}sequences.Add(sequence);
        }
        return sequences;
    }
    public virtual int resolve_sequences(List<List<Entry>> sequences)
    {
        @event("sequences_locked",0,("sequences",sequences.Select(seq=>seq.Select(p=>p.name).ToArray()).ToArray()));int played=0;for(int position=0;position<sequences.Max(seq=>seq.Count);position++)
        {
            var actions=new Dictionary<int,string>();var decisions=new Dictionary<int,List<(string purpose,string[] uids)>>();for(int i=0;i<sequences.Count;i++)
            {var sequence=sequences[i];if(position>=sequence.Count)continue;var entry=sequence[position];var s=players[i];var inst=s.hand.FirstOrDefault(x=>x.uid==entry.uid);var reason=inst is null?"missing_instance":cost(s,inst)>s.energy?"insufficient_energy":null;if(reason is not null){sequence_skips[reason]++;@event("sequence_skip",i,("position",position),("name",entry.name),("reason",reason));continue;}actions[i]=entry.uid;decisions[i]=entry.choices;}
            if(actions.Count>0){replay_decisions=decisions;try{card_step(actions);}finally{replay_decisions=null;}played+=actions.Count;}if(players.Any(s=>s.hp<=0))break;
        }
        return played;
    }
}
