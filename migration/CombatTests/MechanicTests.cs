using Magicians.Native;
using System.Text.Json;

internal sealed class LimitedBattle:StrictBattle
{
    private int[] used={0,0};private int[] limits={1,0};
    public LimitedBattle(string[][] decks):base(decks,seed:19,trace:true){}
    public override Instance? choose_play(int seat){if(used[seat]>=limits[seat]||players[seat].hand.Count==0)return null;used[seat]++;return players[seat].hand[0];}
    public List<List<Entry>> One(bool both=false){used=new[]{0,0};limits=new[]{1,both?1:0};return plan_stage();}
    public List<List<Entry>> Limited(int a,int b){used=new[]{0,0};limits=new[]{a,b};return plan_stage();}
}
internal static class MechanicTests
{
    private static int checks;
    private static void Check(bool condition,string label){if(!condition)throw new Exception("Native mechanic regression: "+label);checks++;}
    private static LimitedBattle Setup(string name,string[]? others=null,string opponent="攻击强化",bool rich=false)
    {
        var b=new LimitedBattle(new[]{new[]{name}.Concat(others??Array.Empty<string>()).ToArray(),new[]{opponent,"防御强化","护盾"}});
        var cfg=JsonSerializer.SerializeToElement(new {kind="mechanism",weights=PolicyTables.learning_policy_INITIAL_WEIGHTS,checkpoint="test-mechanism"});b.policy_configs=new JsonElement?[]{cfg,cfg};
        foreach(var s in b.players){s.pile=s.instances.ToList();s.hand=new(){s.pile[0]};s.pile.RemoveAt(0);s.stage=1;s.energy=100;}
        if(rich){var s=b.players[0];var e=b.players[1];s.hp=400;s.shield=250;e.shield=150;e.poison=6;var el=Catalog.CARDS[name].element;s.specialization=Catalog.ELEMENTS.Contains(el)?el:"fire";foreach(var element in Catalog.ELEMENTS)s.layers[element]=5;s.active.Add(Catalog.SPECIAL[s.specialization]);s.ember=s.specialization=="fire"?3:0;s.active.Add(new Dictionary<string,string>{{"fire","灰烬循环"},{"water","潮汐回响"},{"earth","大地之握"},{"wind","风痕"},{"poison","蚀骨"}}[s.specialization]);s.tide=s.specialization=="water"?75:0;}
        return b;
    }
    private static void Effect(Battle b,string n)=>Effects.Apply(b,0,new Instance("effect",n),new Before(1000,0,0,40,0,0,0,0,false));
    public static void Run()
    {
        // Exact ports of reviewed test_current.py pre-position/instance invariants.
        foreach(var name in Catalog.CARDS.Keys)foreach(var rich in new[]{false,true})
        {var b=Setup(name,new[]{"火花","掠影","护盾","短促"},rich:rich);var s=b.players[0];s.discard.Add(s.pile[^1]);s.pile.RemoveAt(s.pile.Count-1);b.resolve_sequences(b.One());Check(b.executed[name]==1,"all-card locked commit "+name);foreach(var p in b.players)p.assert_instances();}
        foreach(var row in Catalog.CARDS.Values.Where(c=>!c.ability))
        {var b=Setup(row.name,new[]{"火花","掠影","护盾","短促"});b.resolve_sequences(b.One());var s=b.players[0];var x=s.discard.First(x=>x.name==row.name);s.discard.Remove(x);s.hand.Insert(0,x);s.energy=100;var layers=s.layers.Clone();b.resolve_sequences(b.One());Check(Catalog.ELEMENTS.All(el=>b.players[0].layers[el]==layers[el]),"first-layer replay "+row.name);}
        {
            var b=Setup("护盾");var s=b.players[0];var e=b.players[1];s.active.Add("不周山");s.layers["earth"]=100;e.hp=10;e.fx["lifeguard"]=1;b.resolve_sequences(b.One());s=b.players[0];e=b.players[1];Check(s.shield_total==0&&s.defense==0&&e.hp==1&&e.guard_used,"execute consumes old guard");
        }
        {
            var b=Setup("潮汐风暴",opponent:"气旋护壁");b.players[0].active.Add("潮汐之心");b.players[0].tide=1000;b.resolve_sequences(b.One(true));Check(b.players[1].hp<=0&&!b.players[1].guard_used,"new guard cannot protect same position");
        }
        {
            var b=Setup("潮汐风暴",opponent:"护盾");b.players[0].active.Add("潮汐之心");b.players[0].tide=100;b.resolve_sequences(b.One(true));Check(b.players[1].hp==890&&b.players[1].shield_total==60,"new shield cannot absorb same position");
        }
        {
            var b=Setup("万毒爆");b.players[0].layers["poison"]=10;b.players[1].poison=3;b.resolve_sequences(b.One());Check(b.players[1].hp==780&&b.players[0].layers["poison"]==11,"own panels excluded from same-position read");
        }
        foreach(var initial in new[]{0,100})
        {
            var b=Setup("碎盾术",opponent:"护盾");b.players[0].active.Add("不周山");b.players[0].layers["earth"]=10;b.players[1].defense=0;b.players[1].shield=initial;b.resolve_sequences(b.One(true));Check(b.players[1].hp==(initial==0?980:1000)&&b.players[1].shield_total==(initial==0?60:115),"old/new shield provenance "+initial);
        }
        {
            var b=Setup("潮汐风暴",opponent:"泉眼");b.players[0].active.Add("潮汐之心");b.players[0].tide=50;b.players[1].hp=50;b.players[1].defense=0;b.resolve_sequences(b.One(true));Check(b.players[1].hp<=0,"new heal cannot save same-position lethal damage");
        }
        {
            var b=Setup("潮汐风暴",opponent:"时光倒流");b.players[0].active.Add("潮汐之心");b.players[0].tide=1000;b.resolve_sequences(b.One(true));Check(b.players[1].hp<=0&&!b.players[1].revived,"new revive cannot save same position");
        }
        {
            var b=Setup("潮汐风暴",opponent:"灵感涌现");b.players[0].active.Add("潮汐之心");b.players[0].tide=1000;b.players[1].rewind=new Q[]{0,100,40,500,0};var seq=b.Limited(100,100);b.resolve_sequences(seq);Check(b.players[1].revived&&b.executed_by_seat[1].Values.Aggregate(new Q(0),(a,v)=>a+v)==1,"existing revive cuts tail");Check(b.players[1].hand.SequenceEqual(seq[1][0].after.state.hand),"existing revive retains private prefix");
        }
        {
            var b=Setup("毒瓶",opponent:"风之躯");b.players[1].poison=3;b.resolve_sequences(b.One(true));Check(b.players[1].poison==2,"same-position cleanse preserves new poison");
        }
        {
            var b=Setup("活水",new[]{"火花"});b.players[0].hp=900;var seq=b.One();b.players[0].hp=1000;b.resolve_sequences(seq);Check(b.players[0].hand.SequenceEqual(seq[0][0].after.state.hand),"promised draw survives full-health divergence");
        }
        {
            var b=Setup("毒爆",new[]{"毒涌","攻击强化","护盾"});var s=b.players[0];s.hand.Add(s.pile[0]);s.pile.RemoveAt(0);var seq=b.Limited(2,0);Check(!(bool)seq[0][1].flags["poison_draw"]!,"planning does not invent poison");b.resolve_sequences(seq);Check(b.players[1].poison==0&&b.players[0].hand.Count==1,"poison activation rollback no grant");
        }
        {
            var b=Setup("短促",new[]{"毒爆"},opponent:"潮汐风暴");var s=b.players[0];var e=b.players[1];s.hand.Add(s.pile[0]);s.pile.RemoveAt(0);s.hp=1;e.active.Add("潮汐之心");e.tide=1000;var seq=b.Limited(2,1);b.resolve_sequences(seq);Check(!b.players[0].active.Contains("毒爆")&&b.players[1].poison==0&&b.executed["毒爆"]==0,"unreached activation tail has no effects");
        }
        {
            var b=Setup("血潮");var s=b.players[0];var e=b.players[1];s.active.Add("血潮");s.defense=e.defense=0;b.fire(new(){0});Check(s.hp==900,"blood launch life payment");b.wave(new(){b.packet(1,100,"missile",new(){{"main",true}})});Check(s.hp==827,"blood protection reads current life");s.hp=500;b.wave(new(){b.packet(1,100,"missile",new(){{"main",true}})});Check(s.hp==415,"blood current life second attack");b.wave(new(){b.packet(1,100,"missile",new(){{"main",true}})});Check(s.hp==328,"blood current life third attack");
        }
        foreach(var names in new[]{new[]{"猛火弹","引火","爆燃"},new[]{"爆燃","引火","猛火弹"}})
        {var b=Setup("护盾");var s=b.players[0];foreach(var n in names)Effect(b,n);Check(s.fx["bonus"]==50,"additive order independent");s.attack=135;s.ember=2;Effect(b,"压缩爆燃");Effect(b,"导火索");Check(s.fx["bonus"]==145,"conditional bonus components add");Effect(b,"压缩爆燃");Check(s.fx["bonus"]==225,"duplicate conditional bonuses add");}
        {
            var b=Setup("护盾");var s=b.players[0];s.layers["wind"]=4;s.fx["permanent_delay"]=-3;foreach(var n in new[]{"烈焰冲刺","短促","风步"})Effect(b,n);Check(s.fx["delay"]==new Q(-45,2)&&b.gap(s)==22,"fractional interval exact floor");b.players[1].poison=3;Effect(b,"爆裂");Effect(b,"爆裂");Check(s.fx["delay"]==new Q(-125,2)&&b.gap(s)==1,"interval floor cap");
        }
        Console.WriteLine($"NATIVE_COMBAT_MECHANICS_OK checks={checks} all-card-locked=366 replay-first-layers=OK simultaneous-protection=OK exact-arithmetic=OK");
    }
}
