namespace Magicians.Native;

/// <summary>Explicit native registry: each reviewed card has exactly one handler.</summary>
public static class Effects
{
    private static readonly Dictionary<string,int> Shields=new(){{"护盾",60},{"坚岩",70}};
    private static readonly Dictionary<string,int> Bonuses=new(){{"引火",5},{"猛火弹",20},{"利刃",10}};
    private static readonly HashSet<string> Activate=new(){"炽焰王冠","潮汐之心","不周山","天岚","万毒之源","余烬之核","灰烬循环","燃烧血统","潮汐回响","生命之泉","血潮","反震岩核","壁垒之心","大地之握","疾风之刃","穿云箭","风痕","毒爆","蚀命","蚀骨","守护者壁垒","战神徽记"};
    private static int Ceil(Q q)=>(int)q.Ceiling();private static int Floor(Q q)=>(int)q.Floor();
    private static void Add(Fighter s,string key,Q amount)=>s.fx[key]+=amount;
    private static void Sub(Fighter s,Q fixedDamage,int ratio,int count=1,int marks=1,bool leech=false)
    {for(int k=0;k<count;k++)s.subs.Add((fixedDamage,new Q(ratio,100),marks,leech));}
    public static void Apply(Battle b,int i,Instance x,Before pre)
    {
        var s=b.players[i];var e=b.players[1-i];var n=x.name;
        if(Shields.TryGetValue(n,out var shield)){b.shield_gain(s,shield);return;}
        if(Bonuses.TryGetValue(n,out var bonus)){Add(s,"bonus",bonus);return;}
        if(Activate.Contains(n)){s.active.Add(n);if(n=="炽焰王冠")s.fx["fire_activation_stage"]=s.stage;else if(n=="天岚")s.fx["shots"]=0;else if(n=="蚀骨")b.operations.Add(new("decay_reschedule",1-i,i));return;}
        switch(n)
        {
            case "短促":Add(s,"delay",-5);return;
            case "烈焰冲刺":Add(s,"delay",-10);return;
            case "风步":Add(s,"delay",new Q(-15,2));return;
            case "速读":case "掠影":b.draw(s,1);return;
            case "破甲":Add(s,"main_def_pen",40);Add(s,"main_shield_pen",40);return;
            case "燎原":case "深渊潮":case "山崩":case "飓风":case "瘟潮":case "攻击强化":case "防御强化":return;
            case "不灭烈焰":b.recover(s,1,"fire");return;
            case "循毒":if(b.plan_read(i,"recover",e.poison>=3&&s.discard.Any(a=>Catalog.CARDS[a.name].enchantment&&Catalog.CARDS[a.name].element=="poison")))b.recover(s,1,"poison");else b.draw(s,1);return;
            case "余烬引擎":case "灰烬重生":case "盾之共鸣":case "循环":case "重洗":
                b.shuffle(s);if(n=="余烬引擎")b.draw(s,2);else if(n=="灰烬重生")b.add_ember(s,1);else if(n=="盾之共鸣")b.shield_gain(s,60);else if(n=="循环"){s.pending["draw"]++;s.pending["energy"]++;}return;
            case "余烬心脏":if(b.plan_read(i,"low_hp",s.hp*10<=s.max_hp*3)){b.add_ember(s,2);b.heal(s,new Q(s.max_hp-s.hp,5));}else{b.add_ember(s,1);s.pending["draw"]++;}return;
            case "余烬刻度":if(b.plan_read(i,"fire_gate",s.layers["fire"]>=8))b.add_ember(s,1);else s.pending["draw"]++;return;
            case "余烬护心":if(s.ember!=0){b.consume_ember(s,1);b.heal(s,new Q((s.max_hp-s.hp)*15,100));}else b.shield_gain(s,60);return;
            case "回火":if(s.fx["shuffled"]!=0)b.add_ember(s,1);return;
            case "焚心":if(s.ember!=0){var count=Math.Min(2,s.ember);for(int k=0;k<count;k++){b.consume_ember(s,1);b.heal(s,new Q(s.max_hp-s.hp,5));}}else b.add_ember(s,1);return;
            case "灰烬账本":if(b.plan_read(i,"ticket",s.spent_ember/3>s.fx["book_claims"])){if(s.spent_ember/3>s.fx["book_claims"]){s.fx["book_claims"]++;b.add_ember(s,1);}}else s.pending["draw"]+=2;return;
            case "压缩爆燃":Add(s,"bonus",30+(b.stats(i).attack>=135?20:0)+(s.ember>=2?30:0));return;
            case "爆燃":Add(s,"bonus",pre.fire>=2?35:25);return;
            case "导火索":s.fx["discount_fire"]++;Add(s,"bonus",15);return;
            case "战意":if(s.layers["fire"]>=4)s.attack+=4;return;
            case "烈焰附身":b.temp(i,"heal_block",25,1-i);return;
            case "高温炉心":s.fx["hot_core"]+=5;return;
            case "熔盾烈焰":s.fx["melt_shield"]++;return;
            case "狂暴":case "燃血":case "燃页":case "毒瓶":case "血刃":case "禁忌知识":
                var paid=b.pay_life(s,new Q(n=="燃页"?3:5,100));
                if(n=="狂暴")Add(s,"temp_attack",30);else if(n=="燃血"){b.add_ember(s,1);Add(s,"bonus",15);}else if(n=="燃页"){b.draw(s,1);Add(s,"bonus",10);}else if(n=="毒瓶")b.target_poison(i,2);else if(n=="血刃"){s.attack+=6;b.add_tide(s,new Q(paid*s.layers["water"]*6,100));}else{s.pending["draw"]+=2;s.pending["energy"]++;}return;
            case "火花":case "轻羽":if(pre.first)s.layers[Catalog.CARDS[n].element]++;return;
            case "毒引":if(pre.first&&e.poison!=0)s.layers["poison"]++;return;
            case "余火":case "追云":var element=n=="余火"?"fire":"wind";foreach(var inst in b.draw(s,n=="余火"?1:2))if(Catalog.CARDS[inst.name].enchantment&&Catalog.CARDS[inst.name].element==element&&!s.seen.Contains(inst.uid))s.discounts[inst.uid]++;return;
            case "追风":var drawn=b.draw(s,1);if(drawn.Count>0&&Catalog.CARDS[drawn[0].name].enchantment&&Catalog.CARDS[drawn[0].name].element=="wind")s.fx["permanent_delay"]--;return;
            case "活水":var healed=b.heal(s,20);if(b.plan_read(i,"healed_draw",healed>0))b.draw(s,1);return;
            case "火种库":b.search(s,c=>c.enchantment&&c.element=="fire",4,2,true,unseen_only:true,optional:true);return;
            case "火种翻找":b.search(s,c=>c.enchantment&&c.element=="fire",3,prioritize_unseen:true);return;
            case "水之低语":b.search(s,c=>c.element=="water",2);return;
            case "深流":if(b.search(s,c=>c.element=="water"&&c.rarity=="legendary",3).Count==0)b.heal(s,30);return;
            case "寻风":case "寻毒":b.search(s,c=>c.element==(n=="寻风"?"wind":"poison"),2,optional:n=="寻风");return;
            case "寻岩":b.search(s,c=>Catalog.SHIELD_CARDS.Contains(c.name),2,optional:true);return;
            case "穿云":b.search(s,c=>c.element=="wind",3,instant:true,optional:true);return;
            case "岩核共鸣":b.shield_gain(s,100);b.search(s,c=>c.element=="earth"&&c.enchantment&&Catalog.RANK[c.rarity]>=1,5,optional:true);s.pending["earth_discount"]++;return;
            case "命运检索":case "大检索":b.search(s,c=>true,n=="命运检索"?5:4,2);return;
            case "检索附魔":b.search(s,c=>c.enchantment,2);return;
            case "禁咒目录":b.search(s,c=>Catalog.RANK[c.rarity]>=2,5,2,true);return;
            case "回响之潮":b.heal(s,100);if(s.has("潮汐之心")){Add(s,"tide_bonus",50);Add(s,"tide_record",50);}return;
            case "奔流":s.fx["flow"]++;return;
            case "水幕":b.heal(s,50);Add(s,"main_shield_pen",70);return;
            case "泉心":b.heal(s,new Q(s.max_hp-s.hp,5),bonus:100);return;
            case "洪潮":b.add_tide(s,s.tide!=0?new Q(s.tide*3,5):s.layers["water"]*8);return;
            case "深渊":s.fx["abyss"]++;return;
            case "潮汐风暴":var consumedTide=b.consume_tide(s,s.tide);if(consumedTide!=0)b.operations.Add(new("packet",1-i,b.packet(i,new Q(consumedTide*3,2),"water")));return;
            case "血潮汹涌":b.add_tide(s,new Q((s.max_hp-s.hp)*s.layers["water"],50)*(s.hp*100<=s.max_hp*40?new Q(3,2):new Q(1)));return;
            case "回流":if(b.search(s,c=>c.name=="潮汐之心",1,instant:true).Count==0)b.heal(s,30);return;
            case "洪流":if(s.has("潮汐之心"))b.add_tide(s,s.layers["water"]*10);else b.heal(s,60);return;
            case "涌泉":b.heal(s,50);s.fx["springs"]++;return;
            case "潮声":b.heal(s,25);s.pending.heal_batches.Add(25);return;
            case "潮汐回廊":if(b.search(s,c=>c.name=="潮汐之心",1).Count==0)b.search(s,c=>c.element=="water"&&c.rarity=="legendary",s.pile.Count);b.add_tide(s,s.layers["water"]*8);return;
            case "疾流":if(b.plan_read(i,"tide_gate",s.tide>=30)){b.consume_tide(s,30);s.pending["energy"]+=2;}else b.heal(s,30);return;
            case "穿潮":Add(s,"pierce_tide",1);return;
            case "血怒":b.add_tide(s,s.layers["water"]*(pre.hp*2<=s.max_hp?12:8));return;
            case "连环潮":s.fx["discount_water"]++;b.heal(s,20);return;
            case "静水":if(s.has("潮汐之心"))s.fx["still_water"]++;return;
            case "泉眼":b.heal(s,s.hp*2<=s.max_hp?50:20);return;
            case "潮涌":b.heal(s,40);if(s.has("潮汐之心"))Add(s,"tide_shield",50);return;
            case "细流":b.heal(s,30);s.fx["trickle"]++;return;
            case "万钧碎盾":case "碎盾术":b.shield_gain(s,n=="万钧碎盾"?120:80);b.flush_shields(s);if(s.has("不周山")){var consumed=b.take_shield(s,s.shield_total);var p=b.burst(i,consumed,n=="万钧碎盾"?new Q(3,2):new Q(1));if(n=="万钧碎盾")p["active_return"]=new Q(consumed*3,10);b.operations.Add(new("packet",1-i,p));}return;
            case "不坏壁垒":b.shield_gain(s,220,no_decay:true);return;
            case "回震岩甲":b.shield_gain(s,100);s.fx["return_flat"]+=30;return;
            case "山岳之躯":var used=b.take_shield(s,Math.Min(150,Ceil(new Q((s.max_hp-s.hp)*2,3))));b.heal(s,used*new Q(3,2));return;
            case "山岳壁垒":s.fx["earth_extra"]+=30;return;
            case "岩核重压":s.fx["direct"]+=new Q(s.shield,5);return;
            case "岩潮壁垒":b.shield_gain(s,140,temporary:true,conversion:new Q(1,2));return;
            case "层岩结构":s.fx["shield_structure"]++;return;
            case "临时壁垒":b.shield_gain(s,120,temporary:true);return;
            case "回岩":b.shield_gain(s,150);s.fx["return_once"]+=80;return;
            case "守岩":b.shield_gain(s,80);s.fx["next_flat"]+=40;return;
            case "山岳":b.shield_gain(s,pre.shield==0?200:120);return;
            case "岩拳":b.shield_gain(s,60);s.fx["discount_earth"]++;return;
            case "握盾":b.shield_gain(s,80);if(s.has("大地之握"))Add(s,"grip_bonus",100);return;
            case "盾脉":b.shield_gain(s,80);b.flush_shields(s);s.fx["shield_pulse"]+=2;return;
            case "碎岩印记":b.shield_gain(s,60);if((b.slot_snapshot??b.players)[i].fx["broken"]!=0){s.defense+=5;b.shield_gain(s,40,temporary:true);}return;
            case "磐岩壁":b.shield_gain(s,60);Add(s,"shield_leech",30);return;
            case "裂岩护壁":b.shield_gain(s,40);s.fx["direct"]+=b.flush_shields(s)*2;return;
            case "岩甲":b.shield_gain(s,10+2*pre.defense);return;
            case "磐石":b.shield_gain(s,100);b.temp(i,"atk_down",20,1-i);return;
            case "开岩":if(b.plan_read(i,"shield_gate",pre.total>=30)){b.take_shield(s,30);b.draw(s,1);}else b.shield_gain(s,30);return;
            case "气旋护壁":s.fx["lifeguard"]=1;return;
            case "烈风":s.fx["permanent_delay"]-=new Q(5,2);return;
            case "疾风连斩":s.fx["permanent_delay"]--;Sub(s,b.base_gap(s)<=25?160:70,50,2);return;
            case "穿云破甲":Add(s,"armor_break",6);return;
            case "裂空穿云":s.strips.Add((new Q(2,5),2));return;
            case "风暴":s.permanent_subs.Add((90,new Q(3,10),1,false));return;
            case "风痕回廊":Sub(s,100,30,2,s.has("风痕")?2:1);if(b.plan_read(i,"corridor_draw",!s.has("风痕")))b.draw(s,2);return;
            case "狂风":b.draw(s,3);s.energy++;return;
            case "疾行":Add(s,"delay",-10);b.draw(s,1);return;
            case "裂风":Sub(s,70,20,2);return;
            case "顺风":if(pre.wind>=2&&s.fx["tailwind_used"]==0){s.energy++;s.fx["tailwind_used"]=1;}return;
            case "风刃":b.draw(s,1);if(pre.first)s.energy++;return;
            case "风切":Sub(s,40,50,leech:true);return;
            case "风痕刻印":Sub(s,60,30,marks:s.has("风痕")?2:1);if(!s.has("风痕"))Add(s,"delay",-10);return;
            case "风驰":var reward=b.plan_read(i,"wind_reward",b.gap(s)<=30?2:1);b.draw(s,2);s.energy+=reward;return;
            case "风眼":b.temp(i,"negative_guard",1);return;
            case "万毒爆":case "毒潮":var layers=e.poison;var consumedPoison=n=="万毒爆"?layers:Math.Min(3,layers);if(consumedPoison!=0){var raw=b.stats(i).attack*s.layers["poison"]*consumedPoison*new Q(s.specialization=="poison"?4:2,100)*(n=="万毒爆"?new Q(4):new Q(5,2));var p=b.packet(i,raw,"explosion",new(){{"defense",b.stats(1-i).defense}});b.target_poison(i,-consumedPoison);b.operations.Add(new("packet",1-i,p));}return;
            case "万蚀":foreach(var p in b.poison_packets(i))b.operations.Add(new("packet",1-i,p));return;
            case "毒龙":b.target_poison(i,1);return;
            case "疫病":b.temp(i,"atk_down",Floor(3+new Q(e.poison*3,2)),1-i);return;
            case "蚀骨之触":b.operations.Add(new("def_down_permanent",1-i,(Q)Floor(2+new Q(e.poison,2))));return;
            case "缓释毒素":s.fx["slow_poison"]++;b.operations.Add(new("decay_reschedule",1-i,i));return;
            case "固毒":s.fx["poison_guards"]++;return;
            case "枯潮":b.target_poison(i,2);if(b.plan_read(i,"poison_fee",e.poison+2>=3))s.energy++;if(b.plan_read(i,"poison_draw",e.poison+2>=6))b.draw(s,1);return;
            case "毒囊":b.shield_gain(s,40+5*e.poison);return;
            case "毒涌":if(b.plan_read(i,"poison_draw",e.poison!=0)){b.target_poison(i,-1);b.draw(s,2);}else b.draw(s,1);return;
            case "爆裂":Add(s,"delay",e.poison>=3?-20:-10);return;
            case "腐息":s.fx["poison_repeat"]++;return;
            case "腐溃":Add(s,"main_attack",8+4*e.poison);return;
            case "万毒蚀骨":var reductions=Q.Max(0,e.attack-b.stats(1-i).attack)+Q.Max(0,e.defense-b.stats(1-i).defense);Add(s,"main_attack",4*e.poison+new Q(3,2)*reductions);return;
            case "腐潮":b.heal(s,(s.max_hp-s.hp)*Q.Min(new Q(3,10),new Q(15+e.poison,100)));return;
            case "蚀创":s.attack+=e.poison;s.defense+=e.poison;return;
            case "蚀刻":b.temp(i,"heal_block_max",e.poison>=3?50:30,1-i);return;
            case "毒针":s.pending["poison"]++;return;
            case "风之躯":case "风蚀":var basis=(b.slot_snapshot??b.players)[i];var kinds=basis.mods.Where(m=>Battle.NEGATIVE.Contains(m.kind)).Select(m=>m.kind=="heal_block_max"?"heal_block":m.kind).ToHashSet();if(basis.wind_marks!=0)kinds.Add("wind_marks");var (cleared,_)=b.cleanse(s,n=="风之躯"?s.poison:3);if(n=="风之躯")b.draw(s,1);else b.heal(s,(cleared+kinds.Count)*20);return;
            case "时光倒流":var (attack,defense)=b.stats(i);s.rewind=new Q[]{b.time,attack,defense,s.hp,s.shield_total};return;
            case "恐惧":b.temp(i,"fear",1,1-i);s.pending["draw"]++;return;
            case "永恒壁垒":s.fx["eternal"]++;return;
            case "灵感涌现":b.draw(s,2);s.energy++;return;
            case "通晓":b.draw(s,2);b.move_hand(s,1,"discard");return;
            case "坚壁":if(b.move_hand(s,1,"discard")!=0)b.shield_gain(s,50);return;
            case "战术":var amount=s.energy;s.energy=0;s.spent+=amount;if(amount!=0)s.pending["energy"]+=amount+1;else if(s.fx["tactic_used"]==0){s.energy=1;s.fx["tactic_used"]=1;}return;
            case "护盾强化":b.shield_gain(s,40);b.move_hand(s,1,"topdeck");return;
            case "断流封印":b.temp(i,"heal_block",25,1-i);return;
            case "狂战":if(b.move_hand(s,1,"discard")!=0){b.draw(s,1);Add(s,"bonus",10);}return;
            case "翻阅":b.draw(s,2);s.pending["draw"]--;return;
            case "裂伤":s.strips.Add((new Q(1,5),0));return;
            case "包扎":if(b.move_hand(s,1,"discard")!=0)b.heal(s,20);return;
            case "预备":s.pending["draw"]--;s.pending["energy"]++;return;
            case "换页":b.draw(s,1);b.move_hand(s,1,"insert");return;
            case "整理":s.pending["draw"]+=2;s.pending["energy"]--;return;
            case "蓄能":if(b.move_hand(s,1,"topdeck")!=0)s.pending["draw"]++;return;
            default:throw new InvalidOperationException("Unregistered v1.2 effect: "+n);
        }
    }
}
