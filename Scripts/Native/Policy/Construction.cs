using System.Text.Json;
namespace Magicians.Native;

public static class Construction
{
    public static readonly Dictionary<string,double> THIN_INITIAL=PolicyTables.construction_THIN_INITIAL.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.GetDouble());
    public static readonly Dictionary<string,double> REPEAT=PolicyTables.construction_REPEAT.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.GetDouble());
    public static readonly Dictionary<string,double> BURST=PolicyTables.construction_BURST.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.GetDouble());
    public static JsonElement policy(Player p)=>Policy.config(p);
    public static bool is_thin(Player p)=>NJson.Str(policy(p),"kind") is "thin" or "explorer";
    private sealed record Context(Dictionary<string,double> values,double reference,double effective,Func<string,double> quality);
    private static Context quality_context(Player p)
    {
        var names=p.deck.Select(x=>x.name).ToArray();var cfg=policy(p);var parameters=NJson.Obj(cfg,"construction");
        double V(string key)=>NJson.Num(parameters,key,THIN_INITIAL[key]);var weights=Policy.STYLES[p.style];
        double eff=names.Sum(n=>Catalog.CARDS[n].ability ? .35 : 1.0),mainlayers=names.Where(n=>Catalog.CARDS[n].element==p.spec).Sum(n=>Catalog.CARDS[n].layers);
        double draw=names.Sum(n=>Mechanics.static_features(n)["draw"])/Math.Max(1,names.Length),fee=names.Sum(n=>Mechanics.static_features(n)["fee"])/Math.Max(1,names.Length);
        double Quality(string n)
        {
            var c=Catalog.CARDS[n];var f=Mechanics.static_features(n);
            double panel=.36*(c.attack*weights["attack"]+c.defense*weights["defense"]);
            double flow=(3.6*f["draw"]+5*f["fee"]+1.7*f["search"]+2.2*f["shuffle"]+2.5*f["recover"])*V("flow_scale");
            if(n=="风痕回廊"&&p.route=="风痕")flow-=7.2*V("flow_scale");
            double value=panel+flow+f["delay"]*1.8+BURST.GetValueOrDefault(n)+V("core_weight")*REPEAT.GetValueOrDefault(n);
            value+=.6*f["shield"]+.5*f["heal"]+1.2*(f["future_draw"]+1.3*f["future_fee"]);
            if(c.ability)value+=f["passive"]*1.5;
            if(c.element==p.spec)value+=V("first_layer")*c.layers/(1+mainlayers/12);
            else if(c.element=="generic")value-=V("generic_tax");else if(p.spec!="")value-=4;
            if(Policy.SUPPORT.GetValueOrDefault(p.route,Array.Empty<string>()).Contains(n))value+=2.2;
            if(n is "毒龙" or "毒瓶" or "毒针" && p.spec=="poison")value+=2.5;
            if(n=="气旋护壁")value+=4;if(n=="回流"&&p.spec=="water")value+=3;
            if(f["draw"]!=0&&f["fee"]==0&&draw>fee+.4)value-=Math.Min(2,draw-fee)*c.cost;
            return value/(1+.35*c.cost);
        }
        var values=names.Distinct(StringComparer.Ordinal).ToDictionary(n=>n,Quality);
        var repeatable=values.Where(x=>!Catalog.CARDS[x.Key].ability).Select(x=>x.Value).OrderDescending().Take(8).ToArray();
        return new(values,repeatable.Sum()/Math.Max(1,repeatable.Length),eff,Quality);
    }
    public static double draft_quality(Player p,string name,List<Dictionary<string,object?>>? publicPlayers=null)
    {
        publicPlayers??=new();if(NJson.Str(policy(p),"kind")=="explorer")return Exploration.quality(p,name,publicPlayers);
        double q=quality_context(p).quality(name);var c=Catalog.CARDS[name];
        if(p.spec==""&&c.enchantment)
        {var counts=p.deck.GroupBy(x=>Catalog.CARDS[x.name].element).ToDictionary(g=>g.Key,g=>g.Count());string focus=NJson.Focus(p.deck.Select(x=>x.name),p.preference);q+=c.element==focus?6+3*counts.GetValueOrDefault(focus):-3;}
        if(c.kind=="route_legendary")
        {if(publicPlayers.Any(x=>Convert.ToInt32(x["pid"])!=p.pid&&(string?)x["route"]==name))return -100;q+=p.route==""?12:-12;}
        return q;
    }
    public static double purchase_value(Player p,string name,int cost,string? replaced,List<Dictionary<string,object?>> publicPlayers)
    {
        if(NJson.Str(policy(p),"kind")=="explorer")return Exploration.purchase(p,name,cost,replaced,publicPlayers);
        if(!is_thin(p))return Policy.draft_value(p,name,publicPlayers)-2*cost-(replaced is not null?Policy.draft_value(p,replaced,publicPlayers):0);
        var par=policy(p).GetProperty("construction");var ctx=quality_context(p);double q=draft_quality(p,name,publicPlayers);
        double value=q-NJson.Num(par,"dilution")*ctx.reference-NJson.Num(par,"buy_margin")-NJson.Num(par,"point_cost")*cost-Math.Max(0,ctx.effective-NJson.Num(par,"target"))*.3;
        return replaced is not null?q-draft_quality(p,replaced,publicPlayers)-NJson.Num(par,"point_cost")*cost-NJson.Num(par,"buy_margin"):value;
    }
    public static double deletion_value(Player p,string name,int fee)
    {
        if(NJson.Str(policy(p),"kind")=="explorer")return Exploration.deletion(p,name,fee);
        var par=policy(p).GetProperty("construction");var ctx=quality_context(p);
        double gain=NJson.Num(par,"dilution")*ctx.reference-draft_quality(p,name)+Math.Max(0,ctx.effective-NJson.Num(par,"target"))*NJson.Num(par,"delete_pressure")+NJson.Num(par,"starter_bias")*(Catalog.START_ONLY.Contains(name)?1:0);
        return gain-NJson.Num(par,"delete_margin")-fee*NJson.Num(par,"point_cost");
    }
    public static bool? refresh_wanted(Player p,int round_number,List<string> unbought,List<Dictionary<string,object?>> publicPlayers)
    {
        if(NJson.Str(policy(p),"kind")=="explorer")return Exploration.refresh(p,round_number,unbought,publicPlayers);
        if(!is_thin(p))return null;
        return p.points>=2&&(quality_context(p).effective<NJson.Num(policy(p).GetProperty("construction"),"target")-1||round_number>=8&&p.route=="");
    }
}

public static class Exploration
{
    private sealed record Context(double reference,Func<string,double> score,string focus);
    private static Context view(Player p)
    {
        var cfg=Policy.config(p);var v=cfg.GetProperty("construction");var w=cfg.GetProperty("weights");string e=p.spec!=""?p.spec:p.preference;
        var factors=cfg.GetProperty("element_weights").GetProperty(e);var roles=NJson.Obj(cfg,"role_preferences");var style=Policy.STYLES[p.style];
        var names=p.deck.Select(x=>x.name).ToArray();string focus=p.spec!=""?p.spec:NJson.Focus(names,p.preference,true);
        double mainlayers=names.Where(n=>Catalog.CARDS[n].element==focus).Sum(n=>Catalog.CARDS[n].layers);
        double draw=names.Sum(n=>Mechanics.static_features(n)["draw"])/Math.Max(1,names.Length),fee=names.Sum(n=>Mechanics.static_features(n)["fee"])/Math.Max(1,names.Length);
        double V(string k)=>NJson.Num(v,k);double W(string k)=>NJson.Num(w,k);double F(string k)=>NJson.Num(factors,k,1);
        double Value(string n)
        {
            var c=Catalog.CARDS[n];var f=Mechanics.static_features(n);
            double flow=(W("draw")*f["draw"]+W("fee")*f["fee"]+W("search")*f["search"]+W("recycle")*F("recycle")*(f["shuffle"]+f["recover"]))*V("flow_scale");
            if(n=="风痕回廊"&&p.route=="风痕")flow-=2*W("draw")*V("flow_scale");
            double q=W("panel")*(c.attack*style["attack"]+c.defense*style["defense"])+flow;
            q+=V("core_weight")*Construction.REPEAT.GetValueOrDefault(n)+Construction.BURST.GetValueOrDefault(n)+W("tempo")*f["delay"];
            q+=W("protection")*F("protection")*(.7*f["shield"]+.5*f["heal"]);
            q+=W("future")*(f["future_draw"]+1.3*f["future_fee"]);q+=W("discard")*f["discard"]+W("passive")*f["passive"];
            if(c.element==focus)q+=V("first_layer")*F("layer")*c.layers/(1+mainlayers/20);
            else if(c.element=="generic")q-=V("generic_tax");else if(p.spec!="")q-=4;
            if(Policy.SUPPORT.GetValueOrDefault(p.route,Array.Empty<string>()).Contains(n))q+=2.2;
            if(f["draw"]!=0&&f["fee"]==0&&draw>fee+.4)q-=Math.Min(2,draw-fee)*c.cost;
            return q*NJson.Num(roles,f.panel_role,1)/(1+.35*c.cost);
        }
        var values=names.Distinct(StringComparer.Ordinal).ToDictionary(n=>n,Value);var repeat=names.Where(n=>!Catalog.CARDS[n].ability).Select(n=>values[n]).ToArray();
        return new(repeat.Sum()/Math.Max(1,repeat.Length),Value,focus);
    }
    public static double quality(Player p,string name,List<Dictionary<string,object?>>? publicPlayers=null)
    {
        publicPlayers??=new();var ctx=view(p);double q=ctx.score(name);var c=Catalog.CARDS[name];var cfg=Policy.config(p);
        string intent=NJson.Str(cfg,"element_intent");if(intent!=""&&c.element!="generic"&&c.element!=intent)return -100;
        string routeIntent=NJson.Str(cfg,"route_intent");if(routeIntent!=""&&c.kind=="route_legendary")q+=name==routeIntent?8:-4;
        if(p.spec==""&&c.enchantment){int count=p.deck.Count(x=>Catalog.CARDS[x.name].element==ctx.focus);q+=c.element==ctx.focus?6+3*count:-3;}
        if(c.kind=="route_legendary"){if(publicPlayers.Any(x=>Convert.ToInt32(x["pid"])!=p.pid&&(string?)x["route"]==name))return -100;q+=p.route==""?12:-12;}
        return q;
    }
    public static double purchase(Player p,string name,int cost,string? replaced,List<Dictionary<string,object?>> publicPlayers)
    {
        var cfg=Policy.config(p);var v=cfg.GetProperty("construction");var ctx=view(p);double q=quality(p,name,publicPlayers);
        double value=q-(replaced is not null?quality(p,replaced,publicPlayers):NJson.Num(v,"dilution")*ctx.reference)-NJson.Num(v,"buy_margin")-NJson.Num(v,"point_cost")*cost;
        if(NJson.Has(cfg,"size_exploration")&&replaced is null){var b=cfg.GetProperty("size_exploration");if(q>=.65*ctx.reference)value+=NJson.Num(b,"bonus")*Math.Max(0,(NJson.Num(b,"ceiling")-p.deck.Count)/Math.Max(1,NJson.Num(b,"ceiling")-8));}
        if(NJson.Has(cfg,"breadth")&&replaced is null)
        {
            var b=cfg.GetProperty("breadth");var c=Catalog.CARDS[name];string focus=p.spec!=""?p.spec:p.preference;int n=p.deck.Count;
            bool eligible=(c.element=="generic"||c.element==focus)&&!Catalog.START_ONLY.Contains(c.name);
            if(eligible&&q>=NJson.Num(b,"quality_floor")*ctx.reference)value+=NJson.Num(b,"fill_bonus")*Math.Max(0,(NJson.Num(b,"target")-n)/Math.Max(1,NJson.Num(b,"target")-8));
            value-=Math.Max(0,n-NJson.Num(b,"target"))*.45;
        }
        return value;
    }
    public static double deletion(Player p,string name,int fee)
    {
        var cfg=Policy.config(p);var v=cfg.GetProperty("construction");double reference=view(p).reference;
        double value=NJson.Num(v,"dilution")*reference-quality(p,name)+NJson.Num(v,"starter_bias")*(Catalog.START_ONLY.Contains(name)?1:0)-NJson.Num(v,"delete_margin")-fee*NJson.Num(v,"point_cost");
        if(NJson.Has(cfg,"size_exploration")){var b=cfg.GetProperty("size_exploration");value-=NJson.Num(b,"bonus")*Math.Max(0,(NJson.Num(b,"ceiling")-p.deck.Count)/Math.Max(1,NJson.Num(b,"ceiling")-8));}
        if(NJson.Has(cfg,"breadth")){var b=cfg.GetProperty("breadth");value-=NJson.Num(b,"retention_bonus")*Math.Min(1,Math.Max(0,NJson.Num(b,"target")+1-p.deck.Count));value-=NJson.Num(b,"paid_delete_penalty")*fee;}
        return value;
    }
    public static bool refresh(Player p,int r,List<string> unbought,List<Dictionary<string,object?>> publicPlayers)
    {if(p.points<2)return false;var values=unbought.Select(n=>purchase(p,n,0,null,publicPlayers)).ToArray();return values.Length==0||values.Max()<=0||r>=8&&p.route=="";}
}
