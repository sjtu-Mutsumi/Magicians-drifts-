# v1.2 开发流程

## 环境与运行

Godot 4.7.2 .NET、.NET 8 SDK。用 Godot 打开 project.godot。游戏运行不依赖 Python；Python 3.12 仅用于迁移对照、旧规则回归和模型训练工具。

## 当前结构

- Scripts/Presentation/V12MatchController.cs：构筑、战斗、卡牌与复盘界面。
- Scripts/Native/Foundation：精确分数、CPython 兼容随机序列、卡牌及实例状态。
- Scripts/Native/Combat：183张卡牌效果、私有规划、同时结算、严格位置快照。
- Scripts/Native/Policy 与 Tournament：六风格AI、有限卡池、购牌、删牌和四人赛事。
- Scripts/Native/Transport：原 JSON 决策协议，NativeMatchSession 在本机线程保留赛事状态。
- tools/client_v12 与 tools/sim_v12：迁移前只读基准和开发训练工具，不打入客户端发行包。
- releases/v1.2：卡牌快照、校验清单；releases/ai-v1.2-six-styles-20261029：六风格发布模型。
- Data/Presentation/card-art.json：纯美术槽位，不包含旧版规则副本。
- docs/cards-v1.2/*-name-map.json：五系定稿展示名来源；运行卡名继续作为稳定规则 ID。新增或修改展示名后，先运行 `python tools/build_v12_presentation.py`，由五系映射生成 Data/Presentation/card-display.json，再验证客户端。
- Data/Presentation/card-display.json：通过 V12CardPresentation 接入卡面、路线、日志与卡牌提示；显示名与规则 ID 分开。小字保留 display_in_match=false，不在局内显示。

## 验证

```powershell
powershell -ExecutionPolicy Bypass -File tools/run_v12_client_regressions.ps1
```

脚本构建.NET、验证模拟器与客户端协议、检查冻结数据，并运行完整赛事、主菜单、分辨率、删牌和选择测试。图形后端另运行 Scenes/Tests/V12TransitionRunner.tscn，覆盖买牌/结束购买/出牌过渡。测试输出写入 runtime，不能提交。

迁移的原生对照入口为 migration/NativeRunner/NativeRunner.csproj。先运行 `python migration/capture_oracle.py` 捕获原版，再构建该项目，运行 `python migration/diff_oracle.py --kind all`，比较全部卡牌场景、路线整局和手动/托管赛事每次响应与复盘。基础、战斗、经济独立测试分别位于 migration/FoundationTests、CombatTests、EconomyTests。生成的二进制、长日志和 oracle JSONL 不提交。

## Windows 离线包

```powershell
python tools/installer/build_v12_portable.py --godot <Godot控制台exe> --template <同版本Windows导出模板exe> --licenses <依赖许可证目录> --output <新发行目录>
```

使用同版本 Godot Windows 导出模板，并随包保留 Godot/.NET 许可证。发行包不复制 Python 运行时、Python 源码或旧冻结 ZIP；只保留原生程序所需的卡牌、规则JSON、模型和展示资料。不要把本机绝对路径当作规则。

导出后运行 `Magicians.exe --headless -- --release-smoke`，要求日志出现 V12_PACKAGED_OK 且退出码0；校验 integrity.json 后压缩完整目录，附 SHA256。桌面快捷方式使用 tools/update_desktop_shortcut.ps1。

## 历史与版本边界

旧版源码/报告已压缩到项目外 _history，逐文件校验完成后从活动项目删除；旧安装包使用去重归档，files.json 映射原路径到 objects/SHA256。不要将旧归档作为开发上下文。当前 v1.2 包完全取代 v1.1。

## C# 公开发行与复现

Windows 试玩包见仓库首页；源码中的 Python 只用于对照、训练与打包，不是运行依赖。使用 Python 3.12 复现对照测试；`MAGICIANS_REFERENCE_PYTHON` 可指定参考解释器，`DOTNET_EXE` 可指定 dotnet。

检查导出包：`python migration/FoundationTests/audit_native_package.py --package <完整解压的C#发行目录>`；可另加 `--original <旧试玩包目录>` 检查旧包保留情况。检查结果会写入 migration/FoundationTests/package-audit.json。
