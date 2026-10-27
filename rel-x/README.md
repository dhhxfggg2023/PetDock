# PetDock — 桌宠内核

> **本项目完全由 AI 生成。** 规格文档（`docs/`）、代码、测试、绘图脚本与本文档均由 AI 代理在
> 人类所有者的指令与裁决下产出；人类负责方向决策、验收与法律边界，不逐行手写实现。
>
> 权威规格在 `docs/`，代码结构由 `docs/40-development-paradigm.md`（D-01~D-08）约束。
> 本文档只是仓库门面，与规格冲突时以 docs/ 为准。
>
> **CI 状态：从未执行过。** 本仓库的 `.github/workflows/petdock.yml` 从未被 GitHub Actions
> 触发、从未跑出过任何结果（在公开镜像上它会被推送触发）。本页不提供 CI 徽章，也不声称
> 任何一次构建通过。

PetDock 是一个「万物皆可插件」的桌宠宿主：**内核只负责把宠物画到桌面上并让插件说话**，
任何具体桌宠生态（自家格式、DyberPet、BongoCat 重写包、Live2D 元数据层 …）都作为独立插件存在。

## 现在的状态

已完成（各里程碑退出标准见 [docs/50-roadmap.md](docs/50-roadmap.md)）：

- **M0 内核骨架** — WinForms 宿主 + `WS_EX_LAYERED` 逐像素命中穿透 + 置顶 + Per-Monitor V2 DPI。
- **M1 插件宿主** — Tier 1 插件可加载/禁用/热重载，不改内核代码。
- **M2 ABI + 示例宠物** — 四接口 ABI、`docs/31` 示例宠物可循环播放、金图回归夹具。
- **M3 DyberPet 适配器** — 从用户自备的 DyberPet 角色包导入 → 标准包 → sprite-seq 呈现；
  保真度按 [docs/32](docs/32-dyberpet.md) §5 为**降级档**：静态展示、动作切换、悬停反应、
  定时动作可复现；养成 / 任务 / 商店 / AI 对话不可复现，且不在 M3 范围内。
- **M4 行为与事件** — 事件总线五种分发模式 + 14 条事件目录 + 输入钩子授权门 + Brain 指令仲裁；
  生产链上发布 `core.clock.tick`、初始化行为层并回填窗口句柄，因此「宠物对时间 / 鼠标 /
  前台窗口三类事件做出可见反应」在常驻真实链上成立（证据见 `docs/handoff/M4-VERIFY-r2.md` §12）。
  本里程碑同时把自检面搬出内核源码树，使内核代码行数从 10,811 回到 8,031
  （[docs/rfcs/RFC-07](docs/rfcs/RFC-07.md)）。

- **M5 SDK + CLI** — SDK 类型绑定 + CLI 六命令（`tools/petdock-cli/`，commit 667024b）。

进行中：**M7** BongoCat + Tier 2 —— tier 0（BongoCat 交互重写 + [RFC-09](docs/rfcs/RFC-09.md)）
已提交（ef30cf8）；Tier 2 沙箱写一半（`packages/core/PetDock.Core/src/Sandbox/Tier2Sandbox.cs`，未提交）。
未开始：**M8** 稳定性加固、**M9** 打包发布。
M6 插件市场按 [docs/50](docs/50-roadmap.md) 推迟。

**已知欠账**：单元测试覆盖率门禁 D-10a 目前是**棘轮**（`docs/41` D-10a、`docs/rfcs/RFC-04`）——
判定线是「不得低于 `tools/coverage-baseline.json` 登记的实测基线（line 5.09% / branch 4.05%）」，
而不是 80%/70% 的绝对目标；后者降级为 M4/M8 的里程碑目标，每次运行以
`COVERAGE-PENDING` 持续报告。当前覆盖率低是因为 D-10b 禁止单元测试触达真实窗口/GPU/文件系统。

## 构建

前置：.NET 8 SDK（`net8.0-windows`），Windows 10 1809+。

```powershell
dotnet build PetDock.sln -c Release
```

## 运行与自检

```powershell
# 常驻运行：一个示例宠物停在屏幕右下角
dotnet run --project packages/core/PetDock.Core -c Release

# 自检（分配控制台，跑完后输出逐像素命中穿透的证据并退出）
dotnet run --project packages/core/PetDock.Core -c Release -- --selftest

# 导入一个用户自备的 DyberPet 角色包（不复制素材，只引用）
dotnet run --project packages/plugins/petdock.importer.dyberpet/PetDock.DyberPetImporter -c Release -- --in <角色包目录> --out <输出目录>
```

`--selftest` 会检查：窗口已置顶、`WS_EX_LAYERED` 生效、透明区域鼠标穿透（`WindowFromPoint` 落到桌面而非宠物窗）、
不透明区域可交互、DPI 感知已设为 Per-Monitor V2，并打印 CPU 占用。
`--selftest` 需要在**可交互桌面会话**里运行（它会真的创建一个窗口并做逐像素命中测试）。

## 目录

| 路径 | 内容 |
|---|---|
| `packages/core/` | 内核（C# / WinForms）。只做窗口合成与插件宿主，不含任何具体桌宠生态 |
| `packages/sdk/` | 插件 SDK |
| `packages/plugins/` | 第一方与第三方插件，同等待遇 |
| `tests/` | 跨包集成测试、ABI 契约夹具与金图基线 |
| `tools/` | 仓库门禁与素材生成脚本（`docs/40` 的机器可执行规则） |
| `docs/` | 全部规格文档（00~52 + `rfcs/`） |
| `docs/handoff/` | 里程碑交接/契约/验证档案（M1~M4 过程记录；本机存档不入库，入口见 `docs/98-ai-handoff.md`） |
| `assets/sample-pet/` | 自建示例宠物（CC0，可用 `node tools/gen-sample-pet.mjs` 重新生成） |

## 许可
内核 **Apache-2.0**，完整条款见 [LICENSE](LICENSE)，第三方组件声明见 [NOTICE](NOTICE)。
插件是独立作品、各自许可（须在 manifest 的 `license` 字段声明）。

**素材零分发**：本仓库不含任何第三方桌宠素材。用户自备的角色包（例如 DyberPet 角色包）只在
运行时被**只读引用**，既不进仓库也不进安装包；测试用的宠物夹具全部由 `tools/` 下的脚本程序化生成。
应用图标也是自绘的：`tools/make-icons.py` 画的白脸猫耳占位图（随内核以 Apache-2.0 授权，
可随发行物分发）——所有者自己的图标源图放在本机 `assets/user/`（已 gitignore，永不发行）。
