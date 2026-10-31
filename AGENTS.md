# Magicians! v1.2 — 当前项目约束

v1.2 已于 2026-10-30 验收，完全替代 v1.1。主入口 Scenes/Boot/Boot.tscn → MainMenu → Scenes/V12/SinglePlayer.tscn。不存在旧客户端回退入口。

- 游戏运行核心：Scripts/Native（原生 C#），183张卡牌、677份库存；NativeMatchSession 在独立线程暂停/恢复四人赛事，Application 层保留原 JSON 交互协议，Presentation 层界面不变。运行和发行包均不需要 Python。
- tools/sim_v12 与 tools/client_v12 保留为迁移前只读对照及开发训练工具，不进入客户端发行包；规则迁移按 migration 下的差分用例验证，不可用原 Python 进程作为隐藏回退。
- 卡牌：releases/v1.2/cards.json 与 docs/cards-v1.2。保留稳定运行卡名作为规则 ID；五系定稿显示名已通过 V12CardPresentation 接入 Data/Presentation/card-display.json。该显示资料由 tools/build_v12_presentation.py 从五系 *-name-map.json 生成，新增或修改展示名后先运行生成脚本；不得直接替换规则内核的卡名键。小字保留 display_in_match=false，不在局内显示。全部火系卡（含专精/传奇）属于火系附魔牌；ability 与打出后移出规则独立。
- AI：releases/ai-v1.2-six-styles-20261029，恰好六模型。不要把战略 style ID 当战术名，不擅改规则和模型。
- 规则修改需用户授权，并维护 freeze-manifest.json 的当前执行哈希；历史冻结 ZIP 是溯源基线，不代表最新执行修正。保留校验，禁止绕过。
- 回归：powershell -File tools/run_v12_client_regressions.ps1；图形过渡测试另运行 Scenes/Tests/V12TransitionRunner.tscn（需图形后端）。Python3.12、Godot4.7.2.NET、.NET8；TEMP/TMP 使用工作区 runtime/v12-temp。
- 当前 C# 发行：releases/client-v1.2-csharp.json；releases/client-v1.2.json 为迁移前历史记录。构建见 docs/development.md。发布前验证导出日志、禁用 Python 后的原生运行烟测和 integrity.json。
- 旧版源码、研究报告及旧发行包已压缩归档到项目外 D:/Magicians!/_history。日常任务不扫描、解压或引用旧归档。只以本文件、README、当前规则与源码为准。
- 不提交本机缓存、日志、用户配置、密钥、历史大包。不要恢复被清理的旧客户端/文档。
