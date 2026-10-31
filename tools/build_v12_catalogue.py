"""Generate the current catalogue from the six canonical card tables."""
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from tools.sim_v12.catalog import ROOT,CARDS,ELEMENTS
from tools.sim_v12.panel_budget import value,budget_group

def main():
    names=dict(zip((*ELEMENTS,'generic'),('火','水','土','风','毒','通用')))
    rarity={'specialization':'专精','legendary':'传奇','epic':'史诗','rare':'稀有','common':'普通'}
    lines=['# v1.2 冻结183张卡牌总表','','临时正向数值采用加算叠加。统一术语、时点与上限见[当前规则](README.md)，固定面板预算见[panel-budgets.md](panel-budgets.md)。正式快照见releases/v1.2/cards.json。']
    for element in (*ELEMENTS,'generic'):
        lines+=['','## '+names[element],'','|品质|卡牌|费|攻|防|首次层|预算组|V值|效果|','|---|---|---:|---:|---:|---:|---|---:|---|']
        for n,c in CARDS.items():
            if c.element==element:lines.append(f'|{rarity[c.rarity]}|{n}|{c.cost}|{c.attack}|{c.defense}|{c.layers}|{budget_group(n)}|{float(value(c.cost,c.attack,c.defense)):.3f}|{c.text}|')
    (ROOT/'docs/cards-v1.2/catalogue.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print('V12_CATALOGUE',len(CARDS))

if __name__=='__main__':main()
