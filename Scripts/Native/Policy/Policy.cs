using System.Text.Json;
namespace Magicians.Native;

public sealed class CycleRecord
{ public int energy;public string? chosen;public HashSet<string> blocked=new(StringComparer.Ordinal); }

public static class Policy
{
    public static readonly HashSet<string> DRAW=PolicyTables.policy_DRAW.EnumerateArray().Select(x=>x.GetString()!).ToHashSet(StringComparer.Ordinal);
    public static readonly HashSet<string> SHUFFLE=PolicyTables.policy_SHUFFLE.EnumerateArray().Select(x=>x.GetString()!).ToHashSet(StringComparer.Ordinal);
    public static readonly HashSet<string> SEARCH=PolicyTables.policy_SEARCH.EnumerateArray().Select(x=>x.GetString()!).ToHashSet(StringComparer.Ordinal);
    public static readonly Dictionary<string,string[]> SUPPORT=PolicyTables.policy_SUPPORT.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.EnumerateArray().Select(v=>v.GetString()!).ToArray());
    public static readonly Dictionary<string,Dictionary<string,double>> STYLES=PolicyTables.policy_STYLES.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.EnumerateObject().ToDictionary(v=>v.Name,v=>v.Value.GetDouble()));
    public static readonly Dictionary<string,string> STYLE_LABELS=PolicyTables.policy_STYLE_LABELS.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.GetString()!);
    private static readonly Lazy<Dictionary<string,Dictionary<string,object?>>> Targets=new(BuildTargets);
    public static Dictionary<string,Dictionary<string,object?>> TARGETS=>Targets.Value;
    private static Dictionary<string,Dictionary<string,object?>> BuildTargets()
    {
        var result=new Dictionary<string,Dictionary<string,object?>>(StringComparer.Ordinal);
        foreach(string element in Catalog.ELEMENTS)foreach(string route in Catalog.ROUTES[element])
        {
            var support=SUPPORT[route];
            var extra=Catalog.CARDS.Keys.Where(n=>Catalog.CARDS[n].element==element&&Catalog.CARDS[n].enchantment&&!support.Contains(n))
                .OrderByDescending(DRAW.Contains).ThenByDescending(SEARCH.Contains).ThenByDescending(n=>Catalog.CARDS[n].cost<=1).ThenByDescending(n=>Catalog.CARDS[n].layers).Take(4);
            var learned=new[]{Catalog.SPECIAL[element],route}.Concat(support).Concat(extra).Concat(new[]{"灵感涌现","通晓","盾之共鸣"}).Distinct(StringComparer.Ordinal);
            result[route]=NJson.D(("element",element),("route",route),("entries",Catalog.START.Concat(learned).ToArray()));
        }
        if(result.Count!=15)throw new InvalidDataException("Invalid v1.2 target registry");return result;
    }
    private static readonly Lazy<JsonElement> DefaultConfig=new(()=>NJson.Read("tools/sim_v12/rules/ai-policy.json"));
    public static JsonElement config(Player p)=>p.policy??DefaultConfig.Value;
    public static JsonElement config(Battle b,int i)=>b.policy_configs?[i]??DefaultConfig.Value;
    private static Dictionary<string,double> effective_weights(JsonElement cfg,string element)
    {
        var weights=cfg.GetProperty("weights");var factors=NJson.Obj(NJson.Obj(cfg,"element_weights"),element);
        return weights.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.GetDouble()*(NJson.Str(cfg,"kind")=="explorer"?NJson.Num(factors,x.Name,1):1));
    }
    private static double utility(Card c,Features f,Dictionary<string,double> w,Dictionary<string,double> style)=>
        w["panel"]*(c.attack*style["attack"]+c.defense*style["defense"])+w["draw"]*f["draw"]*style["draw"]/20+w["fee"]*f["fee"]+
        w["search"]*f["search"]*style["search"]/10+w["recycle"]*(f["shuffle"]+f["recover"])+w["discard"]*f["discard"]+
        w["future"]*(f["future_draw"]+1.3*f["future_fee"])+w["passive"]*f["passive"]+w["tempo"]*f["delay"]+w["protection"]*(f["shield"]*.7+f["heal"]*.5);
    private static (List<Instance> options,CycleRecord? record) cycle_options(Battle b,Fighter s,List<Instance> affordable)
    {
        if(b._phase!="plan")return(affordable,null);
        // A canonical structural key preserves Python's tuple/multiset equality;
        // no private pile ordering or future RNG is observed.
        var key=JsonSerializer.Serialize(new object?[]{
            s.hand.Select(x=>x.uid).Order(StringComparer.Ordinal).ToArray(),
            s.pile.GroupBy(x=>x.name).OrderBy(g=>g.Key,StringComparer.Ordinal).Select(g=>new object[]{g.Key,g.Count()}).ToArray(),
            s.discard.Select(x=>x.uid).Order(StringComparer.Ordinal).ToArray(),s.removed.Select(x=>x.uid).Order(StringComparer.Ordinal).ToArray(),
            s.seen.Order(StringComparer.Ordinal).ToArray(),s.known_top.ToArray(),
            s.discounts.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>new object[]{x.Key,x.Value}).ToArray(),
            Math.Min(4,s.spent),Math.Min(4,s.played),Math.Min(2,(int)s.counts["wind"]),Math.Min(2,(int)s.counts["fire"]),
            Math.Min(6,b.players[1-s.seat].poison),Math.Min(50,s.tide),s.ember,s.fx["tailwind_used"].ToString(),s.fx["tactic_used"].ToString(),
            Catalog.ELEMENTS.Select(e=>s.fx["discount_"+e].ToString()).ToArray()});
        if(!b._economic_visits.TryGetValue(key,out var record))b._economic_visits[key]=record=new(){energy=s.energy};
        else if(s.energy>=record.energy&&record.chosen is not null){record.blocked.Add(record.chosen);b.cycle_decisions[s.seat]["alternative_or_end"]+=1;}
        var options=affordable.Where(x=>!record.blocked.Contains(x.uid)).ToList();
        if(affordable.Count>0&&options.Count==0)b.cycle_decisions[s.seat]["voluntary_end"]+=1;
        return(options,record);
    }
    public static Instance? choose_play(Battle b,int i)
    {
        var cfg=config(b,i);var s=b.players[i];var options=s.hand.Where(x=>b.cost(s,x)<=s.energy).ToList();
        var (affordable,record)=cycle_options(b,s,options);if(affordable.Count==0)return null;
        Instance? chosen;
        if(NJson.Str(cfg,"kind")=="legacy")chosen=LegacyChoosePlay(b,i,affordable);
        else
        {
            var w=effective_weights(cfg,s.specialization);var style=STYLES[b.styles[i]];
            var bag=s.pile.Concat(s.discard).Select(x=>x.name).ToArray();var known=s.instances.ToDictionary(x=>x.uid,x=>x.name,StringComparer.Ordinal);
            var top=s.known_top.Where(known.ContainsKey).Select(uid=>known[uid]).ToArray();var handNames=s.hand.Select(x=>x.name).ToArray();
            double Score(Instance x)
            {
                var c=Catalog.CARDS[x.name];var f=Mechanics.dynamic_features(b,s,x.name);int cost=b.cost(s,x);double after=s.energy-cost+f["fee"];
                var available=top.Take(Math.Max(1,(int)f["draw"])).ToArray();if(available.Length==0)available=bag;
                double playable=available.Count(n=>Catalog.CARDS[n].cost<=after)/(double)Math.Max(1,available.Length);
                double value=utility(c,f,w,style);value+=w["layer"]*c.layers*(!s.seen.Contains(x.uid)?1:0);
                if(c.kind=="specialization")value+=!s.has(c.name)?120:0;else if(c.kind=="route_legendary")value+=!s.has(c.name)?65:0;else if(c.ability)value+=12;
                value+=w["lookahead"]*(f["draw"]*(playable*5-2)+f["fee"]*Math.Min(3,s.hand.Count)*1.2);
                if(top.Length>0&&f["draw"]!=0)value+=w["lookahead"]*top.Take(Math.Max(1,(int)f["draw"])).Sum(n=>Catalog.CARDS[n].kind=="specialization"?5:2+Mechanics.static_features(n)["fee"]);
                if(f["search"]!=0){bool follows=handNames.Any(n=>n!=x.name&&Mechanics.static_features(n)["draw"]!=0&&Catalog.CARDS[n].cost<=s.energy-cost);value+=w["lookahead"]*(follows?5:-2);}
                if(f["shuffle"]!=0)
                {
                    double recycled=s.discard.Sum(a=>Mechanics.static_features(a.name)["draw"]+Mechanics.static_features(a.name)["fee"]+(s.seen.Contains(a.uid)?1:0));
                    value+=w["recycle"]*Math.Min(3,recycled/Math.Max(1,s.pile.Count));if(s.discard.Count==0)value-=w["recycle"]*1.5;
                    if(top.Length>0)value-=w["lookahead"]*10;if(s.has("灰烬循环")&&s.has("炽焰王冠"))value+=3;
                }
                if(f["discard"]!=0)
                {
                    var others=s.hand.Where(a=>a.uid!=x.uid).ToArray();int waste=others.Count(a=>b.cost(s,a)>Math.Max(0,after)||a.name is "攻击强化" or "防御强化");
                    value+=w["thinning"]*Math.Min(f["discard"],waste)*3;if(others.Length==0)value-=3;
                }
                if(x.name=="顺风"&&f["fee"]==0)value-=12;
                if(x.name=="战术"&&s.energy!=0)value-=30+8*s.hand.Count(a=>a.uid!=x.uid&&b.cost(s,a)<=s.energy);
                if(x.name is "万毒爆" or "毒潮" && b.players[1-i].poison==0)value-=10;
                if(x.name=="气旋护壁"&&s.fx["lifeguard"]==0)value+=s.hp<300?10:1;
                return value/Math.Pow(Math.Max(1,cost),style["curve"]);
            }
            chosen=affordable.OrderByDescending(Score).ThenByDescending(x=>x.uid,StringComparer.Ordinal).First();
        }
        if(record is not null){record.energy=s.energy;record.chosen=chosen?.uid;}return chosen;
    }
    public static double draft_value(Player p,string name,List<Dictionary<string,object?>> publicPlayers)
    {
        var cfg=config(p);if(NJson.Str(cfg,"kind") is "thin" or "explorer")return Construction.draft_quality(p,name,publicPlayers);
        if(NJson.Str(cfg,"kind")=="legacy")return LegacyDraftValue(p,name,publicPlayers);
        var c=Catalog.CARDS[name];var w=effective_weights(cfg,p.spec!=""?p.spec:p.preference);var style=STYLES[p.style];var names=p.deck.Select(x=>x.name).ToArray();
        var counts=names.GroupBy(n=>Catalog.CARDS[n].element).ToDictionary(g=>g.Key,g=>g.Count());string focus=p.spec!=""?p.spec:NJson.Focus(names,p.preference);var f=Mechanics.static_features(name);
        double value=utility(c,f,w,style)/Math.Pow((c.cost+1)/2.0,.35);value+=c.element==focus?5:c.element=="generic"?0:-4;
        if(p.spec==""&&c.enchantment&&c.element==focus)value+=3+counts.GetValueOrDefault(focus)*3;value+=w["layer"]*c.layers;
        var (draw,fee,cheap)=Mechanics.composition(names);
        if(f["draw"]!=0)value+=w["lookahead"]*(cheap+fee-draw*.8)*f["draw"];
        if(f["fee"]!=0)value+=w["lookahead"]*Math.Min(2,draw)*f["fee"]*2;
        if(f["search"]!=0)value+=w["lookahead"]*(draw>.35?2:-.5)*f["search"];
        if(f["discard"]!=0)value+=w["thinning"]*(1-cheap)*2;
        if(f["shuffle"]!=0&&names.Any(Mechanics.SHUFFLES.Contains))value-=2;
        if(p.route!=""&&SUPPORT[p.route].Contains(name))value+=style["support"];
        if(c.kind=="route_legendary")
        {
            if(publicPlayers.Any(x=>Convert.ToInt32(x["pid"])!=p.pid&&(string?)x["route"]==name))return -100;
            value+=p.route==""?12:-12;value+=names.Count(n=>SUPPORT[name].Contains(n))*2;
        }
        return value-Math.Max(0,names.Length-18)*.7;
    }
    public static List<Instance> choose_cards(Battle b,Fighter s,List<Instance> candidates,int count,string purpose)
    {
        var cfg=config(b,s.seat);
        if(NJson.Str(cfg,"kind")=="legacy")
        {return (purpose=="discard"?candidates.OrderBy(x=>b.rank_card(s,x)).ThenBy(x=>x.uid,StringComparer.Ordinal):candidates.OrderByDescending(x=>b.rank_card(s,x)).ThenByDescending(x=>x.uid,StringComparer.Ordinal)).Take(count).ToList();}
        var w=effective_weights(cfg,s.specialization);var style=STYLES[b.styles[s.seat]];bool instant=purpose is "draw" or "recover";
        double budget=instant?s.energy:Math.Max(s.energy,3+(double)s.pending["energy"]);
        double Score(Instance x)
        {
            var c=Catalog.CARDS[x.name];var f=Mechanics.dynamic_features(b,s,x.name);double value=utility(c,f,w,style);
            value+=w["layer"]*c.layers*(!s.seen.Contains(x.uid)?1:0);if(c.kind=="specialization"&&!s.has(c.name))value+=70;if(c.kind=="route_legendary"&&!s.has(c.name))value+=40;
            if(c.cost>budget)value-=14+3*(c.cost-budget);if(c.name is "攻击强化" or "防御强化" && s.seen.Contains(x.uid))value-=4;
            if(instant&&c.cost<=budget)value+=f["fee"]*3+f["draw"]*2;return value/Math.Pow(Math.Max(1,c.cost),.4);
        }
        bool ascending=purpose=="discard"||purpose=="topdeck"&&ReferenceEquals(candidates,s.hand);
        return (ascending?candidates.OrderBy(Score).ThenBy(x=>x.uid,StringComparer.Ordinal):candidates.OrderByDescending(Score).ThenByDescending(x=>x.uid,StringComparer.Ordinal)).Take(count).ToList();
    }
    private static Instance? LegacyChoosePlay(Battle b,int i,List<Instance> affordable)
    {
        var s=b.players[i];var enemy=b.players[1-i];var style=STYLES[b.styles[i]];
        double Score(Instance x)
        {
            var c=Catalog.CARDS[x.name];double value=10+c.attack*style["attack"]+c.defense*style["defense"];
            if(!s.seen.Contains(x.uid))value+=c.layers*style["layer"];if(c.kind=="specialization")value+=150;if(c.kind=="route_legendary")value+=80;
            if(DRAW.Contains(x.name))value+=style["draw"];if(SEARCH.Contains(x.name))value+=style["search"];if(SHUFFLE.Contains(x.name))value+=style["shuffle"];
            if(x.name=="气旋护壁")value+=s.hp<300?60:-8;if(x.name=="顺风"&&s.counts["wind"]<2)value-=25;if(x.name=="战术")value-=80;
            if(x.name is "包扎" or "坚壁" or "狂战" && s.hand.Count<=1)value-=10;if(x.name=="泉眼"&&s.hp*2<=s.max_hp)value+=10;
            if(x.name is "万毒爆" or "毒潮" && enemy.poison==0)value-=15;return value/Math.Pow(Math.Max(1,b.cost(s,x)),style["curve"]);
        }
        return affordable.OrderByDescending(Score).ThenByDescending(x=>x.uid,StringComparer.Ordinal).FirstOrDefault();
    }
    private static double LegacyDraftValue(Player p,string name,List<Dictionary<string,object?>> publicPlayers)
    {
        var c=Catalog.CARDS[name];var counts=p.deck.GroupBy(x=>Catalog.CARDS[x.name].element).ToDictionary(g=>g.Key,g=>g.Count());var style=STYLES[p.style];string focus=p.spec!=""?p.spec:NJson.Focus(p.deck.Select(x=>x.name),p.preference);
        double value=(c.attack*style["attack"]+c.defense*style["defense"])/((c.cost+1)/2.0)/3+(c.element==focus?7:c.element=="generic"?0:-3);
        if(p.spec==""&&c.enchantment&&c.element==focus)value+=3+counts.GetValueOrDefault(focus)*3;
        if(DRAW.Contains(c.name))value+=style["draw"]/5;if(SEARCH.Contains(c.name))value+=style["search"]/8;if(SHUFFLE.Contains(c.name)&&!p.deck.Any(x=>SHUFFLE.Contains(x.name)))value+=style["shuffle"]/2;
        if(p.route!=""&&SUPPORT[p.route].Contains(name))value+=style["support"];
        if(c.kind=="route_legendary")
        {if(publicPlayers.Any(x=>Convert.ToInt32(x["pid"])!=p.pid&&(string?)x["route"]==name))return -100;value+=p.route==""?12:SUPPORT.GetValueOrDefault(p.route,Array.Empty<string>()).Contains(name)?8:-12;value+=p.deck.Count(x=>SUPPORT[name].Contains(x.name))*2;}
        return value-Math.Max(0,p.deck.Count-18)*.6;
    }
}
