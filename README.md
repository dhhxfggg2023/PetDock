# PetDock — 桌宠内核

> **本项目完全由 AI 生成。** 代码、门禁脚本与本文档由 AI 代理在人类所有者的指令与裁决下产出；
> 人类负责方向决策、验收与法律边界，不逐行手写实现。
>
> 本仓库不再包含 `docs/` 与 `tests/`：旧规格与旧测试已整体退役。**活的行为契约只有两处**——
> `packages/core/PetDock.Core/test/SelfCheck/` 里的可执行判据，以及插件 manifest 的校验器
> （`ManifestValidator`）。注释里出现的「原规格 NN」是历史设计依据，不是可执行契约。

PetDock 是一个「万物皆可插件」的桌宠宿主：**内核只负责把宠物画到桌面上并让插件说话**，
任何具体桌宠生态（自家格式、DyberPet、BongoCat 重写包、Live2D 元数据层 …）都作为独立插件存在。

## 现在的状态

- **内核** — C#：WinForms 分层窗负责桌面合成（`WS_EX_LAYERED` 逐像素命中穿透、置顶、
  Per-Monitor V2 DPI、空闲帧泵与单帧预算），WPF 负责主控面板；深浅两套色板跟随系统。
- **插件宿主** — tier 0/1/2 三类插件、能力授权门、WASM/WASI 沙箱、事件总线、生命周期与热重载。
- **示例宠物** — `sample-pet` 呈现器 + `PetDock.SamplePet` 素材包，首次运行即可看到一只在桌面上小范围散步的宠物。
- **主控面板** — WPF 壳 + 页面插件：导航、页头、状态条与主题由内核提供，宠物 / 插件 / 设置三页
  由 first-party 插件 `petdock.ui.default` 贡献；第三方界面插件可以加自己的页，壳不需要改。
- **插件管理** — 从界面安装（文件夹或 zip）、启用 / 停用、删除用户插件；内置插件不可删除，
  随包界面插件（清单 `essential: true`）不可停用——它是唯一的面板入口。
- **发布管线** — `node tools/release.mjs` + `node tools/pack.mjs`，产出解压即用的 portable zip。
- **门禁** — `npm run lint`（七道静态门禁）与 `node tools/verify.mjs`（六层：静态 → 构建 →
  窗口/事件自检 → UI 自检 → 性能 → 发布物自检）。仓库没有 CI 工作流，门禁在本机执行。

## 构建

前置：.NET 8 SDK（`net8.0-windows`）与 Node.js 20+，Windows 10 1809+。

```powershell
dotnet build PetDock.sln -c Release
npm run lint
```

## 运行与自检

```powershell
# 常驻运行：示例宠物从屏幕右下角开始散步，托盘出现
dotnet run --project packages/core/PetDock.Core -c Release

# 窗口/合成自检（会在可交互桌面会话里真的建窗并做逐像素命中测试）
dotnet run --project packages/core/PetDock.Core -c Release -- --selftest

# 主控面板截图回归（输出到 --shots 目录）
dotnet run --project packages/core/PetDock.Core -c Release -- --uitest --shots <目录>

# 校验插件清单（门禁与插件作者共用同一套规则）
dotnet run --project packages/core/PetDock.Core -c Release -- --validate-plugins <插件目录或根>

# 导入用户自备的 DyberPet 角色包（只读引用，不复制素材）
dotnet run --project packages/plugins/petdock.importer.dyberpet/PetDock.DyberPetImporter -c Release -- --in <角色包目录> --out <输出目录>
```

其余诊断入口：`--hosttest`、`--eventtest`、`--animtest`、`--drivetest`、`--perftest`、
`--residenttest`、`--composetest`。它们都编在同一个 `PetDock.Core.exe` 里，不额外发行。

## 插件从哪里来

- **内置**：`packages/plugins/`，随源码树提供，只读。
- **用户**：`<data>/plugins/`（便携发行版是程序旁的 `data\\plugins`，否则 `%LOCALAPPDATA%\\PetDock\\plugins`）。
  面板里的「安装插件…」把文件夹或 zip 解到 `.staging/`，校验清单与入口后再搬进正式目录；
  同名插件先备份为 `.bak-时间戳`。删除只允许针对用户插件。

### 启动时谁会申请授权

| 插件 | 默认 | 说明 |
|---|---|---|
| `petdock.brain.reaction` | 开 | 内置行为脑（清单声明 defaultBrain）：沿水平方向散步，鼠标经过时抬头（不申请输入钩子） |
| `com.petdock.brainnull` / `com.petdock.statenull` / `com.petdock.spriteseq` | 开 | 内核自带的空实现与示例呈现器 |
| `petdock.sample.petimage` | 开 | 示例宠物呈现器 |
| `com.petdock.bongocat.interaction` | **关** | 其他桌宠生态的适配插件，需要键盘钩子，默认不装载 |
| `com.petdock.dyberpet.importer` | **关** | DyberPet 导入器，用到时才开 |
| `com.petdock.myfirstbrain` | **关** | tier-1 JS 示例脑，用到时才开 |

规则是「显式停用 > 显式启用 > 默认」：默认开的插件不会弹授权窗（内置信任集静默授予），
默认关的插件只有用户主动启用时才走授权流程。这样**双击 exe 不会先弹一个授权提醒**。

## 目录

| 路径 | 内容 |
|---|---|
| `packages/core/` | 内核（C# / WinForms 窗口 + WPF 面板）：窗口合成、界面壳、插件宿主、事件总线、沙箱。不含任何具体桌宠生态 |
| `packages/core/PetDock.Core/test/SelfCheck/` | 可执行判据（编进同一个 exe，由诊断旗标驱动） |
| `packages/sdk/` | 插件 SDK（C# 与 JS 两侧的类型/模块面） |
| `packages/plugins/` | 第一方与第三方插件，同等待遇 |
| `tools/` | 门禁、发布与素材生成脚本 |
| `assets/` | 示例素材（CC0，可由 `tools/gen-sample-pet.mjs` 重新生成）、图标与 i18n |
| `packaging/` | 安装说明与便携发行相关文件 |

## 许可

内核 **Apache-2.0**，完整条款见 [LICENSE](LICENSE)，第三方组件声明见 [NOTICE](NOTICE)。
插件是独立作品、各自许可（须在 manifest 的 `license` 字段声明）。

**素材零分发**：本仓库不含任何第三方桌宠素材。用户自备的角色包（例如 DyberPet 角色包）只在
运行时被**只读引用**，既不进仓库也不进安装包；示例素材全部由 `tools/` 下的脚本程序化生成。
应用图标也是自绘的（`tools/make-icons.py`，随内核以 Apache-2.0 授权）；所有者自己的图标源图
放在本机 `assets/user/`（已 gitignore，永不发行）。
