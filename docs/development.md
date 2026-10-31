# v1.2 全 C# 开发流程

## 环境与入口

游戏与开发工具均使用 C#；活动源码不再需要 Python。开发游戏需要 Godot 4.7.2 .NET 和 .NET 8 SDK；独立开发工具只需要 .NET 8 SDK。试玩 Windows 发行包无需安装这些开发环境。

用 Godot 打开 `project.godot`。命令行在项目根目录执行：

```powershell
dotnet run --project tools/Magicians.Tooling -- help
```

工具会自动寻找项目根目录，也可在命令前传 `--root <项目目录>`。输出、训练过程、日志和本机编译文件写入 `runtime`，不要提交。

## 结构与不变约束

- `Scripts/Native`：精确分数、兼容随机序列、183 张卡牌效果、同时结算、构筑、六风格 AI 和四人赛事。
- `Scripts/Application`：原交互协议的 C# 接口；`Scripts/Presentation` 和 `Scenes` 保持已验收界面。
- `tools/Magicians.Tooling/Data`：资料生成、面板与层定义审计、冻结及完整性验证。
- `tools/Magicians.Tooling/Training`：模拟、统计、分组分析与十种训练流程。
- `tools/Magicians.Tooling/Testing`：精确数值、机制、协议、历史行为回放及固定 JSON 证据。
- `tools/Magicians.Tooling/Packaging`：Godot 导出、无 Python 检查、独立运行检查和 ZIP 打包。
- `tools/sim_v12/rules`：沿用原路径的规则 JSON 数据，里面不执行 Python。
- `releases/v1.2`、`releases/ai-v1.2-six-styles-20261029`：冻结卡牌及六个已发布模型。

稳定运行卡名作为规则 ID，五系展示名与小字另存。改名后先生成展示资料；小字仍为 `display_in_match=false`，不在局内显示。未经明确要求，不修改规则、卡牌数值或六个发布模型。

## 资料生成与检查

```powershell
dotnet run --project tools/Magicians.Tooling -- build-presentation --check
dotnet run --project tools/Magicians.Tooling -- build-catalogue --check
dotnet run --project tools/Magicians.Tooling -- audit-layers --check
dotnet run --project tools/Magicians.Tooling -- audit-cards
dotnet run --project tools/Magicians.Tooling -- verify
```

去掉生成命令的 `--check` 会写出对应资料；先检查差异，再按授权封存当前源码。总表生成默认保留当前土系、风系定稿展示名；`build-catalogue --stable-ids` 仅用于明确重现旧生成器的运行 ID 总表，不能拿它覆盖定稿名。

`export-catalog --out runtime/catalog-export` 导出卡牌和库存资料。`audit-cards --baseline <历史目录>` 可显式比较历史资料；不会自动寻找已移出的旧项目。

## 测试与固定证据

```powershell
dotnet run --project tools/Magicians.Tooling -- test
dotnet run --project tools/Magicians.Tooling -- test-oracle
dotnet run --project tools/Magicians.Tooling -- test-business
dotnet run --project tools/Magicians.Tooling -- test-protocol
```

固定证据来自迁移前 Python 版本，保存为 JSON/GZip 数据并检查 SHA-256；C# 测试不启动 Python。`capture-oracle` 导出当前 C# 行为供调试或新回放使用，不能当作原版独立证据或覆盖冻结参考。`diff-oracle` 与指定参考比较；使用自定义参考时应记录来源。

游戏场景回归：

```powershell
dotnet run --project tools/Magicians.Tooling -- client-regressions --godot <Godot.NET控制台exe>
```

它构建客户端、检查冻结资料、执行 C# 业务回归，并运行赛事、生命周期、主菜单、卡面、删牌、选择及战斗集成场景。图形过渡另运行 `Scenes/Tests/V12TransitionRunner.tscn`，需要图形后端。PowerShell 包装入口仍为 `tools/run_v12_client_regressions.ps1 -Godot <exe>`，仅转交 C# 工具执行。

## 模拟、分析与训练

- `sim` / `quick-test`：完整四人赛事、固定种子、可选策略和轨迹。
- `analyze` / `cohort`：读赛事 JSON/JSON.GZip，生成全部卡牌、曝光、携带、阶段与风格统计。
- `train --mode <模式> --out <新目录>`：支持 `mechanism`、`thin`、`medium`、`free`、`dual`、`outcome`、`element`、`element-paired`、`population`、`six`。

训练保留各模式不同的提案规则、预算、种子、验证门槛、配对方式和最终保留测试集。可以用较小预算做开发检查，但这不代表完成原预算的大规模训练。当前训练面向现行 183 张卡；早期 239 卡引擎及其历史研究重放留在旧 Git 提交，不作为当前工具入口。现行训练的历史模型输入可明确指定；默认使用仓库内发布模型起步并记入来源。训练结果输出到新目录，默认不替换发布模型。

具体训练参数、产物和迁移验证边界见 [C# 迁移记录](csharp-migration.md)。

## 冻结与历史边界

`freeze` 默认只验证，不能自动批准本地变更。首次封存 C# 实现执行 `freeze --create-native-manifest`；明确授权的实现变更验收后执行 `freeze --refresh-native-manifest`。不得为了通过检查而在未知变更后重封。这里封存的是当前 v1.2 的实现；它不会批准改写已冻结卡牌或规则。规则变更需另行明确授权并建立相应的新版本快照流程。

- `freeze-manifest.json` 和原冻结 ZIP：保留旧快照证据，逐项验证历史 ZIP 与当前冻结规则资料。
- `retired-python-manifest.json`：记录退休 Python 路径、SHA、Git blob 和原提交。已经移出的原源码是历史引用，不声称离线验证了不存在的文件。
- `native-source-manifest.json`：严格检查当前 C#、工具、卡牌、模型、展示、场景和源文件集合；新增、删除或改动受保护文件都会失败。C# 等文本统一 LF 后校验，JSON 继续检查原始字节。

原 Python 保留在 Git 历史提交中。活动源码不再保留或运行它；不会通过修改语言统计来隐藏活动源码。

## Windows 独立包

```powershell
dotnet run --project tools/Magicians.Tooling -- build-portable --godot <同版本Godot.NET控制台exe> --template <Windows release导出模板exe> --licenses <依赖许可证目录> --output <项目外新发行目录>
dotnet run --project tools/Magicians.Tooling -- audit-package --package <完整解压目录> --smoke
dotnet run --project tools/Magicians.Tooling -- archive --package <完整解压目录> --output <新ZIP路径>
```

导出会还原临时修改的导出配置，保留 Godot/.NET 许可证，校验数据与源码一致，并在禁用 Python 的环境中运行完整赛事检查。日志放在发行目录旁边，不能分发失败输出。ZIP 保留整个顶层目录并逐文件校验，附 SHA-256。

最终分享完整 ZIP；不要只分享本机快捷方式。用户完整解压后双击 `Magicians.exe`，无需联网或安装 Godot、Python、.NET。
