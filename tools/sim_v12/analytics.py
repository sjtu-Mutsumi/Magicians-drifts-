"""Descriptive quick-test metrics. Association is not a causal card effect."""
from collections import Counter, defaultdict
from .catalog import CARDS, ELEMENTS, ROUTES, START_ONLY, copies
from .policy import STYLES, SUPPORT

def blank():
    return Counter()

class Statistics:
    def __init__(self):
        self.rooms=0;self.participants=0;self.battles=0
        self.elements={e:blank() for e in ELEMENTS}
        self.routes={r:blank() for routes in ROUTES.values() for r in routes}
        self.styles={s:blank() for s in STYLES}
        self.cards={n:blank() for n in CARDS}
        self.card_styles={n:defaultdict(Counter) for n in CARDS}
        self.element_players=Counter();self.style_players=Counter()
        self.round_counts=[];self.skips=Counter();self.actions=0;self.planned=0
        self.tie_rooms=0

    @staticmethod
    def ending(book,p,share):
        book['players']+=1;book['score']+=p['score']
        book['positive']+=p['score']>0;book['top_two']+=p['rank']<=2
        book['champions']+=p['rank']==1;book['champion_share']+=share
        book['rank_sum']+=p['rank']

    @staticmethod
    def combat(book,winner,seat):
        book['battles']+=1
        book['wins']+=winner==seat
        book['draws']+=winner is None
        book['win_share']+=.5 if winner is None else int(winner==seat)

    def add(self,room):
        assert len(room['placements'])==4
        assert abs(sum(p['score'] for p in room['placements']))<1e-9
        assert room['pool_total']==sum(copies(c) for c in CARDS.values())
        self.rooms+=1;self.participants+=4;self.battles+=room['battles']
        self.round_counts.append(len(room['rounds']))
        final={p['pid']:p for p in room['placements']}
        champions=sum(p['rank']==1 for p in final.values());self.tie_rooms+=champions>1
        ever=defaultdict(set);seen=defaultdict(set);bought=defaultdict(set);deleted=defaultdict(set)
        for rnd in room['rounds']:
            for row in rnd['ledger']:
                pid=row['pid'];ever[pid].update(row['deck'])
                assert row['income']==row['base_income']+row['carry']+row['bonus']
                assert row['income']-sum(a['cost'] for a in row['actions'])==row['remaining']
                assert row['bank']==min(2,row['remaining'])
                for e in row['exposure']:
                    name=e['name'];assert name not in START_ONLY, copies
                    seen[pid].add(name);self.cards[name]['offer_slots']+=1
                for action in row['actions']:
                    if action['kind']=='buy':
                        n=action['name'];assert n not in START_ONLY, copies
                        bought[pid].add(n);ever[pid].add(n);self.cards[n]['purchase_count']+=1
                        if action.get('replaced'):deleted[pid].add(action['replaced'])
                    elif action['kind']=='delete':deleted[pid].add(action['name'])
            for battle in rnd['battles']:
                self.actions+=battle['actions'];self.planned+=battle['planned_actions']
                self.skips.update(battle['sequence_skips'])
                assert len(battle['executed_by_seat'])==2
                for i,p in enumerate(battle['participants']):
                    if i==1 and battle['ghost']:continue
                    for book in [self.styles[p['style']]]+([self.elements[p['spec']]] if p['spec'] else [])+([self.routes[p['route']]] if p['route'] else []):
                        self.combat(book,battle['winner'],i)
                    for n in set(p['deck']):
                        book=self.cards[n];self.combat(book,battle['winner'],i)
                        count=battle['executed_by_seat'][i].get(n,0)
                        book['casts']+=count;book['cast_battles']+=count>0
        for pid,p in final.items():
            share=1/champions if p['rank']==1 else 0
            self.element_players[p['spec']]+=1;self.style_players[p['style']]+=1
            for book in [self.styles[p['style']]]+([self.elements[p['spec']]] if p['spec'] else [])+([self.routes[p['route']]] if p['route'] else []):self.ending(book,p,share)
            names=Counter(p['deck'])
            for n in names:
                book=self.cards[n];self.ending(book,p,share)
                book['copies']+=names[n]
                book['same_element_holders']+=CARDS[n].element=='generic' or CARDS[n].element==p['spec']
                self.ending(self.card_styles[n][p['style']],p,share)
            for n in ever[pid]:
                book=self.cards[n];book['ever_players']+=1;book['ever_score']+=p['score']
                book['ever_champions']+=p['rank']==1
            for n in seen[pid]:
                book=self.cards[n];book['seen_players']+=1
                book['same_element_seen_players']+=CARDS[n].element=='generic' or CARDS[n].element==p['spec']
            for n in bought[pid]:
                assert n in seen[pid]
                self.cards[n]['bought_players']+=1
            for n in deleted[pid]:self.cards[n]['deleted_players']+=1

    @staticmethod
    def metrics(book):
        div=lambda a,b:book[a]/book[b] if book[b] else None
        return dict(players=book['players'],score=book['score'],mean_score=div('score','players'),
                    score_rate=book['score']/(3*book['players']) if book['players'] else None,
                    positive_score_rate=div('positive','players'),top_two_rate=div('top_two','players'),
                    champions=book['champions'],championship_rate=div('champions','players'),
                    shared_championship_rate=div('champion_share','players'),
                    battle_count=book['battles'],battle_wins=book['wins'],battle_draws=book['draws'],
                    win_rate=div('win_share','battles'),strict_win_rate=div('wins','battles'),
                    average_rank=div('rank_sum','players'))

    def result(self):
        cards=[]
        for n,c in CARDS.items():
            b=self.cards[n];m=self.metrics(b)
            denominator=self.participants if c.element=='generic' else self.element_players[c.element]
            div=lambda a,den:b[a]/den if den else None
            m.update(name=n,element=c.element,rarity=c.rarity,cost=c.cost,kind=c.kind,starter_only=n in START_ONLY,
                     carry_rate=div('players',self.participants),same_element_carry_rate=div('same_element_holders',denominator),
                     eligible_element_players=denominator,ever_carriers=b['ever_players'],ever_carry_rate=div('ever_players',self.participants),
                     ever_mean_score=div('ever_score',b['ever_players']),ever_championship_rate=div('ever_champions',b['ever_players']),
                     exposed_players=b['seen_players'],same_element_exposed_players=b['same_element_seen_players'],
                     offer_slots=b['offer_slots'],purchasing_players=b['bought_players'],purchase_count=b['purchase_count'],
                     exposure_conversion=div('bought_players',b['seen_players']),deleted_players=b['deleted_players'],
                     deletion_rate=div('deleted_players',b['ever_players']),retention_rate=div('players',b['ever_players']),
                     casts=b['casts'],cast_battles=b['cast_battles'],cast_rate=div('cast_battles',b['battles']),
                     styles={s:dict(**self.metrics(v),carry_rate=v['players']/self.style_players[s] if self.style_players[s] else None) for s,v in self.card_styles[n].items()})
            cards.append(m)
        eligible=[c for c in cards if not c['starter_only'] and c['rarity'] not in ('specialization','legendary')]
        groups=defaultdict(list)
        for c in eligible:groups[c['element'],c['rarity']].append(c)
        candidates=[]
        for group in groups.values():
            group.sort(key=lambda c:(c['same_element_carry_rate'] or 0,c['carry_rate']))
            for c in group[:max(1,(len(group)+3)//4)]:
                if c['same_element_exposed_players']<30:continue
                support=[r for r,names in SUPPORT.items() if c['name'] in names]
                candidates.append(dict(name=c['name'],element=c['element'],rarity=c['rarity'],
                                       same_element_carry_rate=c['same_element_carry_rate'],carry_rate=c['carry_rate'],
                                       carriers=c['players'],exposed_players=c['exposed_players'],conversion=c['exposure_conversion'],
                                       mean_score=c['mean_score'],championship_rate=c['championship_rate'],
                                       route_support=support,performance_sample_sufficient=c['players']>=30,
                                       recommendation='复审低选用原因；不可据此断言卡牌弱或自动删除'))
        candidates.sort(key=lambda c:(c['same_element_carry_rate'] or 0,c['carry_rate']))
        return dict(games=self.rooms,players=self.participants,battles=self.battles,tied_championship_rooms=self.tie_rooms,
                    round_counts=self.round_counts,planned_actions=self.planned,executed_actions=self.actions,
                    sequence_skips=dict(self.skips),
                    elements={k:self.metrics(v) for k,v in self.elements.items()},routes={k:self.metrics(v) for k,v in self.routes.items()},
                    styles={k:self.metrics(v) for k,v in self.styles.items()},cards=cards,review_candidates=candidates,
                    definitions=dict(win_rate='实际携带该元素/路线/卡牌参战时，(胜场+0.5*平局)/真实玩家参战场次；幽灵不作为统计玩家',
                                     championship_rate='最终携带者中名次第一的比例；并列第一各计一次，另提供平分冠军份额的折算夺冠率',
                                     carry_rate='淘汰或夺冠时仍携带该卡的玩家人数/全部玩家人数，同名重复只计一人次',
                                     same_element_carry_rate='最终主系为该卡元素的携带人数/该主系总人数；通用以全部玩家为分母',
                                     score_rate='携带者累计名次分/(3*携带人数)，范围-100%至100%；同时提供平均分和正分率',
                                     exposure_conversion='购买过该牌的玩家人数/看见过该牌的玩家人数；不按每次展示重复计人',
                                     caveat='携带表现是相关统计，受路线、构筑成本、曝光、AI风格及幸存者偏差影响，不等于卡牌因果贡献'))
