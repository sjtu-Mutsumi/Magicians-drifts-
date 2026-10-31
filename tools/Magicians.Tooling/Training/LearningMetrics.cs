using System.Text.Json.Nodes;
using Magicians.Native;

namespace Magicians.Tooling;

public sealed record LearningRow(JsonObject Player,Dictionary<string,double> Features,double Reward);

public static class LearningMetrics
{
    public static readonly Dictionary<string,double> InitialWeights=new(StringComparer.Ordinal){["panel"]=.36,["layer"]=2.1,["draw"]=3.6,["fee"]=5,["search"]=1.9,["recycle"]=2.4,["discard"]=.8,["future"]=1.6,["passive"]=2,["tempo"]=1.6,["protection"]=.8,["thinning"]=1,["lookahead"]=1};
    public static JsonObject Group(IEnumerable<JsonObject> source,bool first=true)
    {
        double score=0,people=0,champions=0,battles=0,wins=0;var times=new List<double>();var flow=TJ.Counter();
        foreach(var room in source)
        {
            var seats=TJ.Seats(room,first);
            foreach(var p in room["placements"].Rows().Where(p=>seats.Contains(p.I("pid")))){people++;score+=p.N("score");if(p.I("rank")==1)champions++;}
            foreach(var b in TJ.Battles(room))foreach(var (i,p) in TJ.Players(b))
            {if(!seats.Contains(p.I("pid")))continue;battles++;wins+=TJ.Win(b,i);foreach(var (k,v) in TJ.Counter(b["flow_by_seat"]![i]))flow.Inc(k,v);if(TJ.Winner(b)==i)times.Add(b.N("time"));}
        }
        return TJ.O(("players",people),("mean_score",TJ.Div(score,people)),("championship_rate",TJ.Div(champions,people)),("battle_count",battles),("win_rate",TJ.Div(wins,battles)),("winner_time",TJ.Div(times.Sum(),times.Count)),
            ("plays_per_battle",TJ.Div(flow.GetValueOrDefault("played"),battles)),("instant_draw_per_battle",TJ.Div(flow.GetValueOrDefault("instant_drawn"),battles)),("immediate_fee_per_battle",TJ.Div(flow.GetValueOrDefault("instant_fee"),battles)),
            ("discard_per_battle",TJ.Div(flow.GetValueOrDefault("discarded"),battles)),("shuffle_per_battle",TJ.Div(flow.GetValueOrDefault("shuffles"),battles)),("unused_fee_per_stage",TJ.Div(flow.GetValueOrDefault("unused_energy"),flow.GetValueOrDefault("stages"))));
    }
    public static JsonObject Interval(IEnumerable<double> source)
    {var xs=source.ToArray();double m=TJ.Div(xs.Sum(),xs.Length),se=Math.Sqrt(TJ.Div(TJ.Div(xs.Sum(x=>(x-m)*(x-m)),xs.Length-1),xs.Length));return TJ.O(("games",xs.Length),("mean",m),("low",m-1.96*se),("high",m+1.96*se));}
    public static JsonObject PairedScore(IEnumerable<JsonObject> source)
    {
        var rooms=source.ToArray();if(rooms.Length==0)return TJ.O(("mean",null),("low",null),("high",null),("games",0));
        return Interval(rooms.Select(r=>{var seats=TJ.Seats(r);return r["placements"].Rows().Sum(p=>p.N("score")*(seats.Contains(p.I("pid"))?1:-1))/2;}));
    }
    public static JsonObject PairedWin(IEnumerable<JsonObject> source)=>Interval(source.Select(room=>
    {var seats=TJ.Seats(room);double[] n={0,0},w={0,0};foreach(var b in TJ.Battles(room))foreach(var (i,p) in TJ.Players(b)){int side=seats.Contains(p.I("pid"))?0:1;n[side]++;w[side]+=TJ.Win(b,i);}return TJ.Div(w[0],n[0])-TJ.Div(w[1],n[1]);}));
    public static double FocalScore(JsonObject room)
    {var g=Group(new[]{room});return(g.N("championship_rate")+g.N("win_rate")+(g.N("mean_score")+3)/6)/3;}
    public static JsonObject PairedEpisodes(IEnumerable<JsonObject> source)
    {
        return Interval(source.GroupBy(r=>r.N("seed")).Select(g=>
        {var rows=g.ToArray();if(rows.Length!=2||rows.Select(r=>r.S("pair_side")).ToHashSet().Count!=2)throw new InvalidDataException("Incomplete paired seed "+g.Key);return FocalScore(rows.Single(r=>r.S("pair_side")=="candidate"))-FocalScore(rows.Single(r=>r.S("pair_side")=="control"));}));
    }
    public static JsonObject Objective(IEnumerable<JsonObject> source)
    {
        var champ=new List<double>();var wins=new List<double>();var score=new List<double>();var composite=new List<double>();
        foreach(var room in source){var a=Group(new[]{room});var b=Group(new[]{room},false);double c=a.N("championship_rate")-b.N("championship_rate"),w=a.N("win_rate")-b.N("win_rate"),s=(a.N("mean_score")-b.N("mean_score"))/6;champ.Add(c);wins.Add(w);score.Add(s);composite.Add((c+w+s)/3);}
        return TJ.O(("composite",Interval(composite)),("championship",Interval(champ)),("win",Interval(wins)),("normalized_score",Interval(score)));
    }
    public static JsonObject Stages(IEnumerable<JsonObject> source)
    {
        var result=new JsonObject();foreach(var (name,lo,hi) in new[]{("1-4",1,4),("5-8",5,8),("9-12",9,12),("13+",13,100)})
        {var rs=source.Select(r=>{var n=TJ.Clone(r);n["rounds"]=TJ.A(r["rounds"].Rows().Where(rd=>rd.I("round")>=lo&&rd.I("round")<=hi));return n;}).Where(r=>r["rounds"]!.AsArray().Count>0).ToArray();result[name]=TJ.O(("selected",Group(rs)),("control",Group(rs,false)),("paired_win",PairedWin(rs)));}return result;
    }
    public static JsonObject DeckMetrics(IEnumerable<JsonObject> source,bool first=true)
    {
        var rows=new List<(double size,double repeatable,double generic)>();double buy=0,del=0,paid=0;
        foreach(var r in source)
        {var seats=TJ.Seats(r,first);foreach(var p in r["placements"].Rows().Where(p=>seats.Contains(p.I("pid")))){var names=p["deck"].Strings();rows.Add((Math.Max(1,names.Length),names.Count(n=>!Catalog.CARDS[n].ability),TJ.Div(names.Count(n=>Catalog.CARDS[n].element=="generic"),names.Length)));}
            foreach(var a in r["rounds"].Rows().SelectMany(rd=>rd["ledger"].Rows()).Where(row=>seats.Contains(row.I("pid"))).SelectMany(row=>row["actions"].Rows())){if(a.S("kind")=="buy")buy++;if(a.S("kind")=="delete"){del++;if(a.N("cost")>0)paid++;}}}
        return TJ.O(("players",rows.Count),("deck_size",TJ.Div(rows.Sum(x=>x.size),rows.Count)),("repeatable_size",TJ.Div(rows.Sum(x=>x.repeatable),rows.Count)),("generic_fraction",TJ.Div(rows.Sum(x=>x.generic),rows.Count)),("purchases_per_player",TJ.Div(buy,rows.Count)),("deletions_per_player",TJ.Div(del,rows.Count)),("paid_deletions_per_player",TJ.Div(paid,rows.Count)));
    }
    public static JsonObject Describe(IEnumerable<JsonObject> source,bool first=true)
    {
        var rows=new List<(string element,int size,double flow,int layers)>();var actions=TJ.Counter();var rounds=new Dictionary<int,List<int>>();
        foreach(var r in source)
        {
            var seats=TJ.Seats(r,first);
            foreach(var rd in r["rounds"].Rows())foreach(var row in rd["ledger"].Rows().Where(row=>seats.Contains(row.I("pid"))))
            {foreach(var a in row["actions"].Rows())actions.Inc(a.S("kind"));if(!rounds.TryGetValue(rd.I("round"),out var list))rounds[rd.I("round")]=list=new();list.Add(row["deck"]!.AsArray().Count);}
            foreach(var p in r["placements"].Rows().Where(p=>seats.Contains(p.I("pid"))))
            {var names=p["deck"].Strings();double flow=TJ.Div(names.Count(n=>new[]{"draw","fee","search","discard","shuffle","recover"}.Any(k=>Mechanics.static_features(n)[k]!=0)),names.Length);rows.Add((p.S("spec"),names.Length,flow,names.Where(n=>Catalog.CARDS[n].element==p.S("spec")).Sum(n=>Catalog.CARDS[n].layers)));}
        }
        JsonObject Metrics(IEnumerable<(string element,int size,double flow,int layers)> sourceRows)
        {var rs=sourceRows.ToArray();var ns=rs.Select(x=>x.size).Order().ToArray();return TJ.O(("players",rs.Length),("mean_size",TJ.Div(ns.Sum(),ns.Length)),("median",ns.Length>0?ns[ns.Length/2]:null),("minimum",ns.Length>0?ns[0]:null),("maximum",ns.Length>0?ns[^1]:null),("histogram",ns.GroupBy(n=>n).ToDictionary(g=>g.Key.ToString(),g=>g.Count())),("flow_fraction",TJ.Div(rs.Sum(x=>x.flow),rs.Length)),("main_first_layers",TJ.Div(rs.Sum(x=>x.layers),rs.Length)));}
        var result=Metrics(rows);result["elements"]=new JsonObject(Catalog.ELEMENTS.Select(e=>new KeyValuePair<string,JsonNode?>(e,Metrics(rows.Where(x=>x.element==e)))));result["purchases_per_player"]=TJ.Div(actions.GetValueOrDefault("buy"),rows.Count);result["deletions_per_player"]=TJ.Div(actions.GetValueOrDefault("delete"),rows.Count);result["round_mean_size"]=NativeJson.Node(rounds.ToDictionary(x=>x.Key.ToString(),x=>x.Value.Average()));return result;
    }
    public static Dictionary<string,double> Features(JsonObject player,Dictionary<string,double> casts,double battles)
    {
        var names=player["deck"].Strings();var vector=InitialWeights.Keys.ToDictionary(k=>k,_=>0d);
        foreach(string name in names)
        {
            var c=Catalog.CARDS[name];var f=Mechanics.static_features(name);double blend=.5/Math.Max(1,names.Length)+.5*casts.GetValueOrDefault(name)/Math.Max(1,battles*20);
            foreach(string key in InitialWeights.Keys)
            {double value=key switch{"panel"=>c.attack+1.5*c.defense,"layer"=>c.layers,"recycle"=>f["shuffle"]+f["recover"],"future"=>f["future_draw"]+1.3*f["future_fee"],"protection"=>f["shield"]+f["heal"],"thinning"=>f["discard"]/Math.Max(1,c.cost+1),"lookahead"=>f["draw"]*Math.Max(0,f["fee"]+1-c.cost/2d)+f["search"]*.5,"tempo"=>f["delay"],_=>f.GetValueOrDefault(key)};vector[key]+=value*blend;}
        }return vector;
    }
    public static List<LearningRow> Rows(IEnumerable<JsonObject> source,bool outcome=false)
    {
        var rows=new List<LearningRow>();foreach(var r in source)
        {
            var casts=Enumerable.Range(0,4).ToDictionary(i=>i,_=>TJ.Counter());var played=new double[4];var wins=new double[4];var del=new double[4];var buy=new double[4];
            foreach(var rd in r["rounds"].Rows())
            {foreach(var l in rd["ledger"].Rows())foreach(var a in l["actions"].Rows()){if(a.S("kind")=="buy")buy[l.I("pid")]++;if(a.S("kind")=="delete")del[l.I("pid")]++;}
                foreach(var b in rd["battles"].Rows())foreach(var(i,p) in TJ.Players(b)){int pid=p.I("pid");played[pid]++;wins[pid]+=TJ.Win(b,i);foreach(var(k,v)in TJ.Counter(b["executed_by_seat"]![i]))casts[pid].Inc(k,v);}}
            foreach(var p in r["placements"].Rows())
            {int pid=p.I("pid");var names=p["deck"].Strings();var f=Features(p,casts[pid],played[pid]);var x=f.ToDictionary(kv=>"w."+kv.Key,kv=>kv.Value);
                foreach(var(k,v)in new Dictionary<string,double>{["dilution"]=-Math.Max(1,names.Length),["buy_margin"]=-buy[pid],["point_cost"]=-buy[pid],["delete_margin"]=-del[pid],["starter_bias"]=del[pid],["core_weight"]=f["passive"],["first_layer"]=TJ.Div(names.Sum(n=>Catalog.CARDS[n].layers),names.Length),["generic_tax"]=-TJ.Div(names.Count(n=>Catalog.CARDS[n].element=="generic"),names.Length),["flow_scale"]=f["draw"]+f["fee"]+f["recycle"]})x["c."+k]=v;
                double win=TJ.Div(wins[pid],played[pid]);rows.Add(new(p,x,outcome?((p.I("rank")==1?1:0)+win+(p.N("score")+3)/6)/3:win));}
        }return rows;
    }
    public static double Correlation(IEnumerable<LearningRow> source,string key,string? element=null,int minimum=30,double elementShrink=.6,double styleShrink=.25)
    {
        var rows=source.Where(r=>element is null||r.Player.S("spec")==element).ToArray();if(rows.Length<minimum)return 0;
        var em=rows.GroupBy(r=>r.Player.S("spec")).ToDictionary(g=>g.Key,g=>g.Average(r=>r.Reward));var sm=rows.GroupBy(r=>r.Player.S("style")).ToDictionary(g=>g.Key,g=>g.Average(r=>r.Reward));
        var xs=rows.Select(r=>r.Features.GetValueOrDefault(key)).ToArray();var ys=rows.Select(r=>r.Reward-elementShrink*em[r.Player.S("spec")]-styleShrink*sm[r.Player.S("style")]).ToArray();double xm=xs.Average(),ym=ys.Average();double cov=xs.Zip(ys,(x,y)=>(x-xm)*(y-ym)).Sum();return cov/Math.Max(1e-9,Math.Sqrt(xs.Sum(x=>(x-xm)*(x-xm))*ys.Sum(y=>(y-ym)*(y-ym))));
    }
    public static Dictionary<string,double> MechanismGradient(IEnumerable<JsonObject> source)
    {
        var rows=new List<LearningRow>();foreach(var r in source)
        {var casts=Enumerable.Range(0,4).ToDictionary(i=>i,_=>TJ.Counter());var battles=new double[4];var wins=new double[4];var times=Enumerable.Range(0,4).ToDictionary(i=>i,_=>new List<double>());
            foreach(var b in TJ.Battles(r))foreach(var(i,p)in TJ.Players(b)){int pid=p.I("pid");battles[pid]++;foreach(var(k,v)in TJ.Counter(b["executed_by_seat"]![i]))casts[pid].Inc(k,v);if(TJ.Winner(b)==i){wins[pid]++;times[pid].Add(b.N("time"));}}
            foreach(var p in r["placements"].Rows()){int pid=p.I("pid");double tempo=TJ.Div(times[pid].Sum(t=>Math.Max(0,1-Math.Min(t,2000)/2000)),times[pid].Count);rows.Add(new(p,Features(p,casts[pid],battles[pid]),p.N("score")+.15*TJ.Div(wins[pid],battles[pid])+.1*tempo));}}
        return InitialWeights.Keys.ToDictionary(k=>k,k=>Correlation(rows,k,minimum:1,elementShrink:.5,styleShrink:.3));
    }
    public static Dictionary<string,double> ThinGradient(IEnumerable<JsonObject> source)
    {
        var rows=new List<LearningRow>();foreach(var r in source)
        {
            var del=new double[4];var bought=new double[4];var basic=new double[4];var stages=new double[4];var casts=Enumerable.Range(0,4).ToDictionary(i=>i,_=>TJ.Counter());var flows=Enumerable.Range(0,4).ToDictionary(i=>i,_=>TJ.Counter());
            foreach(var rd in r["rounds"].Rows())
            {foreach(var l in rd["ledger"].Rows()){int pid=l.I("pid");stages[pid]++;foreach(var a in l["actions"].Rows()){if(a.S("kind")=="buy")bought[pid]++;if(a.S("kind")=="delete"){del[pid]++;if(Catalog.START_ONLY.Contains(a.S("name")))basic[pid]++;}}}
                foreach(var b in rd["battles"].Rows())foreach(var(i,p)in TJ.Players(b)){foreach(var(k,v)in TJ.Counter(b["executed_by_seat"]![i]))casts[p.I("pid")].Inc(k,v);foreach(var(k,v)in TJ.Counter(b["flow_by_seat"]![i]))flows[p.I("pid")].Inc(k,v);}}
            foreach(var p in r["placements"].Rows())
            {int pid=p.I("pid");var names=p["deck"].Strings();var f=flows[pid];var x=new Dictionary<string,double>{["target"]=Math.Max(1,names.Length),["dilution"]=-Math.Max(1,names.Length),["buy_margin"]=-TJ.Div(bought[pid],stages[pid]),["point_cost"]=-TJ.Div(bought[pid],stages[pid]),["delete_margin"]=-TJ.Div(del[pid],stages[pid]),["delete_pressure"]=TJ.Div(del[pid],stages[pid]),["starter_bias"]=TJ.Div(basic[pid],stages[pid]),["core_weight"]=TJ.Div(casts[pid].Where(x=>Construction.REPEAT.ContainsKey(x.Key)).Sum(x=>x.Value),casts[pid].Values.Sum()),["first_layer"]=TJ.Div(names.Sum(n=>Catalog.CARDS[n].layers),names.Length),["generic_tax"]=-TJ.Div(names.Count(n=>Catalog.CARDS[n].element=="generic"),names.Length),["flow_scale"]=TJ.Div(f.GetValueOrDefault("played"),5*f.GetValueOrDefault("stages")+f.GetValueOrDefault("instant_drawn"))};rows.Add(new(p,x,p.N("score")));}
        }return Construction.THIN_INITIAL.Keys.ToDictionary(k=>k,k=>Correlation(rows,k,minimum:1,elementShrink:.5,styleShrink:.3));
    }
}
