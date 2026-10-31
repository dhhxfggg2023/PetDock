# 界面层重构方案（UI Rework，2026-10-31 提案）

> 结论先行：现在的界面**不是**「万物皆可插件」，它是「三个页面是插件，其余全是硬编码」。
> 这句话不是批评，是事实描述 —— 壳的契约只有一个全能的 `IUiPageContext`，而托盘、宠物右键、
> 气泡、对话框、引导、设置分区、诊断窗口、市场，全部是内核里的 WinForms/WPF 代码。
> 本方案把它拆成「壳 / 表面 / 提供者」三层，按可独立发布的切片推进，每片都带判据。

---

## 0. 本文的三条纪律

1. **每条结论都要能核对**：带 `文件:行` 的写进来；只是推理出来的标 ⚠️ 并写清「怎么证」。
2. **要推翻就明说**：与既有结论冲突的地方单独列在 §2.6，写清代价。
3. **每片都能单独发版**：任何一片失败，前面的片仍然能用；不存在「改到一半界面不能开」的中间态。

---

## 1. 现状（可核对的数字）

| 项 | 数字 | 出处 |
| --- | --- | --- |
| 内核 UI 层 | **9,633 行**（49 个 .cs） | `packages/core/PetDock.Core/src/Ui/**` |
| 默认界面插件（3 页） | **2,202 行**（.cs + .xaml） | `packages/plugins/petdock.ui.default/**` |
| 页面契约 | `UiContracts.cs` 353 行，**36 个成员**横跨 6 个领域 | `UiContracts.cs:157-332` |
| 页面扩展点 | 只有 1 个：`contributes.pages` | `UiPluginCatalog.cs:82` |
| 窗口层判据 | 295 条（其中壳本身 25 条用假页面，`UiWorkspaceChecks`） | `--uitest` 末行 |
| 导航 | 左侧固定列表 + `Order` 整数排序 | `UiWorkspaceView.xaml.cs:192` |
| 面板几何 | **不记忆**：每次 `CenterScreen` + 固定 1040×680 | `ControlPanel.cs:28-31` |
| 主题 | 内核 `Theme` + 17 个 `Pd.*` 资源键 | `UiWorkspaceView.xaml.cs:236-252` |
| 文案 | 静态全局表，**插件无法贡献自己的文案** | `I18n.cs:36-78` |

### 1.1 运行期生命周期（谁建、谁销）

```
Program.Main
 └─ UiHost（常驻：TrayHost / PetWindowSet / 帧泵 / 插件宿主）
     ├─ 托盘「打开面板」──► UiHost.OpenPanel()
     │    ├─ I18n.Load(设置里的语言)        ← 语言热切换在这里
     │    ├─ LoadUiPages()   ← 每次打开都重新发现 + 新建页面实例
     │    └─ new ControlPanel(this, pages)  ← WinForms Form + ElementHost(WPF)
     │         └─ UiWorkspaceView(pages, this) ← 订阅 context.Changed
     ├─ 页面 CreateView() 缓存进 _views，永不释放（除整窗销毁）
     └─ FormClosed ──► _panel.Dispose(); _panel = null
```

**结构性疑点**：`UiWorkspaceView` 在 `Unloaded` 里退订（`UiWorkspaceView.xaml.cs:60`），
但 `ControlPanel.Dispose()` 走的是 WinForms 销毁路径，**WPF 子树的 `Unloaded` 是否一定触发没有判据**。
⚠️ 假设：若不触发，每次开关面板都会让旧 workspace（连同全部页面视图与位图）被 `UiHost.UiChanged`
事件长期持有 —— 开关 50 次就是 50 份页面树。S0 第一件事就是把它变成可测事实。

---

## 2. 我认定的问题

### 2.1 A 类：结构性（「万物皆可插件」名不副实）

| # | 问题 | 证据 | 影响 |
| --- | --- | --- | --- |
| A1 | 只有一个表面（整页）；托盘、宠物右键、气泡、引导、设置分区、诊断、市场都不是扩展点 | `UiHost.Tray.cs:106`、`PetWindowSet.ContextMenu.cs:16`、`OnboardingWindow.cs`、`SettingsPage.xaml.cs` | 任何「加一个入口」都要改内核；第三方想加托盘项无路可走 |
| A2 | 契约是**一个** 36 成员的全能接口 | `UiContracts.cs:157-332` | 页面拿到 6 个领域的写权限（删宠物、装插件、改设置），却没有声明与裁剪 |
| A3 | 插件无法贡献文案：`T()` 只查内核静态表 | `I18n.cs:72-78` | 第三方页面只能写死中文或复用内核键说出错误的话 |
| A4 | 图标是一个 emoji | `IUiPage.Glyph`（🐾 / 🧩 / ⚙） | 字形因机器而异、读屏念表情名、第三方无从对齐 |
| A5 | 设置页把外观/性能/语言/更新/诊断/关于塞进一页 396 行 | `SettingsPage.xaml.cs` | 用户找不到；改一处动整页；分区无法由插件贡献 |
| A6 | 「插件页」一页承担「已装 / 市场 / 回收站」三种心智 | `PluginsPage.Market.cs`、`PluginsPage.xaml:283` | 三种任务互相打断（打开市场就离开列表） |

### 2.2 B 类：契约与语义（误解点）

| # | 问题 | 证据 | 影响 |
| --- | --- | --- | --- |
| B1 | **破坏性操作要不要确认，由页面自己决定**，内核不强制 | `UiContracts.cs:196` 明写「确认框由页面自己弹」 | 一个忘了弹框的页面就能一键删宠物：这是功能滥用的入口 |
| B2 | 删除宠物**不可逆**，删除插件却进回收站 | `UiHost.Pets.cs:64` vs `PluginInventory.cs:231` | 同类操作两套后果，用户形成不了稳定预期 |
| B3 | 「停用插件」写的是「重启生效」 | `UiContracts.cs:214` | 用户点完看不出变化，以为没生效 |
| B4 | 「授权」实为**收窄**，运行期不许新增 | `UiHost.Panel.cs:249-325` | 界面上是权限勾选框，语义是「只能减不能加」，第一次用必误解 |
| B5 | 状态条是全局唯一出口，任何页面都能盖掉别人的结果 | `UiWorkspaceView.xaml.cs:108` | 一次 Refresh 就能把失败提示冲掉 |
| B6 | `Changed` 是全局事件，页面一律整表重建 | 三页的 `OnChanged → Refresh()` | 改一只宠物的大小会让插件页重建；插件多时是 O(页数×数据量) 抖动 |
| B7 | 契约没写线程约束，但 `SetStatus` 直接碰 WPF | `UiHost.UiPages.cs:30-44` | 页面在后台线程调它就是跨线程访问，今天没炸只是没人这么写 |

### 2.3 C 类：资源与生命周期（内存泄漏面）

| # | 问题 | 证据 | 影响 |
| --- | --- | --- | --- |
| C1 | 视图缓存**无上限、无淘汰、无 Dispose**；`IUiPage` 没有释放语义 | `UiWorkspaceView.xaml.cs:24,82-86` | 访问过的页永远活着；页里的图片/计时器无法回收 |
| C2 | ⚠️ 面板关闭是否触发 WPF `Unloaded` 未证 | `ControlPanel.cs:58-65`、`UiWorkspaceView.xaml.cs:60` | 若不触发，每次开关面板泄漏一整棵页面树 |
| C3 | 页面 `OnChanged` 用 `Dispatcher.BeginInvoke`，**无合并** | `PetsPage.xaml.cs:75-76` 等三处 | 后台高频 Refresh 会灌满 UI 队列（自己 DoS 自己） |
| C4 | 长任务取消源是 UiHost 的私有字段，页面拿不到 token | `UiHost.Work.cs:29-49` | 插件自起的后台活没人管，关窗后仍在跑 |
| C5 | 语言/材质切换 = Dispose 整窗重建 | `UiHost.Language.cs:57-66`、`UiHost.Panel.cs:34-40` | 滚动位置、选中行、未提交输入全丢；重建又踩 C1/C2 |
| C6 | `Theme.Apply` 每次调用都挂一个 `DpiChanged` 处理 | `Design/Theme.cs:208` | 对同一窗体重复 Apply 会重复缩放（今天侥幸只调一次） |

### 2.4 D 类：交互与可达性（人类习惯）

| # | 问题 | 影响 |
| --- | --- | --- |
| D1 | 面板不记忆尺寸/位置，永远弹在屏幕正中 | 用户调过的窗口下次消失；多屏尤其难受 |
| D2 | 没有键盘路径：无 Ctrl+1..9、无 Esc、无 Ctrl+K、无显式焦点顺序 | 电源用户与无障碍用户都被挡住 |
| D3 | 导航没有分组；插件页一多，左栏是一条无限长的列表 | 「装 5 个插件后界面变成垃圾桶」 |
| D4 | 不记忆上次停在哪一页 | 高频用户每次都要点两下 |
| D5 | 空态/加载态/错误态没有统一形态，每页各写一套 | 观感不一致，新增页面要重写一遍 |
| D6 | 有页头但没有统一的动作区 | 同一个「导入」在三页里是三套摆法 |

### 2.5 E 类：死代码与残留（顺手清）

| # | 东西 | 证据 |
| --- | --- | --- |
| E1 | `ControlPanel(..., bool showWindow)` 参数**从未被使用** | `ControlPanel.cs:24` 与 `UiHost.Panel.cs:48` |
| E2 | `UiWorkspaceView.OnContextChanged` 是空方法，却订阅了 `Changed` | `UiWorkspaceView.xaml.cs:59,264-268` |
| E3 | 文案前缀 `cp.*` 与 `ui.*` 两套并存 | `assets/i18n/**`（409 条） |
| E4 | 市场错误码翻译由页面自己维护一份 | `PluginsPage.Market.cs` |

### 2.6 我要推翻的六条既有结论

| 既有结论（今天的实现） | 改成 | 代价 |
| --- | --- | --- |
| ① 一个 `IUiPageContext` 给所有页面 | 六个能力面，页面**声明**要哪些 | 契约破坏；靠 `uiApi.major` 与适配层过渡 |
| ② 页面自己弹确认框 | **宿主强制**：破坏性命令走 `RequestDelete*`，确认与撤销由壳提供 | 页面少一点自由，多一点安全 |
| ③ 视图缓存永不淘汰 | 默认 LRU(4) + `KeepAlive` + 淘汰即 `Dispose` | 页面要实现 `OnActivated/OnDeactivated` |
| ④ 切语言重建整窗 | 原地重绑（文案是键查询，页面在 `OnLanguageChanged` 里重读） | 壳要能通知所有存活页面 |
| ⑤ emoji 当图标 | `IconName` + 内置矢量集 + 首字母兜底 | 需要一套图标资源 |
| ⑥ 设置一页装六件事 | 设置 = 分区容器，各段由内建插件贡献 | 设置页拆成 4–5 个文件 |

---

## 3. 目标架构

### 3.1 三层

```
┌──────────────────────────────────────────────────────────┐
│ Shell（壳）：窗口 / 导航 / 主题 / 焦点 / 工作队列 / 通知   │  ← 不认识任何业务
├──────────────────────────────────────────────────────────┤
│ Surfaces（表面）：page · settings-section · tray-item ·    │  ← 插件贡献，manifest 声明
│   pet-menu-item · onboarding-step · status-source ·        │
│   command                                                  │
├──────────────────────────────────────────────────────────┤
│ Services（宿主动词）：pets · plugins · market · update ·    │  ← 只有声明的能力面可见
│   diagnostics · settings · companions                      │
└──────────────────────────────────────────────────────────┘
```

**壳的四条硬规矩**：不认识业务名词；不直接读写磁盘；所有长活经 `IWork`；所有反馈经 `IStatusSink`。

### 3.2 表面契约（v1）

| surface | 接口 | 落点 | 能力门 | 备注 |
| --- | --- | --- | --- | --- |
| `page` | `IUiPage`（加 `Requires` / `IconName` / `KeepAlive`） | 左栏导航 | 每页显式声明 | 现有三页迁过来 |
| `settings-section` | `ISettingsSection` | 设置页分区 | `ui.settings` | 更新/诊断/关于各一段 |
| `tray-item` | `ITrayContribution` | 托盘菜单 | `ui.tray` | 默认关闭，需显式启用 |
| `pet-menu-item` | `IPetMenuContribution` | 宠物右键 | `ui.petMenu` | 同上 |
| `onboarding-step` | `IOnboardingStep` | 首次引导 | `ui.onboarding` | 现有 3 步改为内建贡献 |
| `status-source` | `IStatusSource` | 状态栏右端 | `ui.status` | 只给 `Text`，壳按 1 Hz 拉，禁止自建计时器 |
| `command` | `ICommandContribution` | Ctrl+K 命令面板 | `ui.commands` | 托盘/宠物菜单/页面共用一套动作 |

**清单形状不变**（重要）：现在的 `contributes` 值一律是「类型名数组」（`ManifestValidator.Parts.cs:75-96`）。
新表面沿用同一条：`contributes.tray = ["命名空间.类型"]`，顺序/分组/图标/启用条件由**契约对象自己报**。
于是校验器只需要扩白名单，不需要改 schema、不需要解析对象数组 —— 这也是「最少改动的正确做法」。

**明确不开放的表面**：任意窗口、任意对话框、任意钩子、可执行脚本、自建网络下载器。

### 3.3 能力面（把 36 个成员拆开）

```csharp
I UiShell        : SnapshotBase / Refresh / Status / Work / T
I PetsSurface    : Pets / ImportAsync / SetOnDesktop / Duplicate / RequestDelete / SetSize
I PluginsSurface : Rows / Toggle / Authorize / Install / Remove / Trash / Restore / Purge
I MarketSurface  : LoadAsync / InstallAsync
I UpdateSurface  : CheckAsync / DownloadAsync / InstallAsync(now) / Pending / LastReport
I DiagnosticsSurface : OpenLogs / ExportBundle / Audit
I SettingsSurface    : View / Apply(patch) / SetLanguage
I CompanionSurface   : Lines / Invoke
```

- 页面声明：`IUiPage.Requires => [UiCapability.Pets, UiCapability.Settings]`。
- 壳只注入声明过的；未声明就取 → **开发期抛异常**（宁可炸在开发期，不要运行时静默没功能）。
- 清单加 `uiApi: { major: 1, minor: 0 }`，运行时比对 major。

### 3.4 生命周期（让泄漏结构上不可能）

```csharp
sealed class PageScope : IDisposable
{
    CancellationToken Token { get; }   // 切页/淘汰/关窗 自动取消
    void Track(IDisposable d);         // 订阅、计时器、句柄都往这里登记
    void Subscribe(Action<UiEvent> h); // 域事件，退订自动
}
```

- `OnActivated(scope)` / `OnDeactivated()` / `Dispose()`；页面不再自己管订阅。
- 视图缓存：LRU 默认 4 页；`KeepAlive=true` 的内建页常驻；淘汰 = Dispose + 从树移除。
- 事件分域：`PetsChanged` / `PluginsChanged` / `SettingsChanged` / `WorkChanged`；
  壳**每帧合并一次**派发（`_pendingDispatch` + 单次 BeginInvoke）。

### 3.5 反馈三段式

| 出口 | 用途 | 规则 |
| --- | --- | --- |
| 页内状态条 | 「这一页刚才那步成没成」 | 每页独立；切走保留，回来还在 |
| 全局通知 | 跨页、后台发生的事 | 带历史（最多 50 条），可撤销的带 Undo |
| 工作条 | 长任务进度/取消 | 同时只允许一个；第二个被拒并说明 |

### 3.6 文案与图标

- 插件自带 `i18n/<lang>.json`，清单声明 `contributes.strings`；`T()` 解析 `@plugin.<id>:<key>`。
- 缺语言回落 `zh-CN` → 键名（与内核同口径）。静态判据：插件键必须带命名空间。
- `IUiPage.IconName`（pets/plugins/settings/market/…）+ 内置矢量集；未知名字回落首字母方块。

### 3.7 导航与信息架构

- 左栏按 `Group` 分组（核心 / 扩展 / 高级），组内按 `Order`。
- `Ctrl+K` 命令面板（跳页 + 执行命令）；`Ctrl+1..9` 切页；`Esc` 关面板；`Ctrl+Tab` 循环。
- 记忆：上次页 id + 窗口 bounds（越界夹取到当前屏幕工作区，复用 `ScreenPlacement` 口径）。
- 切页后焦点进入页面第一个可交互元素；`AutomationProperties` 由骨架控件统一设置。

### 3.8 破坏性操作与撤销（不可逆收敛为零）

- 统一回收站：宠物与插件都进 `data/trash/`（目录名带时间戳，与插件现口径一致）。
- 所有破坏性命令返回 `{ UndoTicket?, MustConfirm }`；`MustConfirm` 的命令**一定**经过壳的确认框。
- 可撤销的在通知区给 10 秒 Undo。

| 操作 | 可撤销 | 确认 | 说明 |
| --- | --- | --- | --- |
| 删除宠物 | 是（回收站） | 是 | 含最后一份素材也进回收站 |
| 删除插件 | 是（已有回收站） | 是 | 已实现 |
| 清空回收站 | **否** | 是 + 文字确认 | 唯一真正的不可逆 |
| 停用插件 | 是（再启用） | 否 | 但要说清生效时机 |
| 更新 | 是（备份保留） | 是 | 见 §2.8 的备份策略 |
| 重置设置 | 是（先导出旧值） | 是 | 提供「导出当前设置为文件」 |

### 3.9 线程与长任务

- 页面**不**自己 `Task.Run`；要后台就 `IWork.RunAsync`（壳负责取消/进度/异常翻译）。
- 判据（源码级）：`packages/plugins/**` 出现 `Task.Run` / `new Timer` / `Thread(` 判红 ——
  与现有 `ui-throttle-single-decision-owner` 同款扫法。
- 跨线程：能力面方法内部 `EnsureUiThread()`，非 UI 线程调用直接抛。

### 3.10 兼容与版本

- 清单加 `uiApi`；不匹配 → 该插件页面不建，状态栏说明原因。
- 今天只有内置三页是真实用户，**允许一次破坏式改动**（0.5.0 的 UI 契约），不留长期兼容层；
  适配层只在 S1/S2 内部存在，S7 删除。

---

## 4. 迁移切片

> 每片以「`verify --config release --only static,build,contract,window` 全绿 + 新判据绿 + LOC 记账」结束。

| 片 | 目标 | 主要改动 | 新判据 | 规模 |
| --- | --- | --- | --- | --- |
| **S0 观测** | 把假设变事实 | `UiProbe`（订阅数/视图数/派发次数）、WeakReference 泄漏探针、`PageScope` 空壳 | `ui-probe-*` 5 条（只测现状） | ~300 行测试 + 50 行壳 |
| **S1 契约拆分** | 页面只拿声明的能力 | 36 成员 → 8 接口 + 聚合实现 + `Requires`；三页改用切片 | `ui-caps-*` 3 条 | 契约 350→~450 行；三页各 30–60 行 |
| **S2 生命周期** | 泄漏结构上不可能 | `PageScope`、域事件、帧内合并、LRU + Dispose | `ui-life-*` 4 条 | 壳 ~400 行；三页各 20–40 行 |
| **S3 外壳 IA** | 导航/键盘/记忆/骨架控件 | 分组、Ctrl+K、Ctrl+1..9、Esc、几何持久化、`PdPageScaffold`、页内状态条 | `ui-nav-*` 5 条 | ~700 行（含 XAML） |
| **S4 设置与市场拆分** | 一页一任务 | 设置拆 5 区、市场独立页、更新/诊断/关于移出 | `ui-ia-*` 3 条 | ~800 行 |
| **S5 表面扩展点** | 托盘/宠物菜单/引导/命令可贡献 | 四个新契约 + 内核改造为内建贡献 + 清单校验 | `ui-surface-*` 5 条 | ~600 行 |
| **S6 插件文案与图标** | 第三方能说自己的话 | `contributes.strings`、`@plugin.` 解析、`IconName` 集、脚手架工具 | `ui-i18n-plugin-*` 2 条 | ~400 行 |
| **S7 清理** | 删旧 | 兼容层、`showWindow`、空订阅、`cp.*` → `ui.*`、文档 | 无新判据（`i18n-reachability` 必须仍为 0） | ~300 行删改 |
| **S8 纯 WPF 壳（可选）** | 去掉 ElementHost | `IShellWindow` → 纯 WPF + 托盘宿主窗 | 视 S0 数据再定 | 大，单独立项 |

**为什么这个顺序**：S0 先给数据；S1/S2 是地基（没有能力面与生命周期，后面每个表面都在复制同样的错误）；
S3/S4 是用户立刻能感知的（导航与信息架构）；S5/S6 才谈「万物皆插件」；S7 最后清场。
**S8 不在本方案承诺范围内** —— 它取决于 S0 测出的 DPI/输入缺陷是否真的来自混合宿主。

---

## 5. 判据（先写判据，再写实现）

| 判据 | 证明什么 | 怎么造 |
| --- | --- | --- |
| `ui-life-unsubscribe` | 开关面板 20 次后订阅数不变 | 计数 + WeakReference + GC |
| `ui-life-lru` | 访问 6 页后存活视图 ≤ 4，被淘汰的 Dispose 过 | 假页面 + 计数 |
| `ui-life-cancel-on-switch` | 切页后页面拿到的 token 已取消 | 假页面记录 |
| `ui-events-coalesced` | 1000 次 Refresh → 每帧 ≤1 次派发 | 计数派发 |
| `ui-events-scoped` | 改宠物不触发设置页重绑 | 假页面计次 |
| `ui-caps-declared` | 未声明的 surface 拿不到 | 假页面 |
| `ui-destructive-forces-confirm` | 页面绕过确认尝试删除 → 被壳拦下 | 假页面 + 记录 |
| `ui-trash-undo` | 删除宠物 → 回收站 → Undo 恢复 | 临时数据根 |
| `ui-nav-keys` | Ctrl+2 / Esc / Ctrl+K 真的生效 | 合成按键 |
| `ui-nav-persist` | 改 bounds/页 → 重开 → 一致；越界夹取 | 临时设置根 |
| `ui-status-per-page` | B 页写状态不冲掉 A 页的失败提示 | 假页面 |
| `ui-work-single` | 同时只一个长任务；第二个被拒并说明 | 假工作 |
| `ui-plugin-strings` | 插件自带文案生效；缺语言回落 | 假插件目录 |
| `ui-surface-manifest` | 未知/重复 surface 被校验器拒绝 | 清单夹具 |
| `ui-no-taskrun-in-plugins` | 插件源码不许自起后台 | 源码扫描 |

---

## 6. 风险与「我不做的事」

| 风险 | 缓解 |
| --- | --- |
| 契约破坏让第三方插件不能用 | 目前无第三方真实用户；`uiApi.major` 检查 + 内部适配层 |
| 295 条窗口判据大改 | 判据与实现在同一片里改；假页面夹具保留，只换注入方式 |
| 拆分后页面反而更难写 | S3 提供 `PdPageScaffold`，三页是第一批用户；写不出来就说明设计错了 |
| 范围蔓延到宠物渲染/物理 | **非目标**：宠物视图、物理、帧泵、DPI 感知口径不动 |
| LOC 预算（D-03a）爆 | 每片单独记账；拆分多为搬移；S7 净减 |

**明确不做**：声明式页面 DSL（tier1/2 的 UI 留给将来）、主题市场、插件脚本引擎、宠物窗口重写、
把托盘改成 WPF。

---

## 7. 需要你拍板的五件事

1. **第三方能否贡献托盘 / 宠物右键？** 推荐：能，但默认关闭 + 需要 `ui.tray` / `ui.petMenu` 能力，安装时明确告知。
2. **设置页拆成「分区容器」还是保持单页？** 推荐：拆（外观/性能/语言/更新/诊断/关于）。
3. **删除宠物改成「回收站 + 撤销」？** 推荐：是 —— 这是把不可逆变成可逆的最小改动。
4. **`cp.*` 文案统一迁到 `ui.*`？** 推荐：是（机械替换 + `i18n-reachability` 守住）。
5. **允许一次破坏式的 UI 契约变更（0.5.0）？** 推荐：允许 —— 现在只有一个内置 UI 插件，越早改越便宜。

---

## 8. 开工顺序

你点头之后我先做 **S0**（只加探针与判据，不改观感），把 §2.3 的 ⚠️ 假设变成数字；拿到数字再进 **S1/S2**。
这样即使后面全部推翻，S0 的探针也留下来成为永久门禁 —— 这也是本仓一贯的做法：
先把「怎么知道它没坏」建起来，再动它。
