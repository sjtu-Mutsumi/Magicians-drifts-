using System.Numerics;

namespace Magicians.Native;

public sealed class ShieldProvenance
{
    public int ordinary;public List<int> temporary;
    public ShieldProvenance(Fighter s){ordinary=s.shield;temporary=s.temporary.Select(b=>(int)b[0]).ToList();}
}

/// <summary>Reviewed pre-position reads, deferred healing and old-shield provenance.</summary>
public class StrictBattle : LockedBattle
{
    protected List<ShieldProvenance>? _old_shields;protected int[]? _slot_heals;protected int _strict_damage;
    public override string snapshot_rule=>"strict_pre_position_v2";
    public StrictBattle(IEnumerable<IEnumerable<string>> decks,IEnumerable<string>? specializations=null,BigInteger? seed=null,bool trace=false,
        Func<Dictionary<string,object?>,List<Dictionary<string,object?>>,int,string,IEnumerable<string>>? chooser=null,string[]? styles=null,string sequence_mode="locked")
        :base(decks,specializations,seed,trace,chooser,styles,sequence_mode){}
    public override void card_panel(Fighter s,Card c,bool first){}
    public override void finish_card_panel(Fighter s,Card c,bool first)
    {base.card_panel(s,c,first);if(s.fx["defense_zero_position"]!=0)s.defense-=c.defense;}
    protected override void slot_begin(List<Fighter> initial){_old_shields=initial.Select(s=>new ShieldProvenance(s)).ToList();_slot_heals=new[]{0,0};foreach(var s in players)s.fx["defense_zero_position"]=0;}
    protected override void slot_end(){_old_shields=null;_slot_heals=null;}
    public override (Q attack,Q defense) stats(int i)
    {
        if(_slot is null||_phase=="plan")return base.stats(i);var actual=players;players=_slot;try{return base.stats(i);}finally{players=actual;}
    }
    public override Q fire_factor(Fighter s)=>base.fire_factor(_slot is not null?_slot[s.seat]:s);
    public override int gap(Fighter s)=>base.gap(_slot is not null&&_phase!="plan"?_slot[s.seat]:s);
    protected override bool execution_ready(int i)=>(_slot??players)[i].has("不周山");
    protected override (int shield,int hp) execution_target(int i)
    {if(_old_shields is null)return base.execution_target(i);var old=_old_shields[i];return(old.ordinary+old.temporary.Sum(),Math.Max(0,players[i].hp-_slot_heals![i]));}
    public override Packet burst(int i,int shield,Q? multiplier=null)
    {var actual=players;if(_slot is not null)players=_slot;try{return base.burst(i,shield,multiplier);}finally{players=actual;}}
    public override List<Fighter> damage_snapshot()
    {
        if(_slot is null)return base.damage_snapshot();var snapshot=ClonePlayers(_slot);for(int i=0;i<snapshot.Count;i++){var s=snapshot[i];var old=_old_shields![i];s.shield=old.ordinary;s.temporary=old.temporary.Select(n=>new Q[]{n,0,0}).ToList();s.hp=players[i].hp;}return snapshot;
    }
    protected override int FlushShieldsCore(Fighter s)
    {
        if(_phase=="actual"&&_old_shields is not null&&s.shield_events.Any(x=>!x.temporary&&!x.no_decay)){var old=_old_shields[s.seat];old.ordinary=Ceil(new Q(old.ordinary*3,4));}return base.FlushShieldsCore(s);
    }
    protected override void ShieldGainCore(Fighter s,Q amount,bool temporary,bool no_decay,Q conversion)
    {
        ShieldGainRaw(s,amount,temporary,no_decay,conversion);if(_phase=="actual"&&_entries.Count>0&&_wave_depth==0){var loss=flush_shields(s);_decay_credit[s.seat]=_decay_credit.GetValueOrDefault(s.seat)+loss;}
    }
    protected override int TakeShieldCore(Fighter s,int n,bool ordinary_only)
    {
        var before=s.temporary.Select(b=>(int)b[0]).ToList();var ordinary=s.shield;var result=base.TakeShieldCore(s,n,ordinary_only);
        if(_phase=="actual"&&_old_shields is not null){var old=_old_shields[s.seat];for(int k=0;k<old.temporary.Count;k++){var amount=old.temporary[k];old.temporary[k]-=Math.Min(amount,Math.Max(0,before[k]-(int)s.temporary[k][0]));}old.ordinary-=Math.Min(old.ordinary,Math.Max(0,ordinary-s.shield));}return result;
    }
    public override int take_damage_shield(Fighter s,int amount)
    {
        if(_old_shields is null)return base.take_damage_shield(s,amount);var old=_old_shields[s.seat];var left=amount;for(int k=0;k<old.temporary.Count;k++){var available=old.temporary[k];if(k>=s.temporary.Count){old.temporary[k]=0;continue;}var used=Math.Min(left,available);left-=used;old.temporary[k]-=used;s.temporary[k][0]-=used;}var ordinary=Math.Min(left,old.ordinary);left-=ordinary;old.ordinary-=ordinary;s.shield-=ordinary;var actual=amount-left;if(actual!=0)state_changed("shield",s.seat);return actual;
    }
    protected override int HealCore(Fighter s,Q amount,bool record,Q bonus)
    {
        if(_phase!="actual"||_slot is null)return base.HealCore(s,amount,record,bonus);var old=_slot[s.seat];var layers=s.layers;var mods=s.mods;var active=s.active;s.layers=old.layers;s.mods=old.mods;s.active=old.active;int actual;try{actual=base.HealCore(s,amount,record,bonus);}finally{s.layers=layers;s.mods=mods;s.active=active;}_slot_heals![s.seat]+=actual;return actual;
    }
    protected override int LoseLifeCore(Fighter s,int amount,bool payment)
    {
        if(payment||_slot is null||_strict_damage==0)return base.LoseLifeCore(s,amount,payment);var active=s.fx["lifeguard"];s.fx["lifeguard"]=_slot[s.seat].fx["lifeguard"];try{return base.LoseLifeCore(s,amount,payment);}finally{s.fx["lifeguard"]=active;}
    }
    protected override void Death()
    {
        if(_slot is null){base.Death();return;}var rewinds=players.Select(s=>s.rewind).ToArray();for(int i=0;i<players.Count;i++)players[i].rewind=_slot[i].rewind;
        try{base.Death();}finally{for(int i=0;i<players.Count;i++){var s=players[i];s.rewind=s.hp<=0&&_cut.Contains(s.seat)?_slot[i].rewind:rewinds[i];}}
    }
    public override void wave(List<Packet> packets,bool reaction=false)
    {
        if(_slot is null){base.wave(packets,reaction);return;}var deferred=_slot_heals!.ToArray();var revived=players.Select(s=>s.revived).ToArray();_slot_heals=new[]{0,0};for(int i=0;i<players.Count;i++)players[i].hp-=deferred[i];_strict_damage++;
        try{base.wave(packets,reaction);}finally{_strict_damage--;for(int i=0;i<players.Count;i++){var s=players[i];if(s.hp>0&&s.revived==revived[i]){var amount=Math.Min(deferred[i],Math.Max(0,s.max_hp-s.hp));s.hp+=amount;_slot_heals[i]+=amount;}}}
    }
}
