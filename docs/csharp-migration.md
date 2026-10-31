# v1.2 完整 C# 迁移记录

## 边界

基准卡牌、规则和发布模型来自合作仓库提交 `0c0150dbeda044f06c0e31d02a157aa56c7106c8`。个人仓库 `2e38abc44a37214b4edd0aad6aa2ab273e171c97` 是已经迁好游戏核心、仍保留 79 个 Python 文件的中间版本。

本次补齐当前 183 卡项目的开发能力，移出活动源码的全部 79 个 Python 文件。旧源可从中间提交查阅；退休 SHA 与 Git blob 记录在 `releases/v1.2/retired-python-manifest.json`。没有把 Python 改后缀、调用解释器或改变语言识别配置作为替代。

## 功能对应

| 原能力 | 当前 C# 实现/命令 |
|---|---|
| 模型、精确分数、随机数、卡牌、战斗、规划、赛事、卡池、策略 | `Scripts/Native`；`protocol` 可用于 JSON 行交互 |
| `sim_v12/__main__`、`quick_test` | `sim` / `quick-test` |
| `analytics`、`cohort`、训练指标与阶段统计 | `analyze` / `cohort`，`Training/Statistics.cs` 与 `LearningMetrics.cs` |
| `train`、`thin_train`、`medium_train`、`free_train` | `train --mode mechanism/thin/medium/free` |
| `dual_train`、`outcome_train` | `train --mode dual/outcome` |
| `element_train`、`element_paired_train` | `train --mode element/element-paired` |
| `population_train`、`six_train` | `train --mode population/six` |
| 卡牌/库存导出、面板预算 | `export-catalog`、`Data/PanelBudgets.cs` |
| 展示名、卡牌总表、卡牌与层契约审计 | `build-presentation` / `build-catalogue` / `audit-cards` / `audit-layers` |
| 原 `freeze_v12`、`verify_v12` | `freeze` / `verify`，历史证据和当前实现分别校验 |
| Python 业务测试、协议测试、迁移期差分 | `test`、`test-business`、`test-protocol`、`test-oracle`，固定 JSON/GZip 证据 |
| 当前行为捕获/比较 | `capture-oracle` / `diff-oracle`；捕获当前实现不能替代独立原版证据 |
| Python 自动导出与包审计 | `build-portable` / `audit-package` / `archive` |
| PowerShell 回归包装 | `client-regressions`；原 `.ps1` 仅转交 C# 工具 |

旧 Python unittest 没有逐句机械翻译；C# 使用固定原版输入/随机状态/输出/返回值回放，以及明确的静态/机制断言。163 个旧测试名称的对应关系见 `tools/Magicians.Tooling/Testing/Fixtures/coverage-matrix.json`，其中 153 个原模拟器测试在迁移期实际执行通过，其余客户端测试对应协议和完整流程检查。

## 训练使用

例如：

```powershell
dotnet run --project tools/Magicians.Tooling -- sim --games 5 --seed 1200 --out runtime/rooms-new
dotnet run --project tools/Magicians.Tooling -- train --mode six --out runtime/six-new
dotnet run --project tools/Magicians.Tooling -- training-selftest
```

训练目录必须为新目录。各模式可通过 `--source`、`--anchor`、`--resume-from` 指定模型；不同模式对单模型文件或模型目录有明确要求，无效输入会报错，不静默替换。所有实际加载的模型和源数据都记录 SHA，并在训练结束前复核。`--workers` 默认 1，可显式增加。

`--rounds`、`--train-games`/`--train-pairs`、`--validation-games`/`--validation-pairs`、`--heldout-games`/`--heldout-pairs`、`--test-pairs`、`--workers` 等缩预算参数用于开发检查；原默认预算与各模式提案/选拔算法、配对种子、独立最终测试集保留。默认没有部署。显式 `--deploy` 还需通过各模式原有门槛、当前实现完整性和预期策略 SHA 检查，仅更新允许的策略及其一条清单哈希；六个已发布模型不随普通训练改变。

早期已归档的 239 卡 `train.historical` 辅助重放不作为本版本训练功能恢复；应在对应历史 Git 提交中追溯，不混入当前 183 卡规则。

## 实际验证

- 原数值与随机参考：精确分数、Python 兼容 MT19937、洗牌和克隆。
- 551 项机制、532 项静态/边界、55 项训练约束、12 项损坏与容错守卫。
- 366 个卡牌场景、15 场整局战斗、7 场完整赛事的原版参考。
- 1,214 次公开业务操作，来自 112 个原测试名；27 项完整协议响应与隐私检查。
- 训练对照包含 3 场完整赛事、8 组固定提案和统计结果，共 41,526 项；浮点统计容差 `1e-11`，失败 0。
- 十种训练模式缩预算共 358 局，跳过 0，输入 SHA 复核通过。没有执行原千局/数千局大型训练，也没有替换当前发布模型。
- 资料生成与现有定稿结果匹配；旧总表生成器的土风名称覆盖问题已修正，`--stable-ids` 可显式复现旧输出。
- 当前源码、规则、模型、展示资料、冻结 ZIP，以及新增/删除源文件都有完整性守卫；损坏、脏源和未经门槛批准的部署会拒绝。

固定原版证据为纯数据，不包含可执行 Python。证据与当前源分别受 SHA 保护。以上是有限回归与小预算训练的实测范围，不代表穷尽所有可能输入或所有训练结果逐位相同。

## 有意保留的差异

- 自动工具默认不部署候选；原有部署能力改为明确 `--deploy`，保留各模式门槛。
- 缺失的旧研究路径改为显式模型输入，默认来源记为仓库发布模型。
- `freeze` 默认验证；显式封存的是当前 v1.2 实现，不批准规则变更。旧卡牌快照与当前 C# 清单分开验证。
- 历史 JSON 中提到的旧 `.py` 路径是来源记录；`tools/sim_v12/rules` 只是沿用路径的 JSON 数据。
