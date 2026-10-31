using Magicians.Native;
using System.Text.Json;
Catalog.Initialize(args[0]);
if(args.Length>1&&args[1]=="long")
{
    var longRows=new List<object>();
    foreach(int seed in new[]{1,334020})
    {
        var rng=new PythonRandom(seed);var pool=new Pool();var tournament=new Tournament();
        var players=Enumerable.Range(0,4).Select(i=>new Player(i,Catalog.ELEMENTS[(seed+i)%5]){policy=SixStyles.load_policy(i==0?"flexible":SixStyles.IDS[(seed+i)%5])}).ToList();
        foreach(var p in players)p.style=rng.choice(Policy.STYLES.Keys.ToArray());
        var last=new Dictionary<object,object>();var meetings=new Dictionary<(string,string),int>();var rounds=new List<object>();
        for(int r=1;r<=12;r++)
        {
            pool.begin();var pairs=tournament.schedule(players,new(),last,meetings,rng);
            foreach(var p in players){p.carry=p.bank;p.bonus=0;p.income=Tournament.base_income(r)+p.bank;p.points=p.income;p.free_used=false;p.bought_elements.Clear();p.actions=new();}
            var templates=Tournament.templates(players,r,rng);pool.generate(players,templates[0],r,0,rng);
            var first=pool.offers.ToDictionary(x=>x.Key,x=>x.Value.Select(t=>t is null?null:pool.tokens[t]).ToArray());
            var bought=new Dictionary<int,HashSet<int>>();var events=new List<Dictionary<string,object?>>();foreach(var p in players)bought[p.pid]=tournament.buy_phase(p,players,pool,r,events);
            var refresh=new Dictionary<int,string?[]>();
            foreach(var p in players)
            {
                if(p.points>=2&&bought[p.pid].Count<8)
                {
                    var pub=Tournament.@public(players);var unbought=pool.offers[p.pid].OfType<string>().Select(t=>pool.tokens[t]).ToList();var wanted=tournament.refresh_wanted(p,r,unbought,pub);
                    if(wanted==true||wanted is null&&(unbought.All(n=>Policy.draft_value(p,n,pub)<Policy.STYLES[p.style]["refresh"])||p.deck.Count<15))
                    {p.points--;p.actions.Add(new(){{"kind","refresh"},{"cost",1}});pool.release(p.pid);pool.generate(new(){p},templates[1],r,1,rng,bought);refresh[p.pid]=pool.offers[p.pid].Select(t=>t is null?null:pool.tokens[t]).ToArray();tournament.buy_phase(p,players,pool,r,events);}
                }
                tournament.delete_phase(p,pool,r);p.bank=Math.Min(2,p.points);pool.release(p.pid);
            }
            pool.audit(players);
            rounds.Add(new{r,pairs=pairs.Select(x=>new object[]{x.a,x.b}).ToArray(),templates,first,refresh,events,players=players.Select(p=>new{p.pid,p.style,p.spec,p.points,p.bank,deck=p.deck.Select(x=>new object?[]{x.name,x.token}).ToArray(),actions=p.actions.ToArray()}).ToArray(),stats=new Dictionary<string,int>(pool.stats)});
        }
        longRows.Add(new{seed,rounds,next=Enumerable.Range(0,8).Select(_=>rng.random()).ToArray()});
    }
    Console.WriteLine(JsonSerializer.Serialize(longRows,new JsonSerializerOptions{IncludeFields=true}));return;
}
var rows=new List<object>();
foreach(int seed in new[]{-17,0,1,17,334020})
{
    var rng=new PythonRandom(seed);var pool=new Pool();var tournament=new Tournament();
    var players=Enumerable.Range(0,4).Select(i=>new Player(i,Catalog.ELEMENTS[((seed+i)%5+5)%5]){policy=SixStyles.load_policy(i==0?"flexible":SixStyles.IDS[i-1])}).ToList();
    pool.begin();var templates=Tournament.templates(players,1,rng);var offers=pool.generate(players,templates[0],1,0,rng);
    var events=new List<Dictionary<string,object?>>();var purchases=new Dictionary<int,HashSet<int>>();
    foreach(var p in players)p.points=Tournament.base_income(1);
    var draft=players.Select(p=>new{pid=p.pid,values=Catalog.CARDS.Keys.ToDictionary(n=>n,n=>Policy.draft_value(p,n,Tournament.@public(players)))}).ToArray();
    foreach(var p in players)purchases[p.pid]=tournament.buy_phase(p,players,pool,1,events);
    pool.audit(players);
    rows.Add(new{seed,templates,offers=offers.ToDictionary(x=>x.Key,x=>x.Value.Select(t=>t is null?null:pool.tokens[t]).ToArray()),draft,players=players.Select(p=>new{p.pid,p.spec,p.points,deck=p.deck.Select(x=>new object?[]{x.name,x.token}).ToArray(),p.actions}).ToArray(),stats=pool.stats});
}
Console.WriteLine(JsonSerializer.Serialize(rows,new JsonSerializerOptions{IncludeFields=true}));
