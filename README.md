# Magicians! v1.2 · 完整 C# 工具链版

游戏、AI 训练、模拟统计、卡牌资料生成、测试、校验和打包工具均已迁移至 C#。当前活动源码不再保留 Python 文件，也不需要安装 Python。游戏继续使用原 v1.2 的规则、卡牌、六个模型与界面。

## 下载与试玩

**[下载 Windows 免安装试玩包](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/download/v1.2-all-csharp-20261031/Magicians-v1.2-AllCSharp-Windows-x64.zip)** · [发布页](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/tag/v1.2-all-csharp-20261031) · [校验值](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/download/v1.2-all-csharp-20261031/SHA256SUMS.txt)

1. 下载 Windows 试玩 ZIP，完整解压。
2. 打开解压目录，双击 `Magicians.exe`。
3. 分享时发送完整 ZIP，保留全部同目录文件。

适用于 Windows x64；无需安装 Godot、Python 或 .NET，也无需联网。不要在 ZIP 中直接运行，也不要只发送本机快捷方式。

## 内容

- 183 张卡牌、677 份卡池库存、6 个已发布 AI 模型。
- 四人单人赛事、手动构筑与战斗、AI 托管、排名、当前轮复盘和分辨率设置。
- 五系定稿卡名保持一致；小字资料保留，局内暂不显示。
- 原生 C# 的精确分数、随机序列、卡牌效果、构筑与赛事实现。
- 完整 C# 开发工具：十种训练流程、统计分析、资料生成、冻结校验、原版行为回放、Godot 导出和 ZIP 打包。

## 源码与开发

**[下载完整 C# 源码](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/download/v1.2-all-csharp-20261031/Magicians-v1.2-AllCSharp-Source.zip)**，或克隆本仓库 `main`。

开发游戏需要 Godot 4.7.2 .NET 与 .NET 8 SDK；独立开发工具只需要 .NET 8 SDK。在项目根目录执行：

```powershell
dotnet run --project tools/Magicians.Tooling -- help
dotnet run --project tools/Magicians.Tooling -- verify
dotnet run --project tools/Magicians.Tooling -- test
```

源码中的规则 JSON 与压缩回放为数据。原 Python 实现保留在 Git 历史提交中，用于追溯；当前 C# 工具不执行它。旧冻结 ZIP 仅为历史证据，不进入试玩包。

- [开发、构建和验证](docs/development.md) · [迁移范围与验证记录](docs/csharp-migration.md)
- [卡牌与规则](docs/cards-v1.2/README.md) · [全部卡牌](docs/cards-v1.2/catalogue.md)
- [构筑规则](releases/deckbuilding-v1.2/README.md) · [世界观](docs/lore/README.md)
- [项目约束](AGENTS.md) · [完整迁移验证数据](migration/all-csharp-validation.json)

## 验证范围

在删除全部活动 Python 文件、禁用 Python 环境后执行 C# 回归。包括原版 366 个卡牌场景、15 场路线战斗、7 场完整赛事，1,214 次业务操作，27 项协议响应与隐私检查，以及 41,526 项训练算法/统计对照。

十种训练模式使用缩小预算完成 358 局流程检查；这不等同于完成原来的千局大型训练。训练默认输出候选，不替换当前发布模型。现行训练针对 183 张卡，早期 239 卡研究重放留在旧提交。

## 历史版本

[此前的 C# 游戏运行版](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/tag/v1.2-csharp-20261031)保留作迁移过程记录；本次同时补齐了原来仍依赖 Python 的开发工具。

### 桌面原始压缩包

**[下载 Magicians--main.zip](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/download/desktop-snapshot-2026-10-29/Magicians--main.zip)**（约 218 MB） · [发布页与校验值](https://github.com/sjtu-Mutsumi/Magicians-drifts-/releases/tag/desktop-snapshot-2026-10-29)

这是 2026-10-29 从桌面原样上传的项目源码压缩包，未重新压缩或修改内部内容。下载后请完整解压；它与另行导出的 Windows 免安装游戏包不同。
