using System.Diagnostics;
using System.Text.Json.Nodes;
using Magicians.Native;

namespace Magicians.Tooling;

/// <summary>Original bounded learning protocols; validation gates checkpoint selection and heldout never supplies gradients.</summary>
internal sealed class TrainingRun
{
    private readonly string root,mode,output;private readonly TrainingArgs args;private readonly int seed,workers;
    private readonly List<JsonObject> all=new(),issues=new();private readonly Stopwatch clock=Stopwatch.StartNew();
    private readonly Dictionary<string,string> hashes;private readonly JsonObject provenance;
    private readonly Dictionary<string,string> policySources=new(StringComparer.Ordinal);
    private readonly JsonObject legacy=TJ.O(("kind","legacy"),("checkpoint","fixed-legacy"));
    public TrainingRun(string project,string selectedMode,TrainingArgs options)
    {
        root=project;mode=selectedMode;args=options;
        if(!new[]{"mechanism","thin","medium","free","dual","outcome","element","element-paired","population","six"}.Contains(mode))throw new ArgumentException("Unknown training mode: "+mode);
        output=TrainingCommands.NewOutput(args.Required("out"));workers=args.Int("workers",1);if(workers<1)throw new ArgumentException("workers must be positive");
        seed=args.Int("seed",mode switch{"mechanism"=>284000,"thin"=>290000,"free"=>292000,"medium"=>293000,"dual"=>294000,"outcome"=>296000,"element"=>298000,"element-paired"=>326000,"population"=>328000,_=>330000});
        hashes=TrainingCommands.Inputs(root);provenance=TJ.O(("mode",mode),("seed",seed),("workers",workers),("sha256",hashes),("cards",Catalog.CARDS.Count),("stock",Catalog.CARDS.Values.Sum(Catalog.copies)),("default_promotion",false));
    }
    public void Run()
    {
        TJ.Save(Path.Combine(output,"inputs.json"),provenance);
        try
        {
            switch(mode){case "mechanism":case "thin":case "medium":case "free":case "outcome":Single();break;case "dual":Dual();break;default:Elemental();break;}
            VerifyPolicySources();TrainingCommands.VerifyInputs(root,hashes);TrainingCommands.SaveSummary(output,all,issues);
            TJ.Save(Path.Combine(output,"progress.json"),TJ.O(("finished",true),("completed",all.Count),("battles",all.Sum(r=>r.I("battles"))),("skipped",issues.Count),("elapsed",Math.Round(clock.Elapsed.TotalSeconds,1))));
            Console.WriteLine(mode.ToUpperInvariant()+"_TRAINING_COMPLETE "+Path.Combine(output,"training.json"));
        }
        catch(Exception ex)
        {
            // A mid-run source change must not leave a provisional report claiming a verified run.
            string trainingPath=Folder("training.json");
            if(File.Exists(trainingPath)){var partial=TJ.Read(trainingPath);partial["input_hashes_verified"]=false;partial["status"]="failed";TJ.Save(trainingPath,partial);}
            Save("failure.json",TJ.O(("status","failed"),("completed",all.Count),("error",ex.GetType().Name+": "+ex.Message),("input_hashes_verified",false)));throw;
        }
    }
    private string Folder(params string[] parts)=>Path.Combine(new[]{output}.Concat(parts).ToArray());
    private JsonObject LoadPolicy(string path)
    {
        string resolved=Path.GetFullPath(path);if(!File.Exists(resolved))throw new FileNotFoundException("Training source policy is missing",resolved);
        var model=TJ.Read(resolved);policySources[resolved]=TJ.Hash(resolved);provenance["source_hashes"]=NativeJson.Node(policySources);return model;
    }
    private void VerifyPolicySources()
    {foreach(var(path,expected)in policySources)if(!File.Exists(path)||TJ.Hash(path)!=expected)throw new InvalidDataException("Source model changed while training: "+path);}
    private JsonObject Source(string? path=null)
    {
        path??=args.Get("source");
        if(path is null){var value=System.Text.Json.Nodes.JsonNode.Parse(SixStyles.load_policy("flexible").GetRawText())!.AsObject();provenance["source"]="bundled-six-style:flexible";return value;}
        string resolved=Path.GetFullPath(path);if(Directory.Exists(resolved))resolved=Path.Combine(resolved,"final-policy.json");
        var model=LoadPolicy(resolved);if(provenance["source"] is null)provenance["source"]=resolved;return model;
    }
    private static void Name(JsonObject cfg,string checkpoint)=>cfg["checkpoint"]=checkpoint;
    private static JsonObject Clone(JsonObject cfg)=>TJ.Clone(cfg);
    private void Save(string path,object? value)=>TJ.Save(Folder(path),value);
    private List<JsonObject> Collect(List<TrainingJob> jobs)
    {var rs=TrainingCommands.Collect(jobs,workers,issues);all.AddRange(rs);return rs;}
    private List<JsonObject> Generic(int start,int count,JsonObject first,Func<int,JsonObject> control,string folder,string segment,string owner="")=>Collect(Enumerable.Range(0,count).Select(i=>new TrainingJob(start+i,Clone(first),Clone(control(i)),folder,segment,Owner:owner)).ToList());
    private void Progress(int step)
    {var p=TJ.O(("round",step),("completed",all.Count),("skipped",issues.Count),("elapsed",Math.Round(clock.Elapsed.TotalSeconds,1)));Save("progress.json",p);Save("issues.json",issues);Console.WriteLine(p.ToJsonString());}
    private static JsonObject SelectedStatistics(IEnumerable<JsonObject> rooms)
    {var s=new TrainingStatistics();foreach(var r in rooms)s.Add(r);return s.Result(true);}
    private static Dictionary<string,double> Directions(JsonObject initial,IEnumerable<LearningRow> rows,bool elements=false)
    {
        var result=new Dictionary<string,double>();var rs=rows.ToArray();foreach(var(section,prefix)in new[]{("weights","w."),("construction","c.")})foreach(var(k,_)in initial[section]!.AsObject())result[prefix+k]=LearningMetrics.Correlation(rs,prefix+k);
        if(elements&&initial["element_weights"] is JsonObject ew)foreach(var(e,weights)in ew)foreach(var(k,_)in weights!.AsObject())result[e+"."+k]=LearningMetrics.Correlation(rs,"w."+k,e);return result;
    }
    private static double Mean(JsonObject objective,string key="composite")=>objective[key].N("mean");
    private static JsonObject Evaluation(IEnumerable<JsonObject> rooms)
    {var rs=rooms.ToArray();return TJ.O(("games",rs.Length),("selected",LearningMetrics.Group(rs)),("control",LearningMetrics.Group(rs,false)),("objective",LearningMetrics.Objective(rs)),("paired_win",LearningMetrics.PairedWin(rs)),("paired_score",LearningMetrics.PairedScore(rs)),("stages",LearningMetrics.Stages(rs)),("selected_decks",LearningMetrics.Describe(rs)),("control_decks",LearningMetrics.Describe(rs,false)),("selected_actions",LearningMetrics.DeckMetrics(rs)),("control_actions",LearningMetrics.DeckMetrics(rs,false)));}
    private void Single()
    {
        var source=mode=="mechanism"?TJ.O(("weights",LearningMetrics.InitialWeights)):Source();
        JsonObject heavy=TJ.O(("kind","mechanism"),("weights",source["weights"]),("checkpoint","fixed-battle-heavy-draft"));
        JsonObject small=Clone(source);Name(small,"fixed-small");JsonObject medium=args.Get("anchor") is string anchor?Source(anchor):mode=="outcome"?TrainingProposals.Medium(source):Clone(source);
        JsonObject initial=mode switch
        {
            "mechanism"=>TJ.O(("kind","mechanism"),("weights",LearningMetrics.InitialWeights),("checkpoint","initial-mechanism")),
            "thin"=>TJ.O(("kind","thin"),("weights",source["weights"]),("construction",Construction.THIN_INITIAL),("checkpoint","thin-initial")),
            "free"=>TrainingProposals.Explorer(source),"medium"=>TrainingProposals.Medium(source),_=>TrainingProposals.Neutral(source)
        };
        JsonObject best=mode=="thin"?Clone(heavy):Clone(initial);
        int defaultRounds=mode switch{"thin"=>16,"free"=>8,"medium"=>3,_=>10};int rounds=args.Int("rounds",defaultRounds);
        int nt=args.Int("train-games",60),nv=args.Int("validation-games",20),nh=args.Int("heldout-games",20);if(rounds<1||nt<1||nv<1||nh<0)throw new ArgumentException("Invalid budgets");
        int finalRounds=args.Int("final-rounds",mode switch{"thin"=>4,"free" or "medium"=>2,_=>0});int nf=args.Int("final-games",100);
        provenance["protocol"]=mode switch{"mechanism"=>"60train20validation20rolling-holdout; mean-score gate; train-only feature gradient","thin"=>"16 construction-only rounds + 4 frozen blind rounds; 3% bounded search; both anchor confidence gates","free"=>"8 target-free win-only learning rounds + 2 frozen blind rounds","medium"=>"3 construction-only broad restart rounds + 2 frozen blind rounds; battle weights fixed",_=>"10 composite outcome rounds; final heldout reserved until final checkpoint freeze"};
        provenance["budgets"]=TJ.O(("rounds",rounds),("train_games",nt),("validation_games",nv),("heldout_games",nh),("final_rounds",finalRounds),("final_games",nf));Save("inputs.json",provenance);Save("initial-policy.json",initial);Save("baselines.json",TJ.O(("legacy",legacy),("heavy",heavy),("small",small),("medium",medium)));
        var direction=new Dictionary<string,double>();if(mode=="mechanism")foreach(string k in LearningMetrics.InitialWeights.Keys)direction[k]=k is "draw" or "fee" or "lookahead" or "recycle" or "thinning"?1:k=="future"?-.25:.15;
        if(mode=="thin")foreach(string k in Construction.THIN_INITIAL.Keys)direction[k]=k is "target" or "delete_margin"?-1:1;
        var selections=new List<JsonObject>();var reserved=new List<int>();var final=new List<JsonObject>();
        for(int step=1;step<=rounds;step++)
        {
            string folder=Folder("batches",step.ToString("00"));JsonObject proposalBase=mode=="thin"&&best.S("kind")!="thin"?initial:best;
            var candidate=TrainingProposals.Propose(mode,proposalBase,initial,step,direction,seed);Save(Path.GetRelativePath(output,Path.Combine(folder,"candidate.json")),candidate);TJ.Save(Path.Combine(folder,"incumbent.json"),best);
            int start=seed+(step-1)*(nt+nv+nh);var opponents=mode=="outcome"?new[]{best,small,medium,legacy}:new[]{best};
            var train=Generic(start,nt,candidate,i=>opponents[i%opponents.Length],folder,"train");var val=Generic(start+nt,nv,candidate,i=>opponents[(i+nt)%opponents.Length],folder,"validation");
            bool complete=train.Count==nt&&val.Count==nv,accept;JsonObject td,vd;
            if(mode is "mechanism" or "thin")
            {td=TJ.O(("mean",LearningMetrics.Group(train).N("mean_score")-LearningMetrics.Group(train,false).N("mean_score")));vd=TJ.O(("mean",LearningMetrics.Group(val).N("mean_score")-LearningMetrics.Group(val,false).N("mean_score")));accept=(mode=="thin"?train.Count>=Math.Ceiling(nt*50d/60)&&val.Count>=Math.Ceiling(nv*18d/20):complete)&&td.N("mean")>=0&&vd.N("mean")>=.2;}
            else if(mode=="outcome")
            {td=LearningMetrics.Objective(train);vd=LearningMetrics.Objective(val);var phases=LearningMetrics.Stages(val);accept=complete&&Mean(td)>=0&&Mean(vd)>=.005&&new[]{"championship","win","normalized_score"}.All(k=>Mean(vd,k)>=-.04)&&phases.AsObject().All(p=>p.Value!["paired_win"].N("games")<12||p.Value!["paired_win"].N("mean")>=-.08);}
            else {td=LearningMetrics.PairedWin(train);vd=LearningMetrics.PairedWin(val);accept=complete&&td.N("mean")>=0&&vd.N("mean")>=.005;}
            if(accept){best=Clone(candidate);Name(best,mode+"-accepted-"+step.ToString("00"));}
            direction=mode=="mechanism"?LearningMetrics.MechanismGradient(train):mode=="thin"?LearningMetrics.ThinGradient(train):Directions(initial,LearningMetrics.Rows(train,mode=="outcome"),mode is "free" or "outcome");
            var choice=TJ.O(("round",step),("accepted",accept),("selected",best.S("checkpoint")),("train",td),("validation",vd),("gradient",direction),("train_decks",LearningMetrics.Describe(train)));selections.Add(choice);TJ.Save(Path.Combine(folder,"selection.json"),choice);TJ.Save(Path.Combine(folder,"checkpoint.json"),best);
            if(mode=="outcome")reserved.AddRange(Enumerable.Range(start+nt+nv,nh));
            else {var held=Generic(start+nt+nv,nh,best,i=>mode=="medium"?small:i<nh/2?legacy:mode=="mechanism"?legacy:mode=="thin"?heavy:small,folder,"rolling-holdout");if(mode=="mechanism")final.AddRange(held);}Progress(step);
        }
        Save("frozen-policy.json",best);
        if(mode=="outcome")for(int i=0;i<reserved.Count;i++)final.AddRange(Generic(reserved[i],1,best,_=>i%2==0?small:medium,Folder("final"),"final-holdout"));
        for(int step=0;step<finalRounds;step++)final.AddRange(Generic(seed+rounds*(nt+nv+nh)+step*nf,nf,best,i=>mode=="medium"?small:i<nf/2?legacy:mode=="thin"?heavy:small,Folder("batches",(rounds+step+1).ToString("00")),"final-holdout"));
        Save("final-policy.json",best);Save("final-selected-statistics.json",SelectedStatistics(final));var ev=new JsonObject();foreach(var g in final.GroupBy(r=>r["comparison"].S("second")))ev[g.Key]=Evaluation(g);
        bool gate;
        if(mode=="mechanism")
        {
            var last=final.Where(r=>r["comparison"].S("first")==best.S("checkpoint")).ToArray();var selected=LearningMetrics.Group(last);var control=LearningMetrics.Group(last,false);
            gate=last.Length>0&&selected.N("mean_score")>control.N("mean_score")&&selected.N("win_rate")>=control.N("win_rate");
        }
        else if(mode=="thin")gate=best.S("kind")=="thin"&&issues.Count<=20&&ev.Count==2&&ev.All(p=>p.Value.N("games")>=180&&p.Value!["paired_score"].N("low")>0);
        else if(mode=="free")gate=issues.Count==0&&ev.Count==2&&ev.All(p=>p.Value.N("games")==100&&p.Value!["paired_win"].N("low")>0);
        else if(mode=="medium")gate=issues.Count==0&&final.Count==200&&LearningMetrics.PairedWin(final).N("low")>0;
        else gate=issues.Count==0&&ev.Count==2&&ev.All(p=>p.Value.N("games")==100&&p.Value!["objective"]!["composite"].N("low")>0&&p.Value!["stages"]!.AsObject().All(s=>s.Value!["paired_win"].N("mean")>=-.03));
        // Historical scripts could overwrite ai-policy.json; explicit deployment is now opt-in and still obeys the measured final gate.
        bool deploy=args.Has("deploy")&&gate&&mode is "mechanism" or "thin" or "free";
        if(deploy){VerifyPolicySources();TrainingCommands.VerifyInputs(root,hashes);DataCommands.DeployPolicy(root,best,hashes["tools/sim_v12/rules/ai-policy.json"],gate);hashes["tools/sim_v12/rules/ai-policy.json"]=TJ.Hash(Path.Combine(root,"tools/sim_v12/rules/ai-policy.json"));hashes["releases/v1.2/native-source-manifest.json"]=TJ.Hash(Path.Combine(root,"releases/v1.2/native-source-manifest.json"));}
        Save("deployment.json",TJ.O(("promoted",deploy),("eligible",gate),("deployed",deploy?best.S("checkpoint"):"unchanged"),("criterion","Final heldout confidence gate; no default replacement without --deploy")));
        Save("training.json",TJ.O(("mode",mode),("completed",all.Count),("skipped",issues.Count),("batches",selections),("candidate",best),("evaluation",ev),("final_test_games",final.Count),("input_hashes_verified",true),("default_changed",deploy)));
    }
    private void Dual()
    {
        string[] families={"medium","small"};var initial=new Dictionary<string,JsonObject>();string? resume=args.Get("resume-from")??args.Get("source");
        foreach(string f in families)
        {
            var cfg=LoadPolicy(resume is null?Path.Combine(root,"tools/sim_v12/fixtures",f+"-policy.json"):Path.Combine(Path.GetFullPath(resume),f+"-final-policy.json"));Name(cfg,"dual-"+f+(resume is null?"-initial":"-continued-initial"));
            if(resume is null)cfg["role_preferences"]=f=="medium"?TJ.O(("numeric",1.15),("strong",.85),("weak",1d)):TJ.O(("numeric",.98),("strong",1.08),("weak",1.02));initial[f]=cfg;
        }
        var best=initial.ToDictionary(kv=>kv.Key,kv=>Clone(kv.Value));var dirs=families.ToDictionary(f=>f,_=>new Dictionary<string,double>());var batches=families.ToDictionary(f=>f,_=>new List<JsonObject>());var reserved=new List<(int seed,string owner)>();
        int rounds=args.Int("rounds",5),nt=args.Int("train-games",60),nv=args.Int("validation-games",20),nh=args.Int("heldout-games",20);if(rounds<1||nt<1||nv<1||nh<0)throw new ArgumentException("Invalid budgets");
        Save("initial-policies.json",initial);provenance["protocol"]="two independent families; each 5*(60train20validation20reserved); final after both freeze; win-only";Save("inputs.json",provenance);
        for(int step=1;step<=rounds;step++)
        {
            var candidates=families.ToDictionary(f=>f,f=>TrainingProposals.Propose("dual",best[f],initial[f],step,dirs[f],seed,f));
            foreach(var (f,fi)in families.Select((f,i)=>(f,i)))
            {
                int start=seed+(step-1)*2*(nt+nv+nh)+fi*(nt+nv+nh);string folder=Folder(f,"batches",step.ToString("00"));TJ.Save(Path.Combine(folder,"candidate.json"),candidates[f]);TJ.Save(Path.Combine(folder,"incumbent.json"),best[f]);
                var tr=Generic(start,nt,candidates[f],_=>best[f],folder,"train",f);var va=Generic(start+nt,nv,candidates[f],_=>best[f],folder,"validation",f);var td=LearningMetrics.PairedWin(tr);var vd=LearningMetrics.PairedWin(va);
                bool accept=tr.Count==nt&&va.Count==nv&&td.N("mean")>=0&&vd.N("mean")>=.005;if(accept){best[f]=Clone(candidates[f]);Name(best[f],$"dual-{f}-accepted-{step:00}");}
                dirs[f]=Directions(initial[f],LearningMetrics.Rows(tr));var choice=TJ.O(("round",step),("accepted",accept),("selected",best[f].S("checkpoint")),("train",td),("validation",vd),("gradient",dirs[f]),("train_decks",LearningMetrics.Describe(tr)));batches[f].Add(choice);TJ.Save(Path.Combine(folder,"selection.json"),choice);TJ.Save(Path.Combine(folder,"checkpoint.json"),best[f]);reserved.AddRange(Enumerable.Range(start+nt+nv,nh).Select(s=>(s,f)));
            }Progress(step);
        }
        Save("frozen-policies.json",best);var final=new List<JsonObject>();foreach(var (s,owner)in reserved.OrderBy(x=>x.seed))final.AddRange(Generic(s,1,best["medium"],_=>best["small"],Folder("final"),"final-holdout",owner));
        var mirror=final.Select(r=>{var c=Clone(r);c["comparison"]!["first_seats"]=NativeJson.Node(TJ.Seats(r,false).Order().ToArray());return c;}).ToArray();
        Save("medium-final-statistics.json",SelectedStatistics(final));Save("small-final-statistics.json",SelectedStatistics(mirror));foreach(string f in families)Save(f+"-final-policy.json",best[f]);
        Save("training.json",TJ.O(("mode",mode),("completed",all.Count),("skipped",issues.Count),("batches",batches),("candidates",best),("evaluation",Evaluation(final)),("final_test_games",final.Count),("input_hashes_verified",true),("default_changed",false)));
    }
    private JsonObject ModelBank(string[] elements,string family="")
    {
        var bank=new JsonObject();string? src=args.Get("source");
        foreach(string e in elements)
        {
            JsonObject cfg;
            if(mode=="element")cfg=TrainingProposals.Element(Source(),e);
            else if(src is not null)
            {
                if(!Directory.Exists(src))throw new ArgumentException(mode+" --source must name an existing directory containing per-element policies: "+src);
                string file=Path.Combine(src,e+(family==""?"-final-policy.json":"-"+family+"-final-policy.json"));
                if(File.Exists(file))cfg=LoadPolicy(file);else if(family=="large"&&File.Exists(Path.Combine(src,e+"-final-policy.json")))cfg=LoadPolicy(Path.Combine(src,e+"-final-policy.json"));else if(family=="small"&&File.Exists(Path.Combine(src,"initial-policies.json")))cfg=LoadPolicy(Path.Combine(src,"initial-policies.json"))[e]!.AsObject();else throw new FileNotFoundException("Provide a source policy for "+e,file);
            }
            else cfg=JsonNode.Parse(SixStyles.load_policy(e).GetRawText())!.AsObject();
            cfg=Clone(cfg);Name(cfg,mode+"-"+e+(family==""?"":"-"+family)+"-initial");
            if(mode=="element-paired")cfg["size_exploration"]=TJ.O(("bonus",0d),("ceiling",TrainingProposals.Sizes[e]+4));
            if(mode=="population")
            {cfg["size_exploration"]=TJ.O(("bonus",family=="large"?3d:0d),("ceiling",TrainingProposals.Sizes[e]+4));cfg["construction"]!["dilution"]=cfg["construction"].N("dilution")*(family=="large"?.92:1.03);if(family=="large")cfg["construction"]!["delete_margin"]=cfg["construction"].N("delete_margin")*1.15;}
            bank[e]=cfg;
        }return bank;
    }
    private void Elemental()
    {
        bool six=mode=="six",population=mode=="population",paired=mode!="element";string[] elements=six?SixStyles.IDS:Catalog.ELEMENTS;string[] families=population?new[]{"large","small"}:new[]{"single"};
        var initial=families.ToDictionary(f=>f,f=>ModelBank(elements,population?f:""));var best=initial.ToDictionary(kv=>kv.Key,kv=>Clone(kv.Value));
        var dirs=new Dictionary<string,Dictionary<string,double>>();var accum=new Dictionary<string,List<LearningRow>>();var batches=new Dictionary<string,List<JsonObject>>();
        foreach(string f in families)foreach(string e in elements){dirs[f+"/"+e]=new();accum[f+"/"+e]=new();batches[f+"/"+e]=new();}
        int rounds=args.Int("rounds",six?4:mode=="element-paired"?10:5);
        int nt=args.Int("train-pairs",six?10:population?9:mode=="element-paired"?18:36),nv=args.Int("validation-pairs",six?4:population?3:mode=="element-paired"?6:12),nh=args.Int("heldout-pairs",six?0:population?3:mode=="element-paired"?6:12);
        if(rounds<1||nt<1||nv<1||nh<0)throw new ArgumentException("Invalid budgets");
        Save("initial-policies.json",population?NativeJson.Node(initial):initial["single"]);provenance["protocol"]=mode switch{"element"=>"5 elements, balanced internal/external and three route intentions;5*(36train12validation12reserved);train-only bounded size reward","element-paired"=>"same-seed paired candidate/control;10*(18train6validation6reserved);balanced modes/routes;frozen final","population"=>"large/small independent starts;5*(9train3validation3reserved pairs) per element/family;branch chosen by last3 validation before test",_=>"6 independent styles;4*(10train4validation pairs);final27pairs each,earth/flexible28;no heldout reselection"};Save("inputs.json",provenance);
        var reserved=new List<(int seed,string element,string match,int route)>();
        for(int step=1;step<=rounds;step++)
        {
            var snapshot=best.ToDictionary(kv=>kv.Key,kv=>Clone(kv.Value));var candidates=new Dictionary<string,JsonObject>();var current=new Dictionary<string,List<JsonObject>>();
            for(int ei=0;ei<elements.Length;ei++)for(int fi=0;fi<families.Length;fi++)
            {
                string e=elements[ei],family=families[fi],key=family+"/"+e;var init=initial[family][e]!.AsObject();
                var c=TrainingProposals.Propose("outcome",best[family][e]!.AsObject(),init,step,dirs[key],seed+ei*(six?771:mode=="element"?700:811)+(population?fi*99:0));Name(c,$"{mode}-{e}-{(population?family+"-":"")}candidate-{step:00}");
                double proportion=mode=="element"?(step-1)/4d:mode=="element-paired"?(step-1)/9d:0;
                if(mode is "element" or "element-paired")c["size_exploration"]!["bonus"]=Math.Round(6*proportion,4);
                candidates[key]=c;string folder=Folder(population?family:"models",e,step.ToString("00"));TJ.Save(Path.Combine(folder,"candidate.json"),c);TJ.Save(Path.Combine(folder,"incumbent.json"),best[family][e]);TJ.Save(Path.Combine(folder,"opponents.json"),snapshot[family]);
                var jobs=new List<TrainingJob>();int stride=nt+nv+nh;int start=seed+(step-1)*elements.Length*families.Length*stride+ei*families.Length*stride+fi*stride;
                for(int i=0;i<stride;i++)
                {
                    int s=start+i;string match=(six?(i+step)%2:population?(i+step+fi)%2:i%2)==0?"internal":"external";int ri=six||population?i%3:(i/2)%3;
                    if(i>=nt+nv){reserved.Add((s,e,match,ri));continue;}
                    JsonObject bank=population?snapshot[families[(s/2)%2]]:snapshot[family];string segment=i<nt?"train":"validation";
                    jobs.Add(new(s,Clone(c),null,paired?Path.Combine(folder,"candidate"):folder,segment,e,match,ri,Clone(bank),paired?"candidate":"",Six:six));
                    if(paired)jobs.Add(new(s,Clone(best[family][e]!.AsObject()),null,Path.Combine(folder,"control"),segment,e,match,ri,Clone(bank),"control",Six:six));
                }current[key]=Collect(jobs);
            }
            foreach(string family in families)foreach(string e in elements)
            {
                string key=family+"/"+e;var own=current[key];var tr=own.Where(r=>r["comparison"].S("segment")=="train").ToArray();var va=own.Where(r=>r["comparison"].S("segment")=="validation").ToArray();
                var td=paired?LearningMetrics.PairedEpisodes(tr):LearningMetrics.Objective(tr);var vd=paired?LearningMetrics.PairedEpisodes(va):LearningMetrics.Objective(va);var modes=new JsonObject();foreach(string m in new[]{"internal","external"})modes[m]=paired?LearningMetrics.PairedEpisodes(va.Where(r=>r["comparison"].S("mode")==m)):LearningMetrics.Objective(va.Where(r=>r["comparison"].S("mode")==m));
                var cs=tr.Where(r=>!paired||r.S("pair_side")=="candidate").ToArray();var os=tr.Where(r=>r.S("pair_side")=="control").ToArray();double proportion=mode=="element"?(step-1)/4d:mode=="element-paired"?(step-1)/9d:0;
                double reward=mode=="element"?.02*proportion*Math.Clamp((LearningMetrics.Describe(cs).N("mean_size")-8)/(TrainingProposals.Sizes[e]+4-8),0,1):mode=="element-paired"?.02*proportion*Math.Clamp((LearningMetrics.Describe(cs).N("mean_size")-LearningMetrics.Describe(os).N("mean_size"))/(TrainingProposals.Sizes[e]+4-8),-1,1):0;
                bool complete=tr.Length==nt*(paired?2:1)&&va.Length==nv*(paired?2:1);
                bool accept=complete&&(paired?td.N("mean"):Mean(td))+reward>=0&&(paired?vd.N("mean"):Mean(vd))>=(paired?.002:.005)&&modes.All(x=>(paired?x.Value.N("mean"):x.Value!["composite"].N("mean"))>=(mode=="element-paired"?-.025:paired?-.04:-.03));
                if(!paired)accept&=new[]{"championship","win","normalized_score"}.All(k=>Mean(vd,k)>=-.08);
                if(accept){best[family][e]=Clone(candidates[key]);Name(best[family][e]!.AsObject(),$"{mode}-{e}-{(population?family+"-":"")}accepted-{step:00}");}
                var fresh=new List<LearningRow>();foreach(var r in tr)foreach(var row in LearningMetrics.Rows(new[]{r},true))if(TJ.Seats(r).Contains(row.Player.I("pid")))
                {double sizeReward=mode is "element" or "element-paired"?.02*proportion*Math.Clamp((row.Player["deck"]!.AsArray().Count-8)/(double)(TrainingProposals.Sizes[e]+4-8),0,1):0;fresh.Add(row with{Reward=row.Reward+sizeReward});}
                if(six||population)accum[key].AddRange(fresh);dirs[key]=Directions(initial[family][e]!.AsObject(),six||population?accum[key]:fresh);
                var retained=va.Where(r=>r.S("pair_side")== (accept?"candidate":"control")).ToArray();double retainedScore=TJ.Div(retained.Sum(LearningMetrics.FocalScore),retained.Length);
                var choice=TJ.O(("round",step),("accepted",accept),("selected",best[family][e].S("checkpoint")),("train",td),("validation",vd),("modes",modes),("size_reward",reward),("retained_validation_score",retainedScore));batches[key].Add(choice);
                string folder=Folder(population?family:"models",e,step.ToString("00"));TJ.Save(Path.Combine(folder,"selection.json"),choice);TJ.Save(Path.Combine(folder,"checkpoint.json"),best[family][e]);
            }Progress(step);
        }
        var choices=new Dictionary<string,string>();if(population)foreach(string e in elements)choices[e]=families.OrderByDescending(f=>batches[f+"/"+e].TakeLast(3).Sum(b=>b.N("retained_validation_score"))/3).First();
        Save("pretest-selection.json",choices);Save("frozen-policies.json",population?NativeJson.Node(best):best["single"]);
        var finalJobs=new List<TrainingJob>();
        if(six)
        {
            int s=seed+rounds*elements.Length*(nt+nv+nh);foreach(string e in elements)for(int i=0;i<args.Int("test-pairs",e is "earth" or "flexible"?28:27);i++)
            {reserved.Add((s++,e,i%2==0?"internal":"external",i%3));}
        }
        foreach(var(s,e,match,ri)in reserved)
        {
            string candidateFamily=population?"large":"single",controlFamily=population?"small":"single";var bank=population?initial[families[(s/2)%2]]:initial["single"];
            finalJobs.Add(new(s,Clone(best[candidateFamily][e]!.AsObject()),null,paired?Folder("final","candidate"):Folder("final"),"final-holdout",e,match,ri,Clone(bank),paired?"candidate":"",Six:six));
            if(paired)finalJobs.Add(new(s,Clone((population?best[controlFamily]:initial[controlFamily])[e]!.AsObject()),null,Folder("final","control"),"final-holdout",e,match,ri,Clone(bank),"control",Six:six));
        }
        var final=Collect(finalJobs);var evaluation=new JsonObject();foreach(string e in elements)
        {
            var rs=final.Where(r=>r["comparison"].S("element")==e).ToArray();var cs=rs.Where(r=>!paired||r.S("pair_side")=="candidate").ToArray();var os=rs.Where(r=>r.S("pair_side")=="control").ToArray();
            evaluation[e]=TJ.O((paired?"paired":"objective",paired?LearningMetrics.PairedEpisodes(rs):LearningMetrics.Objective(rs)),("candidate",LearningMetrics.Group(cs)),("control",LearningMetrics.Group(os)),("decks",LearningMetrics.Describe(cs)));
            foreach(string family in families){var own=population?(family=="large"?cs:os):cs;Save(e+(population?"-"+family:"")+"-final-policy.json",best[family][e]);Save(e+(population?"-"+family:"")+"-statistics.json",SelectedStatistics(own));}
            if(population)Save(e+"-selected-policy.json",best[choices[e]][e]);
        }
        Save("training.json",TJ.O(("mode",mode),("episodes",all.Count),("pairs",paired?all.Count/2:0),("battles",all.Sum(r=>r.I("battles"))),("skipped",issues.Count),("batches",batches),("evaluation",evaluation),("choices",choices),("final_test_games",final.Count),("input_hashes_verified",true),("default_changed",false)));
    }
}
