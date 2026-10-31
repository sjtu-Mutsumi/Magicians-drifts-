using System.Text.Json;
using System.Text.Json.Nodes;
using Magicians.Native;
namespace Magicians.Tooling;

internal static class PolicyRegression
{
    public static int Run(string root)
    {
        var count=0;
        void Check(bool value,string label){if(!value)throw new InvalidDataException(label);count++;}
        double N(JsonNode? n)=>n!.GetValue<double>();
        JsonObject Clone(JsonObject o)=>o.DeepClone().AsObject();
        var source=JsonNode.Parse(File.ReadAllBytes(Path.Combine(root,"tools/sim_v12/fixtures/small-policy.json")))!.AsObject();
        var basic=TrainingProposals.Explorer(source);
        basic["role_preferences"]=JsonSerializer.SerializeToNode(new{numeric=.98,strong=1.08,weak=1.02});
        var neutral=TrainingProposals.Neutral(basic);
        Check(!neutral.ContainsKey("role_preferences")&&!neutral.ContainsKey("breadth"),"neutral removes role and size preferences");
        Check(basic.ContainsKey("role_preferences"),"neutral does not mutate source");
        foreach(var key in new[]{"weights","construction","element_weights"})Check(JsonNode.DeepEquals(neutral[key],basic[key]),"neutral preserves "+key);
        var best=Clone(neutral);
        for(var step=1;step<=10;step++)best=TrainingProposals.Propose("outcome",best,neutral,step,new Dictionary<string,double>(),296000);
        foreach(var section in new[]{"weights","construction"})foreach(var(k,v)in best[section]!.AsObject())
            Check(N(v)>=.65*N(neutral[section]![k])-.000001&&N(v)<=1.5*N(neutral[section]![k])+.000001,"bounded outcome update "+section+"/"+k);
        Check(!best.ContainsKey("role_preferences")&&!best.ContainsKey("breadth"),"updates do not reintroduce preferences");
        var saved=basic.ToJsonString();var fire=TrainingProposals.Element(basic,"fire");var earth=TrainingProposals.Element(basic,"earth");
        Check(N(fire["weights"]!["recycle"])>N(basic["weights"]!["recycle"]),"fire independent profile");
        Check(N(earth["weights"]!["protection"])>N(basic["weights"]!["protection"]),"earth independent profile");
        fire["weights"]!["panel"]=999;
        Check(N(earth["weights"]!["panel"])!=999&&basic.ToJsonString()==saved,"deep independent profile copies");
        var player=new Player(0,"water"){spec="water",points=3,deck=new[]{"潮汐之心","生命之泉","深渊","奔流","活水","泉眼","灵感涌现","护盾"}.Select(n=>(n,(string?)null)).ToList()};
        void Policy(JsonObject cfg)=>player.policy=JsonSerializer.SerializeToElement(cfg);
        var cfg=TrainingProposals.Explorer(source);Policy(cfg);
        Check(!new[]{"target","min_size","max_size"}.Any(cfg["construction"]!.AsObject().ContainsKey),"no artificial size cap");
        double Purchase(string name="涌泉",int cost=2)=>Exploration.purchase(player,name,cost,null,new());
        double Delete(int fee=0)=>Exploration.deletion(player,"泉眼",fee);
        var original=(Purchase(),Delete(),Exploration.refresh(player,8,new(),new()));
        foreach(var target in new[]{8,12,15,18,1000})
        {cfg["construction"]!["target"]=target;Policy(cfg);Check(original==(Purchase(),Delete(),Exploration.refresh(player,8,new(),new())),"arbitrary target ignored "+target);}
        cfg["construction"]!.AsObject().Remove("target");Policy(cfg);
        var before=cfg.ToJsonString();_ = PolicyValue(player,"涌泉");Check(cfg.ToJsonString()==before,"effective weights do not mutate model");
        Check(Purchase(cost:0)>Purchase(cost:2),"purchase point opportunity cost");Check(Delete(0)>Delete(1),"delete fee opportunity cost");
        var buy=Purchase();var del=Delete(1);var medium=TrainingProposals.Medium(cfg);Policy(medium);
        Check(Purchase()>buy&&Delete(1)<del,"medium restart useful purchase and saves paid deletes");
        Check(N(medium["breadth"]!["target"])==15&&!medium["breadth"]!.AsObject().ContainsKey("max_size")&&!medium["breadth"]!.AsObject().ContainsKey("min_size"),"soft medium breadth");
        Check(JsonNode.DeepEquals(medium["weights"],cfg["weights"])&&JsonNode.DeepEquals(medium["element_weights"],cfg["element_weights"]),"medium freezes combat weights");
        Policy(cfg);var numeric=Exploration.quality(player,"泉眼");var flow=Exploration.quality(player,"活水");var weights=cfg["weights"]!.ToJsonString();
        cfg["role_preferences"]=JsonSerializer.SerializeToNode(new{numeric=1.15,strong=.85,weak=1.0});Policy(cfg);
        Check(Exploration.quality(player,"泉眼")>numeric&&Exploration.quality(player,"活水")<flow,"soft role preference affects construction");Check(cfg["weights"]!.ToJsonString()==weights,"role preference keeps combat weights");
        var water=TrainingProposals.Element(basic,"water");player.spec="";Policy(water);
        Check(Exploration.quality(player,"战意")==-100,"element intent rejects unrelated cards");var route=Exploration.quality(player,"血潮");water["route_intent"]="血潮";Policy(water);Check(Math.Abs(Exploration.quality(player,"血潮")-route-8)<1e-10,"route intent soft bonus");
        player.spec="water";water.AsObject().Remove("route_intent");Policy(water);var oldBuy=Purchase();var oldDelete=Construction.deletion_value(player,"攻击强化",1);
        water["size_exploration"]!["bonus"]=6;Policy(water);
        Check(Purchase()>oldBuy&&Construction.deletion_value(player,"攻击强化",1)<oldDelete,"larger-deck bonus affects buy/delete softly");Check(!water["size_exploration"]!.AsObject().ContainsKey("max_size"),"no hard cap in size experiment");
        var room=JsonNode.Parse("{\"seed\":1,\"pair_side\":\"candidate\",\"comparison\":{\"first_seats\":[0]},\"rounds\":[],\"placements\":[{\"pid\":0,\"rank\":1,\"score\":3},{\"pid\":1,\"rank\":2,\"score\":1},{\"pid\":2,\"rank\":3,\"score\":-1},{\"pid\":3,\"rank\":4,\"score\":-3}]}")!.AsObject();
        var control=Clone(room);control["pair_side"]="control";
        Check(N(LearningMetrics.PairedEpisodes(new[]{room,control})["mean"])==0,"identical focal outcomes zero paired delta");
        control["placements"]![0]!["rank"]=4;control["placements"]![0]!["score"]=-3;
        Check(Math.Abs(N(LearningMetrics.PairedEpisodes(new[]{room,control})["mean"])-2d/3)<1e-12,"paired focal outcome delta");
        room["pair_side"]="control";control["pair_side"]="candidate";
        Check(Math.Abs(N(LearningMetrics.PairedEpisodes(new[]{room,control})["mean"])+2d/3)<1e-12,"paired sign reversal");
        try{LearningMetrics.PairedEpisodes(new[]{room});throw new InvalidDataException("Missing pair accepted");}catch(InvalidDataException e)when(e.Message!="Missing pair accepted"){count++;}
        room["comparison"]!["first_seats"]=new JsonArray(0,1);var objective=N(LearningMetrics.Objective(new[]{room})["composite"]!["mean"]);
        Check(Math.Abs(objective-(.5+4d/6)/3)<1e-12,"equal-scale objective components");
        room["comparison"]!["first_seats"]=new JsonArray(2,3);
        Check(Math.Abs(N(LearningMetrics.Objective(new[]{room})["composite"]!["mean"])+objective)<1e-12,"objective seat sign reversal");
        return count;
    }
    private static double PolicyValue(Player p,string n)=>Policy.draft_value(p,n,new());
}
