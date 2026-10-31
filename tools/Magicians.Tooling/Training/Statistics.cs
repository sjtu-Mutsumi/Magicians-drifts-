using System.Text.Json.Nodes;
using Magicians.Native;

namespace Magicians.Tooling;

/// <summary>Descriptive, observational metrics. Ghosts are not real-player observations.</summary>
public sealed class TrainingStatistics
{
    private readonly List<JsonObject> rooms=new();
    public void Add(JsonObject room)
    {
        if(room["placements"]!.AsArray().Count!=4||Math.Abs(room["placements"].Rows().Sum(p=>p.N("score")))>1e-9)throw new InvalidDataException("Invalid placement conservation");
        if(room.I("pool_total")!=Catalog.CARDS.Values.Sum(Catalog.copies))throw new InvalidDataException("Pool size mismatch");
        rooms.Add(room);
    }
    public int Battles=>rooms.Sum(r=>r.I("battles"));
    private static Dictionary<string,Dictionary<string,double>> Books(IEnumerable<string> names)=>names.ToDictionary(n=>n,_=>TJ.Counter(),StringComparer.Ordinal);
    private static void Ending(Dictionary<string,double> b,JsonObject p,double share)
    {b.Inc("players");b.Inc("score",p.N("score"));b.Inc("positive",p.N("score")>0?1:0);b.Inc("top_two",p.I("rank")<=2?1:0);b.Inc("champions",p.I("rank")==1?1:0);b.Inc("champion_share",share);b.Inc("rank_sum",p.I("rank"));}
    private static void Combat(Dictionary<string,double>b,JsonObject battle,int seat)
    {b.Inc("battles");b.Inc("wins",TJ.Winner(battle)==seat?1:0);b.Inc("draws",TJ.Winner(battle) is null?1:0);b.Inc("win_share",TJ.Win(battle,seat));}
    private static JsonObject Metrics(Dictionary<string,double>b)
    {
        double V(string k)=>b.GetValueOrDefault(k);object? D(string a,string den)=>TJ.NullableDiv(V(a),V(den));
        return TJ.O(("players",V("players")),("score",V("score")),("mean_score",D("score","players")),("score_rate",TJ.NullableDiv(V("score"),3*V("players"))),
            ("positive_score_rate",D("positive","players")),("top_two_rate",D("top_two","players")),("champions",V("champions")),("championship_rate",D("champions","players")),
            ("shared_championship_rate",D("champion_share","players")),("battle_count",V("battles")),("battle_wins",V("wins")),("battle_draws",V("draws")),
            ("win_rate",D("win_share","battles")),("strict_win_rate",D("wins","battles")),("average_rank",D("rank_sum","players")));
    }
    public JsonObject Result(bool selectedOnly=false)
    {
        var elements=Books(Catalog.ELEMENTS);var routes=Books(Catalog.ROUTES.Values.SelectMany(x=>x));var styles=Books(Policy.STYLES.Keys);var cards=Books(Catalog.CARDS.Keys);
        var cardStyles=Catalog.CARDS.Keys.ToDictionary(n=>n,_=>new Dictionary<string,Dictionary<string,double>>(StringComparer.Ordinal));
        var elementPlayers=TJ.Counter();var stylePlayers=TJ.Counter();var skips=TJ.Counter();var roundCounts=new List<int>();
        int people=0,actions=0,planned=0,ties=0;
        foreach(var room in rooms)
        {
            var selected=selectedOnly?TJ.Seats(room):Enumerable.Range(0,4).ToHashSet();people+=selected.Count;
            var placements=room["placements"].Rows().ToArray();int champions=placements.Count(p=>p.I("rank")==1);if(champions>1)ties++;roundCounts.Add(room["rounds"]!.AsArray().Count);
            var seen=Enumerable.Range(0,4).ToDictionary(i=>i,_=>new HashSet<string>(StringComparer.Ordinal));var ever=Enumerable.Range(0,4).ToDictionary(i=>i,_=>new HashSet<string>(StringComparer.Ordinal));
            var bought=Enumerable.Range(0,4).ToDictionary(i=>i,_=>new HashSet<string>(StringComparer.Ordinal));var deleted=Enumerable.Range(0,4).ToDictionary(i=>i,_=>new HashSet<string>(StringComparer.Ordinal));
            IEnumerable<Dictionary<string,double>> PlayerBooks(JsonObject p)
            {yield return styles[p.S("style")];if(p.S("spec")!="")yield return elements[p.S("spec")];if(p.S("route")!="")yield return routes[p.S("route")];}
            foreach(var round in room["rounds"].Rows())
            {
                foreach(var ledger in round["ledger"].Rows())
                {
                    int pid=ledger.I("pid");if(!selected.Contains(pid))continue;
                    if(ledger.I("income")!=ledger.I("base_income")+ledger.I("carry")+ledger.I("bonus")||ledger.I("income")-ledger["actions"].Rows().Sum(a=>a.I("cost"))!=ledger.I("remaining")||ledger.I("bank")!=Math.Min(2,ledger.I("remaining")))throw new InvalidDataException("Economy conservation");
                    ever[pid].UnionWith(ledger["deck"].Strings());
                    foreach(var e in ledger["exposure"].Rows())
                    {string name=e.S("name");if(Catalog.START_ONLY.Contains(name))throw new InvalidDataException("Starter offered");seen[pid].Add(name);cards[name].Inc("offer_slots");}
                    foreach(var a in ledger["actions"].Rows())
                    {string name=a.S("name");if(a.S("kind")=="buy"){if(Catalog.START_ONLY.Contains(name))throw new InvalidDataException("Starter purchased");bought[pid].Add(name);if(!selectedOnly)ever[pid].Add(name);cards[name].Inc("purchase_count");if(a.S("replaced")!="")deleted[pid].Add(a.S("replaced"));}else if(a.S("kind")=="delete")deleted[pid].Add(name);}
                }
                foreach(var battle in round["battles"].Rows())
                {
                    actions+=battle.I("actions");planned+=battle.I("planned_actions");foreach(var (k,v) in TJ.Counter(battle["sequence_skips"]))skips.Inc(k,v);
                    foreach(var (i,p) in TJ.Players(battle))
                    {
                        if(!selected.Contains(p.I("pid")))continue;
                        foreach(var b in PlayerBooks(p))Combat(b,battle,i);
                        foreach(string n in p["deck"].Strings().Distinct(StringComparer.Ordinal))
                        {var b=cards[n];Combat(b,battle,i);double count=battle["executed_by_seat"]![i].N(n);b.Inc("casts",count);b.Inc("cast_battles",count>0?1:0);}
                    }
                }
            }
            foreach(var p in placements.Where(p=>selected.Contains(p.I("pid"))))
            {
                int pid=p.I("pid");double share=p.I("rank")==1?1d/champions:0;elementPlayers.Inc(p.S("spec"));stylePlayers.Inc(p.S("style"));
                foreach(var b in PlayerBooks(p))Ending(b,p,share);
                foreach(var g in p["deck"].Strings().GroupBy(n=>n))
                {string n=g.Key;var b=cards[n];Ending(b,p,share);b.Inc("copies",g.Count());b.Inc("same_element_holders",Catalog.CARDS[n].element=="generic"||Catalog.CARDS[n].element==p.S("spec")?1:0);if(!cardStyles[n].TryGetValue(p.S("style"),out var sb))cardStyles[n][p.S("style")]=sb=TJ.Counter();Ending(sb,p,share);}
                foreach(string n in ever[pid]){cards[n].Inc("ever_players");cards[n].Inc("ever_score",p.N("score"));cards[n].Inc("ever_champions",p.I("rank")==1?1:0);}
                foreach(string n in seen[pid]){cards[n].Inc("seen_players");cards[n].Inc("same_element_seen_players",Catalog.CARDS[n].element=="generic"||Catalog.CARDS[n].element==p.S("spec")?1:0);}
                foreach(string n in bought[pid]){if(!seen[pid].Contains(n))throw new InvalidDataException("Unexposed purchase");cards[n].Inc("bought_players");}
                foreach(string n in deleted[pid])cards[n].Inc("deleted_players");
            }
        }
        var output=new JsonArray();
        foreach(var (name,c) in Catalog.CARDS)
        {
            var b=cards[name];double V(string k)=>b.GetValueOrDefault(k);object? D(string k,double den)=>TJ.NullableDiv(V(k),den);var m=Metrics(b);
            m["name"]=name;m["element"]=c.element;m["rarity"]=c.rarity;m["carry_rate"]=NativeJson.Node(selectedOnly?TJ.Div(V("players"),people):D("players",people));
            if(selectedOnly)
            {
                m["exposed_players"]=V("seen_players");m["buying_players"]=V("bought_players");m["exposure_conversion"]=NativeJson.Node(D("bought_players",V("seen_players")));
                m["ever_carry_rate"]=TJ.Div(V("ever_players"),people);m["casts"]=V("casts");m["cast_battles"]=V("cast_battles");
                m["usage_rate"]=NativeJson.Node(D("cast_battles",V("battles")));m["casts_per_carried_battle"]=NativeJson.Node(D("casts",V("battles")));output.Add(m);continue;
            }
            double denominator=c.element=="generic"?people:elementPlayers.GetValueOrDefault(c.element);
            var extra=TJ.O(("cost",c.cost),("kind",c.kind),("starter_only",Catalog.START_ONLY.Contains(name)),("same_element_carry_rate",D("same_element_holders",denominator)),("eligible_element_players",denominator),
                ("ever_carriers",V("ever_players")),("ever_carry_rate",D("ever_players",people)),("ever_mean_score",D("ever_score",V("ever_players"))),("ever_championship_rate",D("ever_champions",V("ever_players"))),
                ("exposed_players",V("seen_players")),("same_element_exposed_players",V("same_element_seen_players")),("offer_slots",V("offer_slots")),("purchasing_players",V("bought_players")),("purchase_count",V("purchase_count")),
                ("exposure_conversion",D("bought_players",V("seen_players"))),("deleted_players",V("deleted_players")),("deletion_rate",D("deleted_players",V("ever_players"))),("retention_rate",D("players",V("ever_players"))),
                ("casts",V("casts")),("cast_battles",V("cast_battles")),("cast_rate",D("cast_battles",V("battles"))));
            foreach(var (k,v) in extra)m[k]=v?.DeepClone();var styled=new JsonObject();foreach(var (style,bk) in cardStyles[name]){var sm=Metrics(bk);sm["carry_rate"]=NativeJson.Node(TJ.NullableDiv(bk.GetValueOrDefault("players"),stylePlayers.GetValueOrDefault(style)));styled[style]=sm;}m["styles"]=styled;output.Add(m);
        }
        JsonObject Map(Dictionary<string,Dictionary<string,double>> books)=>new(books.Select(kv=>new KeyValuePair<string,JsonNode?>(kv.Key,Metrics(kv.Value))));
        if(selectedOnly)return TJ.O(("games",rooms.Count),("players",people),("elements",Map(elements)),("routes",Map(routes)),("cards",output));
        var candidates=new List<JsonObject>();
        foreach(var group in output.Rows().Where(c=>!c.B("starter_only")&&c.S("rarity") is not ("specialization" or "legendary")).GroupBy(c=>(c.S("element"),c.S("rarity"))))
        foreach(var c in group.OrderBy(c=>c.N("same_element_carry_rate")).ThenBy(c=>c.N("carry_rate")).Take(Math.Max(1,(group.Count()+3)/4)))
        {
            if(c.N("same_element_exposed_players")<30)continue;
            candidates.Add(TJ.O(("name",c.S("name")),("element",c.S("element")),("rarity",c.S("rarity")),("same_element_carry_rate",c["same_element_carry_rate"]),("carry_rate",c["carry_rate"]),
                ("carriers",c["players"]),("exposed_players",c["exposed_players"]),("conversion",c["exposure_conversion"]),("mean_score",c["mean_score"]),("championship_rate",c["championship_rate"]),
                ("route_support",Policy.SUPPORT.Where(kv=>kv.Value.Contains(c.S("name"))).Select(kv=>kv.Key).ToArray()),("performance_sample_sufficient",c.N("players")>=30),("recommendation","复审低选用原因；不可据此断言卡牌弱或自动删除")));
        }
        return TJ.O(("games",rooms.Count),("players",people),("battles",Battles),("tied_championship_rooms",ties),("round_counts",roundCounts),("planned_actions",planned),("executed_actions",actions),("sequence_skips",skips),
            ("elements",Map(elements)),("routes",Map(routes)),("styles",Map(styles)),("cards",output),("review_candidates",candidates.OrderBy(c=>c.N("same_element_carry_rate")).ThenBy(c=>c.N("carry_rate")).ToArray()),
            ("definitions",TJ.O(("win_rate","实际携带该元素/路线/卡牌参战时，(胜场+0.5*平局)/真实玩家参战场次；幽灵不作为统计玩家"),("championship_rate","最终携带者中名次第一的比例；并列第一各计一次，另提供平分冠军份额的折算夺冠率"),("carry_rate","淘汰或夺冠时仍携带该卡的玩家人数/全部玩家人数，同名重复只计一人次"),("same_element_carry_rate","最终主系为该卡元素的携带人数/该主系总人数；通用以全部玩家为分母"),("score_rate","携带者累计名次分/(3*携带人数)，范围-100%至100%；同时提供平均分和正分率"),("exposure_conversion","购买过该牌的玩家人数/看见过该牌的玩家人数；不按每次展示重复计人"),("caveat","携带表现是相关统计，受路线、构筑成本、曝光、AI风格及幸存者偏差影响，不等于卡牌因果贡献"))));
    }
}
