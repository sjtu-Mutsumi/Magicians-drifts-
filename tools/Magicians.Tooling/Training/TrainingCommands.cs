using System.Text.Json;
using System.Text.Json.Nodes;
using Magicians.Native;

namespace Magicians.Tooling;

public static class TrainingCommands
{
    public static readonly string[] Commands={"sim","quick-test","analyze","cohort","train","train-mechanism","train-thin","train-medium","train-free","train-dual","train-outcome","train-element","train-element-paired","train-population","train-six","training-probe","training-selftest"};
    public static int Run(string command,string root,string[] args)
    {
        var a=new TrainingArgs(args);foreach(string n in Catalog.CARDS.Keys)_=Mechanics.static_features(n);_=Policy.TARGETS;
        if(command=="training-selftest")
        {var report=SelfTest(root);if(a.Get("out") is string path)TJ.Save(path,report);Console.WriteLine(report.ToJsonString());return 0;}
        if(command=="training-probe")
        {
            var input=TJ.Read(a.Required("input"));var result=Probe(input);TJ.Save(a.Required("out"),result);return 0;
        }
        if(command is "analyze" or "cohort")
        {
            var stats=new TrainingStatistics();var rooms=ReadRooms(a.Required("input")).ToArray();foreach(var room in rooms)stats.Add(room);
            var result=stats.Result(command=="cohort");TJ.Save(a.Required("out"),result);return 0;
        }
        if(command is "sim" or "quick-test")
        {
            string outDir=NewOutput(a.Required("out"));int games=a.Int("games",command=="quick-test"?500:5),seed=a.Int("seed",command=="quick-test"?281200:1200),workers=a.Int("workers",1);
            if(games<=0||workers<=0)throw new ArgumentException("games/workers must be positive");var hashes=Inputs(root);TJ.Save(Path.Combine(outDir,"inputs.json"),TJ.O(("games",games),("seed",seed),("workers",workers),("sequence_rule","locked"),("sha256",hashes)));
            var configs=a.Get("first") is string f?TJ.Read(f):null;var second=a.Get("second") is string s?TJ.Read(s):configs;
            var jobs=Enumerable.Range(0,games).Select(i=>new TrainingJob(seed+i,configs,second,Path.Combine(outDir,"rooms"),"simulation",Trace:a.Has("trace"))).ToList();
            var issues=new List<JsonObject>();var rooms=Collect(jobs,workers,issues);VerifyInputs(root,hashes);SaveSummary(outDir,rooms,issues);TJ.Save(Path.Combine(outDir,"target-decks.json"),Policy.TARGETS);return issues.Count==0?0:1;
        }
        string mode=command.StartsWith("train-",StringComparison.Ordinal)?command[6..]:a.Get("mode")??"six";
        var run=new TrainingRun(root,mode,a);run.Run();return 0;
    }
    public static JsonObject Probe(JsonObject input)
    {
        var result=new JsonObject();var rooms=input["rooms"].Rows().ToArray();
        if(input["rooms"] is not null)
        {
            var stats=new TrainingStatistics();foreach(var r in rooms)stats.Add(r);result["statistics"]=stats.Result();result["cohort"]=stats.Result(true);
            result["group"]=LearningMetrics.Group(rooms);result["group_control"]=LearningMetrics.Group(rooms,false);result["paired_score"]=LearningMetrics.PairedScore(rooms);result["paired_win"]=LearningMetrics.PairedWin(rooms);result["objective"]=LearningMetrics.Objective(rooms);result["stages"]=LearningMetrics.Stages(rooms);result["describe"]=LearningMetrics.Describe(rooms);result["deck_metrics"]=LearningMetrics.DeckMetrics(rooms);
            result["mechanism_gradient"]=NativeJson.Node(LearningMetrics.MechanismGradient(rooms));result["thin_gradient"]=NativeJson.Node(LearningMetrics.ThinGradient(rooms));
            var rows=LearningMetrics.Rows(rooms);result["training_rows"]=TJ.A(rows.Select(r=>new object?[]{r.Player,r.Features,r.Reward}));result["reward_rows"]=TJ.A(LearningMetrics.Rows(rooms,true).Select(r=>new object?[]{r.Player,r.Features,r.Reward}));
        }
        if(input["proposals"] is JsonArray proposals)
        {var values=new JsonArray();foreach(var p in proposals.OfType<JsonObject>())values.Add(TrainingProposals.Propose(p.S("mode"),p["best"]!.AsObject(),p["initial"]!.AsObject(),p.I("step"),TJ.Counter(p["direction"]),p.I("seed"),p.S("family","small")));result["proposals"]=values;}
        if(input["jobs"] is JsonArray jobs){var values=new JsonArray();foreach(var j in jobs.OfType<JsonObject>())values.Add(Room(new TrainingJob(j.I("seed"),j["first"]?.AsObject(),j["second"]?.AsObject(),"","probe",j.S("element"),j.S("mode"),j.I("route"),j["bank"]?.AsObject(),j.S("side"),j.S("owner"),j.B("six"))));result["jobs"]=values;}
        return result;
    }
    public static JsonObject SelfTest(string root)
    {
        foreach(string n in Catalog.CARDS.Keys)_=Mechanics.static_features(n);
        var fixture=TJ.Read(Path.Combine(root,"tools/Magicians.Tooling/Training/Fixtures/parity.json"));var expected=fixture["expected"]!;var actual=Probe(fixture["input"]!.AsObject());int checks=0;
        void Compare(JsonNode? e,JsonNode? a,string path)
        {
            checks++;
            if(e is JsonObject eo){if(a is not JsonObject ao||!eo.Select(x=>x.Key).ToHashSet(StringComparer.Ordinal).SetEquals(ao.Select(x=>x.Key)))throw new InvalidDataException("Training fixture keys differ at "+path);foreach(var(k,v)in eo)Compare(v,ao[k],path+"/"+k);return;}
            if(e is JsonArray ea){if(a is not JsonArray aa||ea.Count!=aa.Count)throw new InvalidDataException("Training fixture length differs at "+path);for(int i=0;i<ea.Count;i++)Compare(ea[i],aa[i],path+"/"+i);return;}
            if(e is null||a is null){if(e is not null||a is not null)throw new InvalidDataException("Training fixture null differs at "+path);return;}
            if(e.GetValueKind()==System.Text.Json.JsonValueKind.Number&&a.GetValueKind()==System.Text.Json.JsonValueKind.Number){double x=TJ.Number(e),y=TJ.Number(a);if(Math.Abs(x-y)>1e-11*Math.Max(1,Math.Max(Math.Abs(x),Math.Abs(y))))throw new InvalidDataException("Training fixture numeric value differs at "+path);return;}
            if(!JsonNode.DeepEquals(e,a))throw new InvalidDataException("Training fixture value differs at "+path);
        }
        Compare(expected,actual,"$");return TJ.O(("status","passed"),("checks",checks),("full_tournaments",3),("proposal_cases",8),("reference_commit","2e38abc44a37214b4edd0aad6aa2ab273e171c97"),("python_required",false));
    }
    public static IEnumerable<JsonObject> ReadRooms(string path)
    {
        if(Directory.Exists(path))foreach(string f in Directory.GetFiles(path,"room-*.json*",SearchOption.AllDirectories).Order(StringComparer.Ordinal))yield return TJ.Read(f);
        else if(path.EndsWith(".gz",StringComparison.Ordinal))yield return TJ.Read(path);
        else {var bytes=File.ReadAllText(path);var n=JsonNode.Parse(bytes);if(n is JsonArray a)foreach(var r in a.OfType<JsonObject>())yield return r;else if(n is JsonObject obj&&obj["rooms"] is JsonArray rs)foreach(var r in rs.OfType<JsonObject>())yield return r;else yield return n!.AsObject();}
    }
    internal static string NewOutput(string folder)
    {string path=Path.GetFullPath(folder);if(Directory.Exists(path)&&Directory.EnumerateFileSystemEntries(path).Any())throw new IOException("Output must be new or empty: "+path);Directory.CreateDirectory(path);return path;}
    internal static Dictionary<string,string> Inputs(string root)=>new[]{"Scripts/Native","tools/Magicians.Tooling","tools/sim_v12/rules","tools/sim_v12/fixtures","docs/cards-v1.2","releases/v1.2","releases/ai-v1.2-six-styles-20261029"}
        .SelectMany(p=>Directory.Exists(Path.Combine(root,p))?Directory.EnumerateFiles(Path.Combine(root,p),"*",SearchOption.AllDirectories):Array.Empty<string>())
        .Where(p=>!p.Split(Path.DirectorySeparatorChar).Any(s=>s is "bin" or "obj" or "__pycache__")&&!p.EndsWith(".pyc",StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).ToDictionary(p=>Path.GetRelativePath(root,p).Replace('\\','/'),TJ.Hash,StringComparer.Ordinal);
    internal static void VerifyInputs(string root,Dictionary<string,string> hashes)
    {foreach(var(p,h)in hashes)if(TJ.Hash(Path.Combine(root,p))!=h)throw new InvalidDataException("Input changed while training: "+p);}
    internal static JsonObject Room(TrainingJob job)
    {
        JsonElement?[]? configs=null;var focal=new List<int>();var opponentNames=new List<string>();
        if(job.First is not null)
        {
            configs=new JsonElement?[4];
            if(job.Element=="")
            {
                var rng=new PythonRandom(job.Seed^978149);var seats=Enumerable.Range(0,4).ToList();rng.shuffle(seats);focal=seats.Take(2).Order().ToList();
                for(int i=0;i<4;i++)configs[i]=JsonSerializer.SerializeToElement(focal.Contains(i)?job.First:job.Second??job.First);
            }
            else
            {
                var rng=new PythonRandom(job.Seed^(job.Six?578471:84521));int seat=rng.randrange(4);focal.Add(seat);var others=(job.Six?SixStyles.IDS:Catalog.ELEMENTS.Where(e=>e!=job.Element)).ToList();rng.shuffle(others);
                for(int i=0;i<4;i++)
                {
                    string oe;if(job.Six){if(job.Mode=="internal")oe=job.Element;else{oe=others[^1];others.RemoveAt(others.Count-1);}}else if(job.Mode=="internal"||i==seat)oe=job.Element;else{oe=others[^1];others.RemoveAt(others.Count-1);}
                    var cfg=TJ.Clone(i==seat?job.First:job.Bank![oe]!.AsObject());string style=i==seat?job.Element:oe;
                    if(Catalog.ELEMENTS.Contains(style,StringComparer.Ordinal)){cfg["element_intent"]=style;cfg["route_intent"]=Catalog.ROUTES[style][(job.Route+(i==seat?0:i+1))%3];}
                    else {cfg.Remove("element_intent");cfg.Remove("route_intent");}
                    configs[i]=JsonSerializer.SerializeToElement(cfg);if(i!=seat)opponentNames.Add(cfg.S("checkpoint"));
                }
            }
        }
        var room=NativeJson.Node(new Tournament().simulate(job.Seed,trace:job.Trace,policy_by_seat:configs))!.AsObject();
        if(job.First is not null)
        {
            var comparison=TJ.O(("first",job.First.S("checkpoint")),("second",job.Element!=""?(job.Six?"six-style-pool":"opponent-pool"):job.Second!.S("checkpoint")),("first_seats",focal),("segment",job.Segment));
            if(job.Element!=""){comparison["element"]=job.Element;comparison["mode"]=job.Mode;comparison["route_intent"]=Catalog.ELEMENTS.Contains(job.Element)?Catalog.ROUTES[job.Element][job.Route]:"";comparison["opponents"]=NativeJson.Node(opponentNames);}
            if(job.Owner!="")comparison["budget_owner"]=job.Owner;room["comparison"]=comparison;
        }
        if(job.Side!="")room["pair_side"]=job.Side;return room;
    }
    internal static List<JsonObject> Collect(List<TrainingJob> jobs,int workers,List<JsonObject> issues)
    {
        var result=new JsonObject?[jobs.Count];var errors=new JsonObject?[jobs.Count];
        Parallel.For(0,jobs.Count,new ParallelOptions{MaxDegreeOfParallelism=workers},index=>
        {var j=jobs[index];try{var r=Room(j);if(j.Folder!="")TJ.Save(Path.Combine(j.Folder,$"room-{j.Seed}.json.gz"),r);result[index]=r;}catch(Exception ex){var error=TJ.O(("ok",false),("seed",j.Seed),("segment",j.Segment),("element",j.Element),("side",j.Side),("error",ex.GetType().Name+": "+ex.Message));errors[index]=error;if(j.Folder!="")TJ.Save(Path.Combine(j.Folder,$"error-{j.Seed}.json"),error);}});
        issues.AddRange(errors.OfType<JsonObject>());return result.OfType<JsonObject>().OrderBy(r=>r.N("seed")).ThenBy(r=>r.S("pair_side"),StringComparer.Ordinal).ToList();
    }
    internal static void SaveSummary(string folder,List<JsonObject> rooms,List<JsonObject> issues)
    {
        var stats=new TrainingStatistics();foreach(var r in rooms)stats.Add(r);var result=stats.Result();result["input_hashes_verified"]=true;TJ.Save(Path.Combine(folder,"statistics.json"),result);TJ.Save(Path.Combine(folder,"issues.json"),issues);
        var lines=new List<string>{"# v1.2 C# 模拟与卡牌统计","",$"{rooms.Count} 局完整四 AI 赛事，{rooms.Count*4} 名人次，{stats.Battles} 场战斗。","","战斗胜率=(胜+0.5×平)/实际玩家参战次数，幽灵不计为玩家。携带表现是相关统计，不等于卡牌的因果贡献。","","|元素|最终人数|战斗胜率|夺冠率|总分|人均分|","|---|---:|---:|---:|---:|---:|"};
        string Number(JsonNode? n)=>n is null?"—":TJ.Number(n).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture);
        foreach(var(e,m)in result["elements"]!.AsObject())lines.Add($"|{e}|{Number(m!["players"])}|{Number(m["win_rate"])}|{Number(m["championship_rate"])}|{Number(m["score"])}|{Number(m["mean_score"])}|");
        lines.AddRange(new[]{"","逐卡完整统计见 [cards.md](cards.md)，机器数据见 [statistics.json](statistics.json)。完整逐局压缩账本、输入SHA256与失败记录一并保留。"});File.WriteAllLines(Path.Combine(folder,"report.md"),lines);
        var cardLines=new List<string>{"# 全183张卡牌携带与表现","","|卡牌|携带人数|携带率|同系携带率|得分率|夺冠率|战斗胜率|购买率|","|---|---:|---:|---:|---:|---:|---:|---:|"};
        foreach(var c in result["cards"].Rows().OrderBy(c=>c.N("carry_rate")))cardLines.Add($"|{c.S("name")}|{Number(c["players"])}|{Number(c["carry_rate"])}|{Number(c["same_element_carry_rate"])}|{Number(c["score_rate"])}|{Number(c["championship_rate"])}|{Number(c["win_rate"])}|{Number(c["exposure_conversion"])}|");File.WriteAllLines(Path.Combine(folder,"cards.md"),cardLines);
        // Source-independent room summary equivalent to the old simulator's smoke entry.
        var executed=TJ.Counter();var elements=TJ.Counter();var routes=TJ.Counter();var ec=TJ.Counter();var rc=TJ.Counter();foreach(var r in rooms){foreach(var p in r["placements"].Rows()){elements.Inc(p.S("spec"),p.N("score"));ec.Inc(p.S("spec"));if(p.S("route")!=""){routes.Inc(p.S("route"),p.N("score"));rc.Inc(p.S("route"));}}foreach(var b in TJ.Battles(r))foreach(var(k,v)in TJ.Counter(b["executed"]))executed.Inc(k,v);}
        TJ.Save(Path.Combine(folder,"summary.json"),TJ.O(("version","1.2"),("games",rooms.Count),("battles",stats.Battles),("round_counts",rooms.Select(r=>r["rounds"]!.AsArray().Count).ToArray()),("card_count",Catalog.CARDS.Count),("registered_effects",Catalog.CARDS.Count),("pool_copies",Catalog.CARDS.Values.Sum(Catalog.copies)),("starter",Catalog.START),("starter_only",Catalog.START_ONLY.Order(StringComparer.Ordinal).ToArray()),("executed_cards",executed.Count),("executed",executed),("element_scores",elements),("route_scores",routes),("element_selections",ec),("route_selections",rc),("interpretation",rooms.Count<1000?"Smoke tests do not establish balance or optimal AI play":"Heuristic AI sample; not proof of optimal play")));
    }
}

internal sealed record TrainingJob(int Seed,JsonObject? First,JsonObject? Second,string Folder,string Segment,string Element="",string Mode="",int Route=0,JsonObject? Bank=null,string Side="",string Owner="",bool Six=false,bool Trace=false);
