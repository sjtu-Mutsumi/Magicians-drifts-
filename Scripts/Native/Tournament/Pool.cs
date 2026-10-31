using System.Text.Json;
namespace Magicians.Native;

/// <summary>Finite-copy offers, preserving the canonical joint min-cost sampler.</summary>
public class Pool
{
    private static readonly Lazy<JsonElement> Configuration=new(()=>NJson.Read("tools/sim_v12/rules/probabilities.json"));
    public static JsonElement CFG => Configuration.Value;
    public JsonElement cfg;
    public Dictionary<string,string> tokens=new(StringComparer.Ordinal);
    public Dictionary<string,List<string>> free=new(StringComparer.Ordinal);
    public Dictionary<string,int> owner=new(StringComparer.Ordinal);
    public List<string> pending=new();
    public HashSet<string> seen=new(StringComparer.Ordinal);
    public HashSet<string>[] player_seen=Enumerable.Range(0,4).Select(_=>new HashSet<string>(StringComparer.Ordinal)).ToArray();
    public Dictionary<int,List<string?>> offers=new();
    public Dictionary<string,int> stats=new(StringComparer.Ordinal);
    private static readonly Dictionary<string,int> Rank=new(){{"common",0},{"rare",1},{"epic",2},{"legendary",3}};
    public Pool(bool unlimited_generic=false,bool noncompetitive=false,JsonElement? config=null)
    {
        cfg=config??CFG;assert_uniform_counts(cfg);
        foreach(var (name,c) in Catalog.CARDS)
        {
            int count=unlimited_generic&&c.rarity=="legendary"&&c.element=="generic"?4:copies(c,cfg);
            if(noncompetitive&&count!=0)count=Math.Max(4,count*4);
            if(count>0)free[name]=new();
            for(int k=0;k<count;k++){string token=$"{name}#{k}";tokens[token]=name;free[name].Add(token);}
        }
    }
    public static string category(string name)
    {
        var c=Catalog.CARDS[name];if(c.kind=="route_legendary")return "route";
        if(c.element!="generic")return "stacking";
        return CFG.GetProperty("generic_engine_legendaries").EnumerateArray().Any(x=>x.GetString()==name)?"generic_engine":"generic_other";
    }
    public static int copies(Card c,JsonElement? config=null)
    {
        var cfg=config??CFG;
        if(Catalog.START_ONLY.Contains(c.name)||c.rarity=="specialization")return 0;
        if(c.rarity!="legendary")
        {
            var overrides=NJson.Obj(cfg,"card_copy_overrides");
            return overrides.ValueKind==JsonValueKind.Object&&overrides.TryGetProperty(c.name,out var exact)?exact.GetInt32():cfg.GetProperty("nonlegendary_copy_counts").GetProperty(c.rarity).GetProperty(c.element=="generic"?"generic":"enchantment").GetInt32();
        }
        return cfg.GetProperty("legendary_copy_counts").GetProperty(c.element=="generic"?"generic":c.kind=="route_legendary"?"route":"stacking").GetInt32();
    }
    public static void assert_uniform_counts(JsonElement cfg)
    {
        var groups=new Dictionary<(string,string),HashSet<int>>();
        foreach(var c in Catalog.CARDS.Values.Where(c=>new[]{"common","rare","epic"}.Contains(c.rarity)&&!Catalog.START_ONLY.Contains(c.name)))
        {
            int q=copies(c,cfg);if(q<=0)throw new InvalidDataException("Nonpositive card copies");
            var key=(c.rarity,c.element=="generic"?"generic":"enchantment");if(!groups.ContainsKey(key))groups[key]=new();groups[key].Add(q);
        }
        if(groups.Values.Any(v=>v.Count!=1))throw new InvalidDataException("Nonlegendary copies must be uniform within rarity/category");
    }
    private static int stochastic_round(double x,PythonRandom rng)=> (int)Math.Floor(x)+(rng.random()<x-Math.Floor(x)?1:0);
    public void begin()
    {
        if(offers.Count!=0)throw new InvalidOperationException("Outstanding offers at round boundary");
        foreach(var token in pending)free[tokens[token]].Add(token);
        pending.Clear();seen.Clear();foreach(var s in player_seen)s.Clear();
    }
    public void release(int pid)
    {
        if(!offers.Remove(pid,out var board))return;
        foreach(var token in board)if(token is not null)free[tokens[token]].Add(token);
    }
    public void remove(string? token)
    { if(token is null)return;if(!owner.Remove(token))throw new InvalidOperationException("Token not owned");pending.Add(token); }
    public string buy(int pid,int slot)
    {
        var token=offers[pid][slot]??throw new InvalidOperationException("Empty offer");
        if(owner.ContainsKey(token))throw new InvalidOperationException("Token already owned");
        offers[pid][slot]=null;owner[token]=pid;return token;
    }
    public void audit(IEnumerable<Player> players)
    {
        var parts=free.Values.SelectMany(x=>x).Concat(owner.Keys).Concat(pending).Concat(offers.Values.SelectMany(x=>x).OfType<string>()).ToList();
        if(parts.Count!=tokens.Count||parts.Distinct(StringComparer.Ordinal).Count()!=parts.Count)throw new InvalidOperationException("Pool conservation");
        foreach(var p in players)
        {
            foreach(var g in p.deck.GroupBy(x=>x.name))if(g.Count()!=1&&!(new[]{"攻击强化","防御强化"}.Contains(g.Key)&&g.Count()<=2))throw new InvalidOperationException("Duplicate card");
            foreach(var (n,t) in p.deck)if(t is not null&&(owner[t]!=p.pid||tokens[t]!=n))throw new InvalidOperationException("Deck ownership");
            if(p.deck.Count(x=>Catalog.CARDS[x.name].kind=="route_legendary")>1||p.deck.Count(x=>Catalog.CARDS[x.name].rarity=="legendary")>3)throw new InvalidOperationException("Legendary limits");
            if(p.spec!=""&&p.deck.Count(x=>Catalog.CARDS[x.name].element==p.spec&&Catalog.CARDS[x.name].kind!="specialization")<3)throw new InvalidOperationException("Specialization minimum");
        }
    }
    private readonly record struct Node(string kind,int pid=-1,int slot=-1,string name="");
    private sealed class Edge(Node to,int reverse,int capacity,long cost,int original)
    { public Node to=to;public int reverse=reverse,capacity=capacity,original=original;public long cost=cost; }
    public Dictionary<int,List<string?>> generate(List<Player> players,Dictionary<int,List<int>> templates,int round_num,int face,PythonRandom rng,Dictionary<int,HashSet<int>>? bought=null)
    {
        var slots=new List<(Player p,int i,int rank,string element)>();var desired=new Dictionary<(int,int),string>();
        foreach(var p in players)
        {
            var active=Enumerable.Range(0,8).Where(i=>bought is null||!bought[p.pid].Contains(i)).ToList();if(active.Count==0)continue;
            var ranks=new List<int>(templates[p.pid]);
            if(face==1&&CFG.GetProperty("second_face_legendary_guarantees").TryGetProperty(round_num.ToString(),out _))
                ranks=apply_refresh_legendary_guarantee(ranks,Enumerable.Range(0,8).Except(active).ToHashSet(),rng).template;
            int target;
            if(p.spec!="")target=stochastic_round(NJson.Num(cfg,"post_specialization_enchantment_share")*active.Count,rng);
            else
            {
                var distribution=cfg.GetProperty("pre_specialization_enchantment_count").EnumerateObject().ToArray();
                target=stochastic_round(rng.choices(distribution.Select(x=>int.Parse(x.Name)).ToList(),distribution.Select(x=>x.Value.GetDouble()).ToList())[0]*active.Count/8.0,rng);
            }
            var used=new HashSet<string>(StringComparer.Ordinal);var assigned=new Dictionary<int,string>();
            var allowed=p.spec==""?Catalog.ELEMENTS.ToList():new List<string>{p.spec};
            if(face==1&&p.spec=="")allowed=allowed.Where(e=>!p.bought_elements.Contains(e)||rng.random()<NJson.Num(cfg,"refresh_bought_element_admission")).ToList();
            var counts=p.deck.Where(x=>Catalog.CARDS[x.name].kind!="specialization").GroupBy(x=>Catalog.CARDS[x.name].element).ToDictionary(x=>x.Key,x=>x.Count());
            string ChooseElement(List<string> options)
            {
                double bias=p.spec==""?NJson.Num(cfg,"pre_progress_weight_per_card"):0;
                return rng.choices(options,options.Select(e=>1+bias*Math.Min(2,counts.GetValueOrDefault(e))).ToList())[0];
            }
            foreach(int i in active.Where(i=>ranks[i]==3))
            {
                bool forced=face==0&&new[]{8,10,12}.Contains(round_num)&&i<2;
                var types=CFG.GetProperty("legendary_type_order").EnumerateArray().Select(x=>x.GetString()!).ToList();
                var weights=CFG.GetProperty("legendary_types").GetProperty(round_num<=8?"6_to_8":"9_plus").EnumerateArray().Select(x=>x.GetDouble()).ToList();
                string type=forced?"route":rng.choices(types,weights)[0];desired[(p.pid,i)]=type;
                var options=allowed.Where(e=>p.spec!=""||!used.Contains(e)).ToList();
                assigned[i]=(type=="route"||type=="stacking")&&options.Count>0?ChooseElement(options):"generic";
                if(assigned[i]!="generic")used.Add(assigned[i]);
            }
            var rest=active.Where(i=>!assigned.ContainsKey(i)).ToList();rng.shuffle(rest);
            int need=Math.Max(0,target-assigned.Values.Count(e=>e!="generic"));
            foreach(int i in rest)
            {
                var options=allowed.Where(e=>p.spec!=""||!used.Contains(e)).ToList();
                if(need>0&&options.Count>0){assigned[i]=ChooseElement(options);used.Add(assigned[i]);need--;}
                else assigned[i]="generic";
            }
            foreach(int i in active)slots.Add((p,i,ranks[i],assigned[i]));
        }
        var graph=new Dictionary<Node,List<Edge>>();
        List<Edge> Edges(Node node){if(!graph.TryGetValue(node,out var edges))graph[node]=edges=new();return edges;}
        void AddEdge(Node a,Node b,int cap,long cost){var aa=Edges(a);var bb=Edges(b);aa.Add(new(b,bb.Count,cap,cost,cap));bb.Add(new(a,aa.Count-1,0,-cost,0));}
        var source=new Node("source");var sink=new Node("sink");var candidates=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        foreach(var (name,available) in free)
        {
            var avail=available.Where(t=>Catalog.CARDS[name].rarity!="legendary"||!seen.Contains(t)).ToList();
            if(avail.Count>0){candidates[name]=avail;AddEdge(new("name",name:name),sink,avail.Count,0);}
        }
        var pn=new HashSet<Node>();
        foreach(var (p,i,rank,element) in slots)
        {
            var slot=new Node("slot",p.pid,i);AddEdge(source,slot,1,0);var owned=p.deck.Select(x=>x.name).ToHashSet(StringComparer.Ordinal);
            foreach(var (name,avail) in candidates)
            {
                var c=Catalog.CARDS[name];int cr=Rank[c.rarity];
                if(cr>rank||owned.Contains(name)||p.spec!=""&&c.element!="generic"&&c.element!=p.spec||p.spec==""&&c.element!="generic"&&c.element!=element)continue;
                if(c.rarity=="legendary"&&player_seen[p.pid].Contains(name))continue;
                if(c.kind=="route_legendary"&&new[]{10,12}.Contains(round_num)&&p.deck.Any(x=>Catalog.CARDS[x.name].kind=="route_legendary"))
                {
                    int shown=player_seen[p.pid].Count(n=>Catalog.CARDS[n].kind=="route_legendary");
                    int? routeSlot=slots.Where(x=>x.p.pid==p.pid&&x.rank==3).Select(x=>(int?)x.i).FirstOrDefault();
                    if(shown!=0||i!=routeSlot)continue;
                }
                var key=new Node("pn",p.pid,name:name);if(pn.Add(key))AddEdge(key,new("name",name:name),1,0);
                int downgrade=rank-cr;
                bool guaranteed=rank==3&&(face==0&&new[]{8,10,12}.Contains(round_num)&&i<3||face==1&&new[]{8,10,12}.Contains(round_num));
                int priority=guaranteed&&face==0&&i<3?4-i:1;
                long cost=(long)downgrade*(guaranteed?10000000*priority:1000000)+10000*(c.element!=element?1:0);
                if(cr==3&&desired.TryGetValue((p.pid,i),out var typ))cost+=50000*(face==0&&new[]{8,10,12}.Contains(round_num)&&i==0?2:1)*(category(name)!=typ?1:0);
                cost+=(int)(100*(-Math.Log(Math.Max(1e-12,rng.random()))/avail.Count));AddEdge(slot,key,1,cost);
            }
            AddEdge(slot,sink,1,1000000000);
        }
        foreach(var unused in slots)
        {
            var distances=new Dictionary<Node,long>{{source,0}};var parents=new Dictionary<Node,(Node,int)>();var queue=new Queue<Node>();queue.Enqueue(source);var queued=new HashSet<Node>{source};
            while(queue.Count>0)
            {
                var a=queue.Dequeue();queued.Remove(a);var edges=Edges(a);
                for(int j=0;j<edges.Count;j++)
                {
                    var e=edges[j];if(e.capacity>0&&distances.GetValueOrDefault(e.to,long.MaxValue/4)>distances[a]+e.cost)
                    {distances[e.to]=distances[a]+e.cost;parents[e.to]=(a,j);if(queued.Add(e.to))queue.Enqueue(e.to);}
                }
            }
            if(!parents.ContainsKey(sink))throw new InvalidOperationException("Pool matching failure");
            for(var b=sink;b!=source;){var (a,j)=parents[b];var e=Edges(a)[j];e.capacity--;Edges(b)[e.reverse].capacity++;b=a;}
        }
        var result=players.ToDictionary(p=>p.pid,_=>Enumerable.Repeat<string?>(null,8).ToList());
        foreach(var (p,i,rank,element) in slots)
        {
            var chosen=Edges(new("slot",p.pid,i)).FirstOrDefault(e=>e.to.kind=="pn"&&e.original!=0&&e.capacity==0);
            if(chosen is null){NJson.Inc(stats,"empty");continue;}
            string name=chosen.to.name;string token=rng.choice(candidates[name]);candidates[name].Remove(token);free[name].Remove(token);result[p.pid][i]=token;
            var c=Catalog.CARDS[name];NJson.Inc(stats,"generated");NJson.Inc(stats,"downgrade",Rank[c.rarity]<rank?1:0);NJson.Inc(stats,"category_deviation",c.element!=element?1:0);
            if(c.rarity=="legendary")
            {
                if(!seen.Add(token)||!player_seen[p.pid].Add(name))throw new InvalidOperationException("Legendary repeated");
                NJson.Inc(stats,"legendary");NJson.Inc(stats,"type_deviation",category(name)!=desired.GetValueOrDefault((p.pid,i),category(name))?1:0);
            }
        }
        foreach(var p in players)
        {
            var board=result[p.pid];var names=board.OfType<string>().Select(t=>tokens[t]).ToArray();
            if(names.Distinct(StringComparer.Ordinal).Count()!=names.Length)throw new InvalidOperationException("Duplicate same-face name");
            var elems=names.Select(n=>Catalog.CARDS[n].element).Where(e=>e!="generic").ToArray();
            if(p.spec==""&&elems.Distinct(StringComparer.Ordinal).Count()!=elems.Length)throw new InvalidOperationException("Duplicate pre-specialization element");
            offers[p.pid]=board;
        }
        return result;
    }
    public static (List<int> template,int? chosen) apply_refresh_legendary_guarantee(List<int> template,HashSet<int> purchased_slots,PythonRandom rng)
    {
        if(template.Count!=8||template.Any(r=>r<0||r>3)||purchased_slots.Any(i=>i<0||i>7))throw new ArgumentException("Invalid rarity template");
        var active=Enumerable.Range(0,8).Where(i=>!purchased_slots.Contains(i)).ToArray();var result=new List<int>(template);
        if(active.Length==0)return(result,null);int lowest=active.Min(i=>result[i]);int chosen=rng.choice(active.Where(i=>result[i]==lowest).ToList());result[chosen]=3;return(result,chosen);
    }
}
