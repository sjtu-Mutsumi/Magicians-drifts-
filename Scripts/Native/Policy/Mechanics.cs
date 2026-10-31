using System.Text.Json;
using System.Text.RegularExpressions;
namespace Magicians.Native;

public sealed class Features : Dictionary<string,double>
{
    public string panel_role="";
    public Features():base(StringComparer.Ordinal){}
    public Features(Features other):base(other,StringComparer.Ordinal){panel_role=other.panel_role;}
}
public static class Mechanics
{
    private static readonly Dictionary<string,Features> Cache=new(StringComparer.Ordinal);
    private static readonly Lazy<JsonElement> Roles=new(()=>NJson.Read("tools/sim_v12/rules/panel-budgets.json").GetProperty("card_roles"));
    private static double Table(JsonElement table,string name)=>NJson.Num(table,name);
    public static readonly HashSet<string> SHUFFLES=PolicyTables.mechanics_SHUFFLES.EnumerateArray().Select(x=>x.GetString()!).ToHashSet(StringComparer.Ordinal);
    public static Features static_features(string name)
    {
        if(Cache.TryGetValue(name,out var cached))return cached;
        var c=Catalog.CARDS[name];string text=c.text;
        double Extract(string pattern)=>Regex.Matches(text,pattern).Sum(m=>double.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture));
        double shield=Extract(@"获得(\d+)(?:普通|临时)?护盾"),heal=Extract(@"回复(\d+)生命"),delay=Extract(@"(?:间隔减少|间隔永久减少)(\d+(?:\.\d+)?)");
        if(name=="疾风之刃")delay=0;
        var fixedDelay=new Dictionary<string,double>{{"短促",5},{"烈焰冲刺",10},{"风步",7.5},{"疾行",10},{"烈风",2.5}};
        if(fixedDelay.TryGetValue(name,out var value))delay=value;
        var future=PolicyTables.mechanics_FUTURE.TryGetProperty(name,out var entry)?entry:default;
        var f=new Features
        {
            ["draw"]=Table(PolicyTables.mechanics_DRAW_N,name),["fee"]=Table(PolicyTables.mechanics_FEE_N,name),
            ["search"]=Table(PolicyTables.mechanics_SEARCH_N,name),["discard"]=Table(PolicyTables.mechanics_DISCARD_N,name),
            ["shuffle"]=SHUFFLES.Contains(name)?1:0,["recover"]=name is "不灭烈焰" or "循毒"?1:0,
            ["future_draw"]=future.ValueKind==JsonValueKind.Array?future[0].GetDouble():0,["future_fee"]=future.ValueKind==JsonValueKind.Array?future[1].GetDouble():0,
            ["passive"]=Table(PolicyTables.mechanics_PASSIVES,name),["shield"]=shield/50,["heal"]=heal/40,["delay"]=delay/5,
            panel_role=Roles.Value.GetProperty(name).GetString()!
        };
        Cache[name]=f;return f;
    }
    public static (double draw,double fee,double cheap) composition(IEnumerable<string> sequence)
    {
        var names=sequence.ToArray();double total=Math.Max(1,names.Length);
        return(names.Sum(n=>static_features(n)["draw"])/total,names.Sum(n=>static_features(n)["fee"])/total,names.Count(n=>Catalog.CARDS[n].cost<=1)/total);
    }
    public static Features dynamic_features(Battle b,Fighter s,string name)
    {
        var f=new Features(static_features(name));var e=b.players[1-s.seat];
        if(name=="疾风之刃")f["delay"]=(4+(double)s.layers["wind"]/2)/5;
        if(name=="活水")f["draw"]=s.hp<s.max_hp?1:0;
        else if(name=="毒涌")f["draw"]=e.poison!=0?2:1;
        else if(name=="枯潮"){f["draw"]=e.poison+2>=6?1:0;f["fee"]=e.poison+2>=3?1:0;}
        else if(name=="开岩")f["draw"]=s.shield_total>=30?1:0;
        else if(name=="风驰")f["fee"]=b.gap(s)<=30?2:1;
        else if(name=="风刃")f["fee"]=s.hand.Any(x=>x.name==name&&!s.seen.Contains(x.uid))?1:0;
        else if(name=="顺风")f["fee"]=s.counts["wind"]>=2&&s.fx["tailwind_used"]==0?1:0;
        else if(name=="风痕回廊")f["draw"]=s.has("风痕")?0:2;
        else if(name=="战术")f["fee"]=s.energy==0&&s.fx["tactic_used"]==0?1:0;
        else if(name=="疾流")f["future_fee"]=s.tide>=30?2:0;
        else if(name=="循毒")
        {bool can=e.poison>=3&&s.discard.Any(x=>Catalog.CARDS[x.name].element=="poison"&&Catalog.CARDS[x.name].enchantment);f["draw"]=can?0:1;f["recover"]=can?1:0;}
        if(name is "不灭烈焰" or "循毒" && s.discard.Count==0)f["recover"]=0;
        return f;
    }
}
