using System.Text.Json.Nodes;
using Magicians.Native;

namespace Magicians.Tooling;

public static class TrainingProposals
{
    public static readonly Dictionary<string,double> ExplorerInitial=new(){["dilution"]=.92,["buy_margin"]=.45,["point_cost"]=.4,["delete_margin"]=.6,["starter_bias"]=.9,["core_weight"]=2,["first_layer"]=2.5,["generic_tax"]=.8,["flow_scale"]=.8};
    public static JsonObject ElementInitial()=>new(Catalog.ELEMENTS.Select(e=>new KeyValuePair<string,JsonNode?>(e,TJ.O(("layer",e is "fire" or "water" or "earth"?1.12:.94),("recycle",e is "wind" or "poison"?1.08:.98),("protection",e is "fire" or "water" or "earth"?1.05:.98)))));
    public static readonly Dictionary<string,int> Sizes=new(){["fire"]=10,["water"]=12,["earth"]=14,["wind"]=10,["poison"]=14};
    private static readonly Dictionary<string,Dictionary<string,double>> Profiles=new()
    {
        ["fire"]=new(){["recycle"]=1.25,["draw"]=1.1,["passive"]=1.1},["water"]=new(){["layer"]=1.2,["protection"]=1.2,["future"]=1.1},
        ["earth"]=new(){["protection"]=1.35,["passive"]=1.15,["panel"]=1.1},["wind"]=new(){["draw"]=1.15,["fee"]=1.15,["tempo"]=1.2,["lookahead"]=1.15},["poison"]=new(){["layer"]=1.3,["protection"]=1.2,["passive"]=1.15}
    };
    public static JsonObject Element(JsonObject source,string element)
    {
        var c=Neutral(source);c["element_intent"]=element;c["checkpoint"]=element+"-style-initial";
        foreach(var(k,v)in Profiles[element])c["weights"]![k]=c["weights"].N(k)*v;
        c["construction"]!["first_layer"]=c["construction"].N("first_layer")*(element is "water" or "poison"?1.2:1.05);
        if(element is "water" or "earth" or "poison")c["construction"]!["dilution"]=c["construction"].N("dilution")*.9;
        c["size_exploration"]=TJ.O(("bonus",0d),("ceiling",Sizes[element]+4));return c;
    }
    public static JsonObject Neutral(JsonObject source)
    {var c=TJ.Clone(source);c.Remove("breadth");c.Remove("role_preferences");c["checkpoint"]="outcome-initial-neutral";return c;}
    public static JsonObject Explorer(JsonObject source)=>TJ.O(("kind","explorer"),("checkpoint","explore-initial"),("weights",source["weights"]),("construction",ExplorerInitial),("element_weights",ElementInitial()));
    public static JsonObject Medium(JsonObject small)
    {
        var c=TJ.Clone(small);c["checkpoint"]="medium-restart";
        foreach(var(k,v)in new Dictionary<string,double>{["dilution"]=.84,["buy_margin"]=.2,["delete_margin"]=2.4,["starter_bias"]=.6,["first_layer"]=3.1,["core_weight"]=2.4})c["construction"]![k]=v;
        c["breadth"]=TJ.O(("target",15d),("fill_bonus",4.5),("retention_bonus",4d),("paid_delete_penalty",6d),("quality_floor",.7));return c;
    }
    private static double Rounded(double value)
    {
        // Python rounds the exact binary input to the nearest decimal multiple.
        // Scaling a double then Math.Round can turn a near tie into an exact tie.
        var scaled=Q.Abs(Q.FromDouble(value))*new Q(1000000);
        var quotient=System.Numerics.BigInteger.DivRem(scaled.numerator,scaled.denominator,out var remainder);
        int comparison=(2*remainder).CompareTo(scaled.denominator);
        if(comparison>0||comparison==0&&!quotient.IsEven)quotient++;
        return (double)new Q(value<0?-quotient:quotient,1000000);
    }
    public static JsonObject Propose(string mode,JsonObject best,JsonObject initial,int step,IReadOnlyDictionary<string,double> direction,int seed=0,string family="small")
    {
        var c=TJ.Clone(best);c["checkpoint"]=(mode=="mechanism"?"candidate":mode=="free"?"explore-candidate":mode=="dual"?"dual-"+family+"-candidate":mode+"-candidate")+"-"+step.ToString("00");
        if(mode is "mechanism" or "thin")
        {
            double norm=direction.Values.Select(Math.Abs).DefaultIfEmpty(0).Max();if(norm==0)norm=1;
            string section=mode=="thin"?"construction":"weights";foreach(string key in c[section]!.AsObject().Select(x=>x.Key).ToArray())
            {double anchor=initial[section].N(key),v=c[section].N(key)*(1+(mode=="thin"?.03:.05)*direction.GetValueOrDefault(key)/norm);double lo=mode=="thin"&&key=="target"?14:anchor*(mode=="thin"?.6:.65);double hi=mode=="thin"&&key=="target"?22:anchor*(mode=="thin"?1.8:1.5);c[section]![key]=Rounded(Math.Clamp(v,lo,hi));}return c;
        }
        var rng=new PythonRandom(mode switch{"free"=>9100+step,"medium"=>14000+step,"dual"=>22000+step+(family=="medium"?0:100)+seed,_=>seed+9871+step});
        int Sign()=>rng.choice(new[]{-1,1});
        if(mode=="medium")
        {
            foreach(string k in c["construction"]!.AsObject().Select(x=>x.Key).ToArray())
            {int fallback=Sign();double signal=direction.GetValueOrDefault("c."+k,fallback),delta=.15*(.6*signal+.4*Sign()),anchor=initial["construction"].N(k);c["construction"]![k]=Rounded(Math.Clamp(c["construction"].N(k)*(1+delta),.65*anchor,1.5*anchor));}
            foreach(string k in new[]{"fill_bonus","retention_bonus","paid_delete_penalty","quality_floor"}){double anchor=initial["breadth"].N(k);c["breadth"]![k]=Rounded(Math.Clamp(c["breadth"].N(k)*(1+.12*Sign()),.75*anchor,1.25*anchor));}return c;
        }
        foreach(var(section,prefix,rate)in new[]{("weights","w.",mode=="free"?.04:mode=="dual"?.06:.07),("construction","c.",mode=="free"?.04:.10)})
        foreach(string key in c[section]!.AsObject().Select(x=>x.Key).ToArray())
        {
            double fallback=mode is "free" or "dual"?Sign():0,signal=direction.GetValueOrDefault(prefix+key,fallback),anchor=initial[section].N(key);
            double change=rate*((mode=="free"?.7:.6)*signal+(mode=="free"?.3:.4)*Sign());double lower=mode=="free"?.55:mode=="dual"?.6:.65,upper=mode=="free"?1.8:mode=="dual"?1.6:1.5;
            c[section]![key]=Rounded(Math.Clamp(c[section].N(key)*(1+change),lower*anchor,upper*anchor));
        }
        if(mode=="dual")
        {
            if(c["breadth"] is JsonObject)foreach(string key in new[]{"fill_bonus","retention_bonus","paid_delete_penalty","quality_floor"}){double anchor=initial["breadth"].N(key);c["breadth"]![key]=Rounded(Math.Clamp(c["breadth"].N(key)*(1+.08*Sign()),.75*anchor,1.25*anchor));}
            if(c["role_preferences"] is JsonObject)foreach(string key in c["role_preferences"]!.AsObject().Select(x=>x.Key).ToArray()){double anchor=initial["role_preferences"].N(key);c["role_preferences"]![key]=Rounded(Math.Clamp(c["role_preferences"].N(key)*(1+.03*Sign()),.94*anchor,1.06*anchor));}return c;
        }
        foreach(var(e,weights)in c["element_weights"]!.AsObject())foreach(string key in weights!.AsObject().Select(x=>x.Key).ToArray())
        {double anchor=initial["element_weights"]![e].N(key),signal=direction.GetValueOrDefault(e+"."+key);double factor=mode=="free"?.015:.025,lo=mode=="free"?.85:.9,hi=mode=="free"?1.15:1.1;c["element_weights"]![e]![key]=Rounded(Math.Clamp(weights.N(key)*(1+factor*signal),lo*anchor,hi*anchor));}return c;
    }
}
