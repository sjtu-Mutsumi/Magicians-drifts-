# Magicians! v1.2 · 原生 C# 版

这是个人仓库 Magicians-drifts- 中的 C# 迁移版本。游戏规则、六风格 AI 与赛事流程均在 C# 内运行，卡牌效果和界面沿用 v1.2。

## 下载与试玩

**[下载 Windows 免安装试玩包](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/download/v1.2-csharp-20261031/Magicians-v1.2-CSharp-Windows-x64.zip)**（约 73.4 MB） · [发布页](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/tag/v1.2-csharp-20261031) · [校验值](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/download/v1.2-csharp-20261031/SHA256SUMS.txt)

1. 下载上述 Windows 试玩 ZIP，完整解压到一个普通文件夹。
2. 打开解压目录，双击 `Magicians.exe`。
3. 分享时发送整个 ZIP，保留同目录的 `Magicians.pck`、`data_Magicians!_windows_x86_64` 和 `runtime-data`。

适用于 Windows x64；无需安装 Godot、Python 或 .NET，也不需要联网。源码 ZIP 用于开发，试玩请下载上面的 Windows 包。实时联机尚未接入。

## 本版内容

- 183 张卡牌、677 份卡池库存、6 个 AI 模型。
- 四人单人赛事、手动构筑与战斗、AI 托管、排名、当前轮复盘和分辨率设置。
- 保留已确认的五系卡名与小字资料，小字暂不在局内显示。
- 366 个卡牌场景、15 场路线战斗、7 场完整赛事响应与原 Python 基准一致；另通过 100 场复杂牌组战斗和 27 项协议请求对照。
- 试玩包通过禁用 Python 环境的运行检查，245 项文件哈希校验通过。

## 源码与开发

**[下载本次 C# 版源码](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/download/v1.2-csharp-20261031/Magicians-v1.2-CSharp-Source.zip)**，或直接克隆本仓库的 `main`。

开发环境为 Godot 4.7.2 .NET 与 .NET 8 SDK，打开 `project.godot` 后构建运行。原生核心位于 `Scripts/Native`；`tools/sim_v12` 和 `tools/client_v12` 的 Python 代码保留为迁移对照与训练工具，不进入试玩包。

- [开发、构建和验证](docs/development.md) · [迁移验证记录](migration/validation.json)
- [卡牌与规则](docs/cards-v1.2/README.md) · [全部卡牌](docs/cards-v1.2/catalogue.md)
- [构筑规则](releases/deckbuilding-v1.2/README.md) · [世界观](docs/lore/README.md)
- [项目约束](AGENTS.md) · [本次发行元数据](releases/client-v1.2-csharp.json)

迁移基准为合作仓库 `qqzj1071/Magicians-` 的提交 `0c0150dbeda044f06c0e31d02a157aa56c7106c8`。本次只发布到这个个人仓库。

## 桌面原始压缩包

**[下载 Magicians--main.zip](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/download/desktop-snapshot-2026-10-29/Magicians--main.zip)**（约 218 MB） · [发布页与校验值](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/tag/desktop-snapshot-2026-10-29)

这是 2026-10-29 从桌面原样上传的项目源码压缩包，未重新压缩或修改内部内容。下载后请完整解压；它与另行导出的 Windows 免安装游戏包不同。
