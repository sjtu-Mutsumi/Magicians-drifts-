using System.Text.Json;
using Magicians.Native;
namespace Magicians.Tooling;

internal static class StaticRegression
{
    public static object Run(string root)
    {
        var count=0;
        void Check(bool test,string name){if(!test)throw new InvalidDataException(name);count++;}
        foreach(var r in Enumerable.Range(1,100))
        {
            Check(Tournament.base_income(r)==(r<=3?2:r<=7?3:4),"income boundary "+r);
            var rule=Tournament.deletion_rules(r);var expected=r<=3?(0,Array.Empty<int>()):r<=6?(1,new[]{1}):(2,new[]{0,1});
            Check(rule.limit==expected.Item1&&rule.fees.SequenceEqual(expected.Item2),"deletion boundary "+r);
        }
        try{Tournament.base_income(0);throw new InvalidDataException("invalid income round accepted");}catch(ArgumentException){count++;}
        try{Tournament.deletion_rules(0);throw new InvalidDataException("invalid deletion round accepted");}catch(ArgumentException){count++;}
        using(var published=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"releases/deckbuilding-v1.2/economy.json"))))
            TestCommands.Compare(published.RootElement,Tournament.ECONOMY,"published economy");
        var forbidden=new[]{"圣盾","愈合之光","战术大师","治愈洪流","破军","破障法阵","盾墙","蓄势","稳固结界","穿甲","潮汐恩泽","大地之盾","穿空","灼痛","镜岩之心","风幕","蚀界","拆盾弹","翻找","垒石"};
        foreach(var n in forbidden)Check(!Catalog.CARDS.ContainsKey(n),"retired card "+n);
        foreach(var n in Catalog.START_ONLY)Check(Catalog.copies(Catalog.CARDS[n])==0,"starter copies "+n);
        var selected=new[]{"熔盾烈焰","奔流","层岩结构","风暴","缓释毒素"};
        foreach(var card in Catalog.CARDS.Values)
        {
            if(selected.Contains(card.name))
            {
                Check(card.cost==3&&card.rarity=="epic","permanent card identity "+card.name);
                Check(PanelBudget.reference_rarity(card.name,card.rarity)=="rare"&&PanelBudget.multiplier(card.name)==new Q(4,5),"permanent budget reference "+card.name);
                var bounds=PanelBudget.bounds(card.name,card.rarity);Check(bounds.low==6&&bounds.high==new Q(36,5)&&PanelBudget.value(card.cost,card.attack,card.defense)==7,"permanent budget "+card.name);
            }
            else Check(PanelBudget.budget_group(card.name)!="three_cost_permanent","budget isolation "+card.name);
        }
        foreach(var c in Catalog.CARDS.Values.Where(c=>c.element=="fire"))Check(c.enchantment,"all fire cards enchantments "+c.name);
        Check(Catalog.CARDS["深渊"].cost==3&&Catalog.CARDS["风暴"].cost==3,"abyss and storm costs");
        Check(Policy.TARGETS.Values.All(x=>((System.Collections.IEnumerable)x["entries"]!).Cast<string>().All(Catalog.CARDS.ContainsKey)),"route targets have no retired cards");
        foreach(var style in SixStyles.IDS)
        {
            var model=SixStyles.load_policy(style);Check(model.GetProperty("kind").GetString()=="explorer","all six models");
            if(style=="flexible")Check(!model.TryGetProperty("element_intent",out var intent)||string.IsNullOrEmpty(intent.GetString()),"flexible intent boundary");
            else Check(model.GetProperty("element_intent").GetString()==style,"element intent "+style);
        }
        try{SixStyles.load_policy("unknown");throw new InvalidDataException("unknown style accepted");}catch(ArgumentException){count++;}
        foreach(var name in new[]{"枯潮","循毒","狂战","换页","禁咒目录","风痕回廊","导火索","连环潮","岩拳"})Check(PanelBudget.role(name)=="strong","strong precedence "+name);
        foreach(var name in new[]{"不灭烈焰","重洗","盾之共鸣","灰烬循环","疾流","坚壁"})Check(PanelBudget.role(name)=="weak","weak role "+name);
        foreach(var name in new[]{"狂战","换页"})Check(Mechanics.static_features(name)["draw"]==1,"conditional draw classification "+name);
        var roleCounts=Catalog.CARDS.Keys.GroupBy(PanelBudget.role).ToDictionary(g=>g.Key,g=>g.Count());Check(roleCounts["numeric"]==119&&roleCounts["strong"]==32&&roleCounts["weak"]==32,"uniform roles inventory");
        Check(PanelBudget.bounds("深渊","epic")== (new Q(19,2),new Q(11))&&PanelBudget.value(Catalog.CARDS["深渊"].cost,Catalog.CARDS["深渊"].attack,Catalog.CARDS["深渊"].defense)==new Q(43,4),"abyss unchanged budget");
        Check(PanelBudget.bounds("狂风","rare")== (new Q(21,4),new Q(63,10))&&PanelBudget.bounds("检索附魔","common")== (new Q(9,2),new Q(27,5)),"uniform strong weak budget");
        foreach(var name in new[]{"猛火弹","坚岩","攻击强化","泉眼","万蚀"})
        {var c=Catalog.CARDS[name];var (lo,hi)=PanelBudget.bounds(name,c.rarity);Check(PanelBudget.role(name)=="numeric"&&PanelBudget.value(c.cost,c.attack,c.defense)>=lo+(hi-lo)*new Q(3,4),"instant card high numeric budget "+name);}
        var unchanged=new Dictionary<string,(int,int)>{{"战意",(8,0)},{"深渊",(8,9)},{"回震岩甲",(13,1)},{"山岳壁垒",(1,9)},{"蚀创",(6,5)},{"血刃",(2,4)},{"烈风",(8,1)},{"岩核重压",(8,1)},{"盾脉",(1,7)}};
        foreach(var (n,panel) in unchanged)Check((Catalog.CARDS[n].attack,Catalog.CARDS[n].defense)==panel,"unselected panel unchanged "+n);
        using(var damage=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"releases/deckbuilding-v1.2/damage.json"))))
        using(var runtimeDamage=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"tools/sim_v12/rules/damage.json"))))
            TestCommands.Compare(damage.RootElement.GetProperty("table"),runtimeDamage.RootElement.GetProperty("table"),"published arena damage");
        foreach(var (r,losses) in new[]{(1,new[]{7,9,9}),(4,new[]{7,9,9}),(5,new[]{14,17,18}),(8,new[]{14,17,18}),(9,new[]{16,19,20})})
        {for(var i=0;i<3;i++)Check(Tournament.arena_loss(r,new[]{100,500,1000}[i])==losses[i],"arena loss "+r+"/"+i);Check(Tournament.arena_loss(r,1000,won:true)==0,"winning arena loss");}
        Check(Tournament.arena_loss(81,500)==Tournament.arena_loss(80,500),"late rounds arena cap");
        foreach(var row in new[]{(3,2,0,0),(4,0,0,0),(4,1,1,1),(6,2,1,1),(7,0,1,0),(7,1,2,1),(8,2,2,1)})
        {
            var par=new Dictionary<string,double>(Construction.THIN_INITIAL){["delete_margin"]=-1000,["starter_bias"]=1000};
            var player=new Player(0,"fire"){points=row.Item2,deck=Catalog.START.Concat(new[]{"猛火弹","坚岩"}).Select(n=>(n,(string?)null)).ToList(),policy=JsonSerializer.SerializeToElement(new{kind="thin",weights=PolicyTables.learning_policy_INITIAL_WEIGHTS,construction=par})};
            new Tournament().delete_phase(player,new Pool(),row.Item1);
            Check(player.actions.Count==row.Item3&&player.actions.Sum(a=>Convert.ToInt32(a["cost"]))==row.Item4&&player.points==row.Item2-row.Item4&&player.deck.Count==10-row.Item3,"actual deletion charges and limits "+row.Item1+"/"+row.Item2);
        }
        using(var combat=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"docs/cards-v1.2/combat-rules.json"))))
        using(var runtimeCombat=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"tools/sim_v12/rules/combat.json"))))
            TestCommands.Compare(combat.RootElement,runtimeCombat.RootElement,"published fatigue rules");
        var setup=new StrictBattle(new[]{new[]{"顺风","风刃","轻羽","掠影"},new[]{"攻击强化","防御强化","护盾"}},seed:19);
        var mechanism=JsonSerializer.SerializeToElement(new{kind="mechanism",weights=PolicyTables.learning_policy_INITIAL_WEIGHTS,checkpoint="test-mechanism"});setup.policy_configs=new JsonElement?[]{mechanism,mechanism};
        foreach(var s in setup.players){s.hand=s.instances.ToList();s.pile.Clear();s.stage=1;s.energy=s.seat==0?3:0;}
        var sequence=setup.plan_stage()[0].Select(x=>x.name).ToList();Check(sequence.IndexOf("顺风")>=2,"conditional wind fee prepared before tailwind");
        var p=new Player(0,"fire"){spec="fire",policy=mechanism};Check(Policy.draft_value(p,"禁忌知识",new())>8,"generic ability mechanism utility");
        p=new Player(0,"water"){spec="water",deck=new[]{"潮汐之心","生命之泉","奔流","深渊","涌泉","活水","潮声","洪流","泉眼","通晓"}.Select(n=>(n,(string?)null)).ToList(),policy=JsonSerializer.SerializeToElement(new{kind="thin",weights=PolicyTables.learning_policy_INITIAL_WEIGHTS,construction=Construction.THIN_INITIAL})};
        Check(Construction.purchase_value(p,"包扎",0,null,new())<0&&Construction.deletion_value(p,"包扎",0)>0,"thin skips free filler");
        var policyChecks=PolicyRegression.Run(root);var guardChecks=GuardRegression.Run(root);
        return new{checks=count,training_policy_checks=policyChecks,corruption_guard_checks=guardChecks};
    }
}
