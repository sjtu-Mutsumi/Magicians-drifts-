# Magicians! v1.2 — 全 C# 项目约束

主入口 Scenes/Boot/Boot.tscn → MainMenu → Scenes/V12/SinglePlayer.tscn。用户已授权将游戏和开发工具全部迁移为 C#；本个人仓库直接修改 main，除非用户明确要求分支。

- 运行核心 Scripts/Native，开发工具 tools/Magicians.Tooling，均不调用 Python。不要恢复 Python 源码、Python 子进程、解释器或隐藏回退。历史原版通过 Git 提交与固定 JSON/GZip 证据追溯。
- 183 张卡牌、677 份库存、恰好六个发布模型；不擅改规则、数值、风格 ID 或模型。卡牌稳定运行 ID 与展示名分离，小字 display_in_match=false，不在局内显示。
- Data/Presentation/card-display.json 由五系 *-name-map.json 生成。改名后使用 C# build-presentation；总表默认保留土风定稿名，不用 --stable-ids 覆盖当前名称。
- 当前规则 JSON 沿用 tools/sim_v12/rules 路径，仅为数据。游戏、工具项目分开；Godot 编译排除 tools/**/*.cs、migration/**/*.cs、runtime/**/*.cs。
- 迁移后验证由 C# verify 和 test 执行；场景回归用 client-regressions --godot <exe>，图形过渡另测。构建与命令详见 docs/development.md。
- 旧 freeze-manifest/原 ZIP 验证历史资料；retired-python-manifest 仅记录退休源引用；native-source-manifest 严格保护当前实现和资料。禁止绕过检查或对未知改动自动重封；只有授权实现变更验收后显式 refresh。
- 训练默认输出候选模型，不替换现有发布模型。保留各模式选拔/验证门槛与独立测试集；小预算检查不能声称完成大规模训练。显式部署必须先通过完整性与候选选择门槛。
- 发布前：无 Python 的 C# 全回归、Godot 场景、独立包烟测、全部完整性哈希。保留同版本 Godot/.NET 许可证。
- 不提交 .godot、bin/obj、runtime、用户配置、密钥或本机缓存。不要扫描其他项目或已归档旧研究目录。
