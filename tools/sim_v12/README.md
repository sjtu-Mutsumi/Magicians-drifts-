# v1.2 权威模拟器

当前183张卡牌、677库存，四人赛事。规则在 rules/，效果在 effects.py，StrictBattle负责锁定出牌序列。全部火系卡（含专精/传奇）统一计作火系附魔牌；见 catalog.Card.enchantment。

运行测试：python -m unittest discover -s tools/sim_v12 -t .

六模型加载入口 six_styles.load_policy；已发布模型位于 releases/ai-v1.2-six-styles-20261029。six_train.initial_models 以当前发布模型作为继续训练起点。fixtures 中两份小型策略仅用于训练算法回归，不是客户端模型。旧批次训练驱动仅保留算法兼容；不要运行含历史输入路径的旧批次 main。新训练应显式指定输入模型、全新种子范围与输出目录，并分别保留训练/验证/终测，不能据终测重选。

历史研究报告已压缩移出工作树。项目入口与发行流程见根 README 和 docs/development.md。
