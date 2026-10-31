using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Magicians.Native;

public class Tournament
{
    public static JsonElement ECONOMY=>Economy.Value;
    private static readonly Lazy<JsonElement> Economy=new(()=>NJson.Read("tools/sim_v12/rules/economy.json"));
    private static readonly Lazy<JsonElement> Damage=new(()=>NJson.Read("tools/sim_v12/rules/damage.json").GetProperty("table"));
    public Func<Player,string,int,string?,List<Dictionary<string,object?>>,double>? purchase_value_override;
    public static int arena_loss(int round_num,int winner_remaining_hp,bool won=false,bool draw=false)
    {
        if(round_num<1)throw new ArgumentOutOfRangeException(nameof(round_num));
        int maximum=Damage.Value.EnumerateObject().Max(x=>int.Parse(x.Name));var row=Damage.Value.GetProperty(Math.Min(round_num,maximum).ToString());
        int m=row[0].GetInt32();double n=row[1].GetDouble();return won?0:draw?m:m+(int)Math.Floor(Math.Log(Math.Max(1,winner_remaining_hp))*n);
    }
    public static List<Dictionary<string,object?>> @public(IEnumerable<Player> players)=>players.Select(p=>NJson.D(("pid",p.pid),("hp",p.hp),("spec",p.spec),("route",p.route))).ToList();
    public static int base_income(int r)
    {
        if(r<1)throw new ArgumentOutOfRangeException(nameof(r));
        var matches=ECONOMY.GetProperty("income").EnumerateArray().Where(row=>row.GetProperty("rounds")[0].GetInt32()<=r&&(row.GetProperty("rounds")[1].ValueKind==JsonValueKind.Null||r<=row.GetProperty("rounds")[1].GetInt32())).ToArray();
        if(matches.Length!=1)throw new InvalidDataException("Ambiguous or missing income rule");return matches[0].GetProperty("points").GetInt32();
    }
    public static int free_price(Player p,Card c,int r)=>!p.free_used&&(r<=2&&c.rarity=="common"||r>=3&&r<=4&&c.rarity is "common" or "rare")?0:Catalog.RANK[c.rarity]+1;
    public virtual (int? cost,string? old) price_and_replace(Player p,string name,int r)
    {
        var c=Catalog.CARDS[name];var names=p.deck.Select(x=>x.name).ToArray();
        if(names.Contains(name)||Catalog.START_ONLY.Contains(name))return(null,null);
        if(p.spec!=""&&c.element!="generic"&&c.element!=p.spec)return(null,null);
        if(c.kind=="route_legendary"){if(p.spec=="")return(null,null);if(p.route!="")return(2,p.route);}
        var legends=names.Where(n=>Catalog.CARDS[n].rarity=="legendary").ToArray();
        if(c.rarity=="legendary"&&legends.Length>=3)
        {
            var legal=legends.Where(n=>Catalog.CARDS[n].kind!="route_legendary").ToArray();if(legal.Length==0)return(null,null);
            string old=legal.OrderBy(n=>Policy.draft_value(p,n,new())).First();
            if(Catalog.CARDS[old].element==p.spec&&c.element!="generic"&&c.element!=p.spec)return(null,null);
            int main=names.Count(n=>Catalog.CARDS[n].element==p.spec&&Catalog.CARDS[n].kind!="specialization");
            if(main<=3&&Catalog.CARDS[old].element==p.spec&&c.element!=p.spec)return(null,null);return(4,old);
        }
        return(free_price(p,c,r),null);
    }
    public virtual void PurchaseSpecific(Player p,string name,int slot,int cost,string? old,Pool pool,List<Dictionary<string,object?>> events)
    {
        if(old is not null){var item=p.deck.First(x=>x.name==old);p.deck.Remove(item);pool.remove(item.token);}
        p.points-=cost;if(cost==0)p.free_used=true;string token=pool.buy(p.pid,slot);p.deck.Add((name,token));p.bought_elements.Add(Catalog.CARDS[name].element);
        p.actions.Add(NJson.D(("kind","buy"),("name",name),("cost",cost),("slot",slot),("replaced",old)));
        if(Catalog.CARDS[name].kind=="route_legendary")events.Add(NJson.D(("kind","route"),("pid",p.pid),("name",name)));
        if(p.spec=="")
        {
            var counts=p.deck.Where(x=>Catalog.CARDS[x.name].enchantment).GroupBy(x=>Catalog.CARDS[x.name].element).ToDictionary(g=>g.Key,g=>g.Count());
            var eligible=Catalog.ELEMENTS.Where(e=>counts.GetValueOrDefault(e)>=3).ToArray();
            if(eligible.Length>0){p.spec=eligible.OrderByDescending(e=>counts[e]).ThenByDescending(e=>e==p.preference).First();p.deck.Add((Catalog.SPECIAL[p.spec],null));events.Add(NJson.D(("kind","specialization"),("pid",p.pid),("element",p.spec)));}
        }
    }
    public virtual HashSet<int> buy_phase(Player p,List<Player> players,Pool pool,int r,List<Dictionary<string,object?>> events)
    {
        var bought=new HashSet<int>();
        while(true)
        {
            var options=new List<(double value,int slot,string name,int cost,string? old)>();
            for(int slot=0;slot<pool.offers[p.pid].Count;slot++)
            {
                var token=pool.offers[p.pid][slot];if(token is null)continue;string name=pool.tokens[token];var (cost,old)=price_and_replace(p,name,r);if(cost is null||cost>p.points)continue;
                double value=purchase_value_override?.Invoke(p,name,cost.Value,old,@public(players))??Construction.purchase_value(p,name,cost.Value,old,@public(players));
                options.Add((value,slot,name,cost.Value,old));
            }
            if(options.Count==0)break;
            var selected=options.OrderByDescending(x=>x.value).ThenByDescending(x=>x.slot).ThenByDescending(x=>x.name,StringComparer.Ordinal).ThenByDescending(x=>x.cost).ThenByDescending(x=>x.old,StringComparer.Ordinal).First();
            if(selected.value<=0)break;PurchaseSpecific(p,selected.name,selected.slot,selected.cost,selected.old,pool,events);bought.Add(selected.slot);
        }
        return bought;
    }
    public static (int limit,int[] fees) deletion_rules(int r)
    {
        if(r<1)throw new ArgumentOutOfRangeException(nameof(r));string key=r<=3?"rounds_1_to_3":r<=6?"rounds_4_to_6":"rounds_7_plus";
        var row=ECONOMY.GetProperty("deletions").GetProperty(key);int limit=row[0].GetInt32();var fees=row[1].EnumerateArray().Select(x=>x.GetInt32()).ToArray();
        if(limit!=fees.Length)throw new InvalidDataException("Invalid deletion schedule");return(limit,fees);
    }
    public virtual void delete_phase(Player p,Pool pool,int r)
    {
        var (limit,fees)=deletion_rules(r);
        for(int k=0;k<limit;k++)
        {
            int fee=fees[k];if(p.deck.Count<=8||p.points<fee)break;
            var counts=p.deck.GroupBy(x=>x.name).ToDictionary(g=>g.Key,g=>g.Count());int main=p.deck.Count(x=>Catalog.CARDS[x.name].element==p.spec&&Catalog.CARDS[x.name].kind!="specialization");
            var options=new List<(double value,int index,string name,string? token)>();
            for(int j=0;j<p.deck.Count;j++)
            {
                var (name,token)=p.deck[j];var c=Catalog.CARDS[name];if(c.ability||c.rarity=="legendary"||c.element==p.spec&&main<=3)continue;
                if(Construction.is_thin(p)){options.Add((Construction.deletion_value(p,name,fee),j,name,token));continue;}
                double value=Policy.draft_value(p,name,new());int undesirable=(Catalog.START_ONLY.Contains(name)?6:0)+(counts[name]>1?2:0)+(c.element!="generic"&&c.element!=p.spec&&p.spec!=""?8:0);
                if(Policy.DRAW.Contains(name)||Policy.SHUFFLE.Contains(name))undesirable-=4;options.Add((undesirable-value/3,j,name,token));
            }
            if(options.Count==0)break;
            var selected=options.OrderByDescending(x=>x.value).ThenByDescending(x=>x.index).ThenByDescending(x=>x.name,StringComparer.Ordinal).ThenByDescending(x=>x.token,StringComparer.Ordinal).First();
            if(selected.value<(Construction.is_thin(p)?0:2+fee*2))break;
            p.deck.RemoveAt(selected.index);pool.remove(selected.token);p.points-=fee;p.actions.Add(NJson.D(("kind","delete"),("name",selected.name),("cost",fee)));
        }
    }
    public static List<Dictionary<int,List<int>>> templates(List<Player> players,int r,PythonRandom rng)
    {
        string key=r<8?r.ToString():"8_plus";var weights=Pool.CFG.GetProperty("rarities").GetProperty(key);var result=new List<Dictionary<int,List<int>>>();
        for(int face=0;face<2;face++)
        {
            var byPlayer=new Dictionary<int,List<int>>();foreach(var p in players)
            {var values=rng.choices(Enumerable.Range(0,4).ToList(),weights[face].EnumerateArray().Select(x=>x.GetDouble()).ToList(),8);if(face==0&&Pool.CFG.GetProperty("first_face_legendary_guarantees").TryGetProperty(r.ToString(),out _))for(int i=0;i<3;i++)values[i]=3;byPlayer[p.pid]=values;}
            result.Add(byPlayer);
        }
        return result;
    }
    public virtual List<(int a,object b)> schedule(List<Player> players,List<Player> dead,Dictionary<object,object> last,Dictionary<(string,string),int> meetings,PythonRandom rng)
    {
        var ids=players.Select(p=>p.pid).ToArray();var choices=new List<List<(int,object)>>();
        if(ids.Length==4){int a=ids[0],b=ids[1],c=ids[2],d=ids[3];choices.Add(new(){(a,b),(c,d)});choices.Add(new(){(a,c),(b,d)});choices.Add(new(){(a,d),(b,c)});}
        else if(ids.Length==3){foreach(int g in ids){var others=ids.Where(i=>i!=g).ToArray();foreach(var ghost in dead)choices.Add(new(){(others[0],others[1]),(g,"g"+ghost.pid)});}}
        else choices.Add(new(){(ids[0],ids[1])});
        static (string,string) Key(int a,object b){string aa=a.ToString(),bb=b.ToString()!;return StringComparer.Ordinal.Compare(aa,bb)<=0?(aa,bb):(bb,aa);}
        var scored=choices.Select((pairs,index)=>(pairs,index,repeats:pairs.Count(x=>Equals(last.GetValueOrDefault(x.Item1),x.Item2)||Equals(last.GetValueOrDefault(x.Item2),x.Item1)),meetings:pairs.Sum(x=>NJson.Count(meetings,Key(x.Item1,x.Item2))),random:rng.random())).ToArray();
        var chosen=scored.OrderBy(x=>x.repeats).ThenBy(x=>x.meetings).ThenBy(x=>x.random).ThenBy(x=>x.index).First().pairs;
        foreach(var (a,b) in chosen){last[a]=b;last[b]=a;var key=Key(a,b);meetings[key]=NJson.Count(meetings,key)+1;}return chosen;
    }
    public virtual bool? refresh_wanted(Player p,int r,List<string> unbought,List<Dictionary<string,object?>> publicPlayers)=>Construction.refresh_wanted(p,r,unbought,publicPlayers);
    public virtual Battle CreateBattle(string[][] decks,string[] specializations,BigInteger seed,bool trace,string[] styles,string sequence_mode)=>sequence_mode=="locked"?new StrictBattle(decks,specializations,seed,trace,styles:styles,sequence_mode:sequence_mode):new Battle(decks,specializations,seed,trace,styles:styles,sequence_mode:sequence_mode);
    public Dictionary<string,object?> simulate()=>simulate(BigInteger.One);
    public virtual Dictionary<string,object?> simulate(BigInteger seed,bool trace=false,int max_rounds=80,bool random_styles=true,string sequence_mode="locked",JsonElement?[]? policy_by_seat=null,Func<Player,int>? refresh_threshold=null)
    {
        var rng=new PythonRandom(seed);var pool=new Pool();var players=Enumerable.Range(0,4).Select(i=>new Player(i,Catalog.ELEMENTS[(int)(((seed+i)%5+5)%5)])).ToList();
        if(policy_by_seat is not null)foreach(var p in players){p.policy=policy_by_seat[p.pid];if(p.policy is JsonElement cfg&&Catalog.ELEMENTS.Contains(NJson.Str(cfg,"element_intent")))p.preference=NJson.Str(cfg,"element_intent");}
        if(random_styles)foreach(var p in players)p.style=rng.choice(Policy.STYLES.Keys.ToArray());var initialStyles=players.ToDictionary(p=>p.pid,p=>p.style);
        var dead=new List<Player>();var last=new Dictionary<object,object>();var meetings=new Dictionary<(string,string),int>();var rounds=new List<Dictionary<string,object?>>();var placements=new List<Dictionary<string,object?>>();int battleCounter=0;
        for(int r=1;r<=max_rounds;r++)
        {
            pool.begin();var pairs=schedule(players,dead,last,meetings,rng);var events=pairs.Select(pair=>NJson.D(("kind","pairing"),("a",pair.a),("b",pair.b))).ToList();
            foreach(var p in players)
            {int bonus=p.streak!=0&&Math.Abs(p.streak)%3==0?1:0;p.carry=p.bank;p.bonus=bonus;p.income=base_income(r)+p.bank+bonus;p.points=p.income;p.free_used=false;p.bought_elements.Clear();p.actions=new();}
            var faces=templates(players,r,rng);pool.generate(players,faces[0],r,0,rng);
            var exposure=players.ToDictionary(p=>p.pid,p=>pool.offers[p.pid].Select((token,j)=>(token,j)).Where(x=>x.token is not null).Select(x=>NJson.D(("face",0),("slot",x.j),("name",pool.tokens[x.token!]))).ToList());
            var purchaseSlots=new Dictionary<int,HashSet<int>>();foreach(var p in players)purchaseSlots[p.pid]=buy_phase(p,players,pool,r,events);pool.audit(players);
            foreach(var p in players)
            {
                if(p.points>=(refresh_threshold?.Invoke(p)??2)&&purchaseSlots[p.pid].Count<8)
                {
                    var oldPublic=@public(players);var unbought=pool.offers[p.pid].OfType<string>().Select(t=>pool.tokens[t]).ToList();var refresh=refresh_wanted(p,r,unbought,oldPublic);
                    if(refresh==true||refresh is null&&(unbought.All(n=>Policy.draft_value(p,n,oldPublic)<Policy.STYLES[p.style]["refresh"])||p.deck.Count<15))
                    {p.points--;p.actions.Add(NJson.D(("kind","refresh"),("cost",1)));pool.release(p.pid);pool.generate(new(){p},faces[1],r,1,rng,purchaseSlots);exposure[p.pid].AddRange(pool.offers[p.pid].Select((token,j)=>(token,j)).Where(x=>x.token is not null).Select(x=>NJson.D(("face",1),("slot",x.j),("name",pool.tokens[x.token!]))));buy_phase(p,players,pool,r,events);}
                }
                delete_phase(p,pool,r);p.bank=Math.Min(2,p.points);pool.release(p.pid);
            }
            pool.audit(players);
            var ledger=players.Select(p=>NJson.D(("pid",p.pid),("style",p.style),("income",p.income),("base_income",base_income(r)),("carry",p.carry),("bonus",p.bonus),("remaining",p.points),("bank",p.bank),("actions",p.actions.Select(a=>new Dictionary<string,object?>(a)).ToList()),("exposure",exposure[p.pid]),("deck",p.deck.Select(x=>x.name).ToArray()))).ToList();
            foreach(var p in players)if(p.income-p.actions.Sum(a=>Convert.ToInt32(a["cost"]))!=p.points)throw new InvalidOperationException("Economy conservation");
            var snapshots=players.ToDictionary(p=>p.pid);var archives=dead.ToDictionary(p=>p.pid);var results=new List<Dictionary<string,object?>>();
            foreach(var (a,b) in pairs)
            {
                var one=snapshots[a];bool ghost=b is string;var two=ghost?archives[int.Parse(((string)b)[1..])]:snapshots[(int)b];
                var digest=SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{r}:{a}:{b}"));var battleSeed=new BigInteger(digest.AsSpan(0,8),true,true);
                var battle=CreateBattle(new[]{one.deck.Select(x=>x.name).ToArray(),two.deck.Select(x=>x.name).ToArray()},new[]{one.spec,two.spec},battleSeed,trace,new[]{one.style,two.style},sequence_mode);
                battle.policy_configs=new[]{one.policy,two.policy};var result=battle.run();battleCounter++;
                var remainingHp=((System.Collections.IEnumerable)result["hp"]!).Cast<object>().Select(Convert.ToInt32).ToArray();int? winner=result["winner"] is null?null:Convert.ToInt32(result["winner"]);
                foreach(var (index,p) in (ghost?new[]{(0,one)}:new[]{(0,one),(1,two)}))
                {bool won=winner==index,draw=winner is null;int loss=arena_loss(r,remainingHp[1-index],won,draw);p.hp-=loss;p.streak=draw?0:won?Math.Max(0,p.streak)+1:Math.Min(0,p.streak)-1;}
                var participants=new[]{one,two}.Select(p=>NJson.D(("pid",p.pid),("spec",p.spec),("route",p.route),("style",p.style),("deck",p.deck.Select(x=>x.name).ToArray()))).ToArray();
                var record=NJson.D(("a",a),("b",b),("ghost",ghost),("participants",participants));foreach(var row in result)record[row.Key]=row.Value;results.Add(record);
            }
            var eliminated=players.Where(p=>p.hp<=0).ToList();
            Dictionary<string,object?> Place(Player p,int round)=>NJson.D(("pid",p.pid),("hp",p.hp),("round",round),("spec",p.spec),("route",p.route),("style",p.style),("deck",p.deck.Select(x=>x.name).ToArray()));
            foreach(var p in eliminated){placements.Add(Place(p,r));dead.Add(p.Clone());foreach(var (_,token) in p.deck)pool.remove(token);}
            players=players.Where(p=>p.hp>0).ToList();pool.audit(players);rounds.Add(NJson.D(("round",r),("events",events),("ledger",ledger),("battles",results),("eliminated",eliminated.Select(p=>p.pid).ToArray())));
            if(players.Count<=1)
            {
                foreach(var p in players)placements.Add(Place(p,r+1));placements=placements.OrderByDescending(p=>Convert.ToInt32(p["round"])).ThenByDescending(p=>Convert.ToInt32(p["hp"])).ToList();int[] scores={3,1,-1,-3};int pos=0;
                while(pos<4)
                {int end=pos+1;while(end<4&&Equals(placements[end]["round"],placements[pos]["round"])&&Equals(placements[end]["hp"],placements[pos]["hp"]))end++;double score=scores.Skip(pos).Take(end-pos).Sum()/(double)(end-pos);for(int j=pos;j<end;j++){placements[j]["rank"]=pos+1;placements[j]["score"]=score;}pos=end;}
                return NJson.D(("version","1.2"),("sequence_rule",sequence_mode),("seed",seed),("styles",initialStyles),("rounds",rounds),("placements",placements),("battles",battleCounter),("pool_total",pool.tokens.Count),("pool_stats",new Dictionary<string,int>(pool.stats)));
            }
        }
        throw new InvalidOperationException("Tournament did not terminate; no fabricated champion");
    }
}
