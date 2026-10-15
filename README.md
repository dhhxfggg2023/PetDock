# PetDock — 桌宠内核（开发中，M0）
#
> 权威规格在 `docs/`，代码结构由 `docs/40-development-paradigm.md`（D-01~D-08）约束。
> 本文档只是仓库门面，与规格冲突时以 docs/ 为准。

PetDock 是一个「万物皆可插件」的桌宠宿主：**内核只负责把宠物画到桌面上并让插件说话**，
任何具体桌宠生态（自家格式、DyberPet、BongoCat 重写包、Live2D 元数据层 …）都作为独立插件存在。

## 现在的状态

- **M0 内核骨架**：WinForms 宿主 + `WS_EX_LAYERED` 逐像素命中穿透 + 置顶 + Per-Monitor V2 DPI。
- 尚未完成：插件宿主（M1）、ABI 与示例宠物（M2）、DyberPet 适配器（M3）、行为事件（M4）、SDK/CLI（M5）。
- 里程碑与退出标准见 [docs/50-roadmap.md](docs/50-roadmap.md)。

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
```

`--selftest` 会检查：窗口已置顶、`WS_EX_LAYERED` 生效、透明区域鼠标穿透（`WindowFromPoint` 落到桌面而非宠物窗）、
不透明区域可交互、DPI 感知已设为 Per-Monitor V2，并打印 CPU 占用。

## 目录

| 路径 | 内容 |
|---|---|
| `packages/core/` | 内核（C# / WinForms）。只做窗口合成与插件宿主，不含任何具体桌宠生态 |
| `packages/sdk/` | 插件 SDK（M5 起） |
| `packages/plugins/` | 第一方与第三方插件，同等待遇 |
| `tests/` | 跨包集成测试与金图夹具（M2 起） |
| `tools/` | 仓库内开发工具：素材生成、功耗基准、依赖扫描 |
| `docs/` | 全部规格文档（00~52） |
| `assets/sample/` | 程序化生成的占位素材，非第三方资产 |

## 许可

内核 Apache-2.0（完整许可文本在 M9 打包阶段随发行物落盘）。插件与素材各自许可，架构上插件为独立作品。
图标与示例素材**零第三方分发**：`assets/` 下的内容全部由 `tools/` 内脚本程序化生成。
