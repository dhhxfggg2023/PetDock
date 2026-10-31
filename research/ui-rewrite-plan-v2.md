# 界面层重做方案 v2（Greenfield Rewrite）

> 约束变更（2026-10-31）：**旧界面层禁止复用，必须完全重做**。
> v1 的方案是「拆分 + 迁移」，作废；v2 是「另起一层、双轨、切换、删除」。
> 旧层只保留两样东西：**可执行的规格**（295 条判据）与**踩过的坑**（代码注释里的故障史），
> 一行代码都不进新层。

---

## 0. 一句话方案

新建一层纯 WPF 界面（新程序集、新契约、新设计系统、新页面），与旧层并行存在直到新层通过
「等价比对」；然后用一次切换把它变成默认，最后**物理删除**旧层。

---

## 1. 硬边界：删什么、留什么

### 1.1 删除清单（R8 一次性物理删除，不留兼容）

| 路径 | 规模 | 说明 |
| --- | --- | --- |
| `packages/core/PetDock.Core/src/Ui/**` | **9,633 行 / 49 文件** | 旧壳、页面宿主、WinForms 自绘控件（`Design/Themed*`）、托盘、对话框、引导、审计窗、气泡、主题 |
| `packages/plugins/petdock.ui.default/**` | **2,202 行** | 三个旧页面（Pets/Plugins/Settings）与市场第二态 |
| `UiHost` 里的 UI 部分 | 约 1.5k 行 | 面板/托盘/引导/对话框/语言的接线；业务部分搬到 AppCore |
| `ControlPanel` / `UiWorkspaceView` / `PetDockDialog` / `ConfirmDialog` / `AuthorizationDialog` / `AuditWindow` / `OnboardingWindow` / `TrayHost` | — | 全部 |

### 1.2 明确保留（不是「界面层」）

- **宠物窗口与合成器**：`Compositor/LayeredFrameWindow*`、`ScreenPlacement`、帧泵、物理、命中图。
  （它们是 Win32 分层窗，不是 WinForms 控件；新壳只负责「谁来开它、在托盘/菜单里怎么叫它」。）
- **宿主服务**：插件宿主、更新、市场、数据存储、审计、诊断、i18n 数据表（`assets/i18n/**`）、自检夹具。
- **判据**：旧判据是规格书，逐条搬迁或改写，不直接删。

### 1.3 「不许复用」怎么变成机械保证（不是靠自觉）

1. 新层是**新程序集** `PetDock.Ui`（放在 `packages/core/` 内，不新开 layer —— 避免改 depscan 的层级模型）；
   旧层留在 `PetDock.Core` 里，直到 R8 删除。
2. 新程序集 csproj **不允许** `UseWindowsForms`；加一条 lint：`packages/core/PetDock.Ui/**` 出现
   `System.Windows.Forms` / `PetDock.Core.Ui` 命名空间即判红（与现有 depscan 同款扫法）。
3. 新层与旧层**不共享任何类型**：契约放在 `PetDock.Ui.Contracts`，旧层不引用它；
   两边只共享 AppCore 的**服务接口**（§3.2）。
4. 旧层的删除是 R8 的验收条件：`git ls-files packages/core/PetDock.Core/src/Ui | wc -l == 0`。

---

## 2. 技术判断：必须换掉消息循环（这是重做的关键，不是可选项）

### 2.1 现状为什么必须换

现在是 **WinForms `Application.Run(UiHost)` + `ElementHost` 里嵌 WPF**（`Program.cs:363`、`ControlPanel.cs:36`）。
这个组合直接生出了旧层里一半的 workaround：

| 症状 | 旧层的补丁 | 出处 |
| --- | --- | --- |
| 面板最大化时系统 `MessageBox` 被宠物窗压在下面 | 自绘置顶确认框 | `PetDockDialog.cs` 注释 |
| WPF 与 WinForms 各自算 DPI | `Theme.Apply` 手工 `Scale` + `AutoScaleMode=None` | `Design/Theme.cs:195-222` |
| 换语言要拆整窗重建 | `Dispose` + `OpenPanel()` | `UiHost.Language.cs:57-66` |
| 两个消息循环 / 两套焦点 | ElementHost 的嵌套 dispatcher | 结构性 |

只要还留着 ElementHost，新层就会继承这四类问题 —— 所以「完全重做」必须包含**换泵**。

### 2.2 目标运行时

```
Program.Main
  ├─ （旧路径，R8 前保留）Application.Run(UiHost)                        ← WinForms 泵
  └─ （新路径）new Application().Run(new ShellWindow())                 ← WPF 泵 + 单一 Dispatcher
```

- 托盘：Win32 `Shell_NotifyIcon`（P/Invoke）+ WPF `ContextMenu`，**不引 WinForms**。
- 宠物窗口：仍是 `NativeWindow` 分层窗；WPF 的 dispatcher 泵同样泵 Win32 消息，
  **但这一点必须先证明**（§2.4 spike）。
- 单实例 / 更新交接 / 崩溃上报：时序不变，只是从 `Application.ThreadException` 换成
  `DispatcherUnhandledException`。
- `--uitest`：改用 `DispatcherFrame` 泵（替换现在的 `Application.DoEvents()`），
  其余「造现场—断言—收尾」的写法不变。

### 2.3 备选（若不换泵）

**B 案：纯 WinForms 自绘壳，无 WPF**。不用换泵，但要把布局、无障碍（UIA）、文本排版全部自己做 ——
正是旧层做得最差的三件事。**只有 spike 失败时才走 B 案。**

### 2.4 R0 spike（go / no-go，先做，不写产品代码）

在一个临时项目里证明 5 件事，结果写进 `research/ui-pump-spike.md`：

| # | 要证明 | 硬指标 |
| --- | --- | --- |
| 1 | 宠物分层窗在 WPF 泵下正常渲染 | 连续 300 帧无缺帧；`UpdateLayeredWindow` 不被抢 |
| 2 | 命中穿透/拖动/双击不变 | 复用 `--selftest` 的命中探针，逐项等价 |
| 3 | 托盘图标 + 右键菜单可用 | 图标回调进 WPF 菜单，菜单不抢焦点 |
| 4 | 单实例 / 关闭 / 退出干净 | 关窗后进程退出、无残留窗口 |
| 5 | `--uitest` 能在无 WinForms 的情况下驱动 | 泵 200 帧 + 合成点击 |

**任一项不达标 → 走 B 案，不硬上。** 这一条写在最前面，是为了不在 R3 才发现地基不对。

---

## 3. 新层蓝图

### 3.1 程序集与依赖

```
packages/core/
  PetDock.AppCore/          ← 无 UI 的宿主服务（从 UiHost 抽出来）
  PetDock.Ui.Contracts/     ← 表面契约 + 能力面 + 只读快照（只有类型，没有实现）
  PetDock.Ui/               ← 壳 + 设计系统 + 内建页面（纯 WPF，禁 WinForms）
packages/plugins/
  petdock.ui.default/       ← 重写：只贡献页面，不含壳
```

依赖方向：`Ui` → `Ui.Contracts` + `AppCore`；`ui.default` → `Ui.Contracts`；`AppCore` 不认识 `Ui`。

### 3.2 前置片：从 UiHost 抽出 AppCore

`UiHost` 今天约 3.5k 行，混了「宿主服务」与「界面接线」。重做界面之前必须先把服务抽出来，
否则新层要么复制它们，要么继续依赖旧层 —— 两条都违反约束。

AppCore 拥有：宠物实例与桌面状态、插件宿主与清单、市场、更新、设置、诊断/审计、语言表、
陪伴层、工作队列的**执行**（不含进度 UI）。**不认识任何窗口。**
旧 UI 在 R8 前降到「AppCore 的适配器」；新 UI 直接吃 AppCore。

### 3.3 契约（全部新写，不复用 `UiContracts.cs`）

- **表面**（9 个）：`page` / `settings-section` / `tray-item` / `pet-menu-item` /
  `onboarding-step` / `status-source` / `command` / `notification` / `dialog-request`。
  清单形状沿用现在的「类型名数组」（`contributes.tray = ["命名空间.类型"]`），
  顺序/分组/图标由契约对象自己报 —— 校验器只扩白名单。
- **能力面**（8 个）：Shell / Pets / Plugins / Market / Update / Diagnostics / Settings / Companions。
  页面用 `Requires` 声明；未声明的拿不到，取用即抛。
- **PageScope**：token + 订阅登记 + 句柄登记，切页/淘汰/关窗自动释放。
- **事件分域** + 帧内合并派发。
- **破坏性操作**：`{ UndoTicket?, MustConfirm }`，确认由壳强制、撤销在通知区。

### 3.4 设计系统（新写）

- 令牌：颜色/间距/圆角/字号/动效时长（一份 `tokens.json` 生成 C# 常量与 XAML 资源，避免两处漂移）。
- 控件：Button / Toggle / Slider / Table / EmptyState / Skeleton / Progress / StatusBar / Toast / Dialog。
- 图标：内置矢量集 + 首字母兜底（不再用 emoji）。
- 键盘与焦点：Tab 顺序、`Ctrl+1..9`、`Ctrl+K`、`Esc`、切页后焦点入正文。
- 无障碍：每个可交互元素都有 `AutomationProperties.Name`；由控件基类统一设置，不靠调用方自觉。

### 3.5 路由与信息架构

- 分组导航（核心 / 扩展 / 高级）+ 命令面板（`Ctrl+K`）。
- 深链 `petdock://page/plugins`（可选，R5 之后）。
- 记忆：上次页 + 窗口 bounds（越界夹取到工作区）。
- 页面骨架控件：页头 / 动作区 / 内容 / 空态 / 页内状态条 —— 三页共用一套。

### 3.6 文案

- 内核表 `assets/i18n/**` 保留（是数据不是代码），但新层**只读**，键名统一 `ui.*`。
- 插件可贡献 `i18n/<lang>.json`，`T()` 解析 `@plugin.<id>:<key>`；缺语言回落 zh-CN → 键名。
- 旧键 `cp.*` 在 R7 做一次机械迁移并逐条核对语义（旧键里存在**说错话**的：例如把「停用」说成立即生效）。

### 3.7 托盘 / 对话框 / 引导 / 审计查看器（重写，不复用）

- 托盘：Win32 图标 + WPF 菜单；菜单项来自 AppCore + `tray-item` 贡献。
- 对话框：单一 `ShellDialog` 基础设施（模态 + 置顶 + 键盘焦点 + 不遮宠物窗），确认/授权/批注三种形态。
- 引导：步骤由 `onboarding-step` 贡献，内建 3 步；可跳过、可重看。
- 审计查看器：只读表格 + 过滤；与 `AuditWindow` 无共享代码。

### 3.8 启动 / 关闭 / 更新时序（在新泵下重新推一遍）

| 时点 | 谁做 | 不变量 |
| --- | --- | --- |
| 单实例锁 | Main，早于任何窗口 | 抢不到就退出 0 并提示 |
| 更新交接 | Main，早于任何窗口 | 交接成功则本进程退出 |
| 壳创建 | WPF Application.Run | 任何窗口失败都不影响宠物窗口已存在 |
| 关面板 | 壳 | 只隐藏，不退出；宠物与托盘继续 |
| 退出 | 托盘/命令 | 先释放页面作用域，再释放宠物窗口，再停泵 |

---

## 4. 判据策略：旧 295 条是规格书

实测分类（由脚本按真实判据 id 生成，见 `research/ui-parity-matrix.md`）：

| 类别 | 条数 | 处理 |
| --- | --- | --- |
| **KEEP** 与界面壳无关（合成器 / 协议 / 后端 / 插件管理） | **200** | 原样保留，一条不改 |
| **PORT** 界面行为（pets / plugins / market / settings / work / update 三态 / 授权批量 / 插件删除入口） | **50** | 换新契约重写等价判据，**语义一条不许变** |
| **REWRITE** 旧壳特有（nav / panel / theme / modal / tray / onboarding / auth 弹窗） | **45** | 用新壳等价判据取代（更强：生命周期 / 合并 / 能力面 / 撤销） |
| 合计 | **295** | |
### 4.2 先写判据，再写页面

每个页面切片（R4–R6）的顺序固定为：**把该页的旧判据搬到新契约 → 跑红 → 写页面 → 跑绿**。
这样「完全重写」不会退化成「重写一半忘了旧行为」—— 尤其是那几条用血换来的语义：

- 收起之后宠物仍在清单里（`ui-pets-desktop-stays-listed`）
- 逐只尺寸而非全局（`ui-pets-*size*`）
- 授权只能收窄、不能新增（`ui-auth-*`）
- 更新三态分开（`ui-update-three-outcomes`）
- 删除宠物如实报「素材删不掉」（`cp.pets.delete.failed`）
- 市场失败分「离线 / 索引坏 / 没条目」三种（`ui-market-*`）

### 4.3 双轨期的 `--uitest`

`--uitest` 增加 `--ui next`：跑新壳的判据；不带参数跑旧壳（R7 之前）。
两套判据共用一个「造现场」夹具层（数据根、假插件目录、临时设置），避免两套夹具各自腐烂。

---

## 5. 切片 R0–R8

| 片 | 产出 | 判据 | 退出条件 |
| --- | --- | --- | --- |
| **R0 spike** | `research/ui-pump-spike.md` + 临时探针（不入产品） | 无（人工证据 + 数字） | §2.4 五项达标；否则改走 B 案 |
| **R0b 教训档案** | `research/ui-lessons.md`：把旧层注释里的故障史与新层必须保住的行为逐条抄出来（约 60 条） | 无 | 每条都有出处（文件:行 或 判据 id） |
| **R1 骨骼** | `PetDock.AppCore` 抽出（旧 UI 变适配器）、`PetDock.Ui.Contracts`、`PetDock.Ui` 空壳、`--ui next` | 骨架 5 条（起窗/退出/单实例/托盘/无 WinForms 引用） | 新旧两条路径都能启动；旧路径行为零变化 |
| **R2 设计系统** | tokens 生成器、控件库、图标集、焦点/键盘 | 设计系统 8 条（令牌一致、图标兜底、焦点顺序、对比度） | 三页可以只用控件库搭出来 |
| **R3 壳** | 导航/路由/PageScope/事件/工作/通知/页内状态/几何记忆/语言原地切换 | 壳 10 条 | 假页面能证明：切换 20 次订阅不增长、淘汰即释放、1000 次事件每帧一次 |
| **R4 宠物页** | 重写 Pets（含收起/放回、逐只尺寸、care、导入、删除+回收站） | 搬 ~12 条 + 新增撤销 2 条 | 旧 pets 判据全部在新契约下绿 |
| **R5 插件页 + 市场** | 重写 Plugins/Market（授权、回收站、安装、市场三态） | 搬 ~16 条 | 同上 |
| **R6 设置/更新/诊断/关于 + 托盘/对话框/引导** | 设置分区容器 + 更新三态 + 诊断 + 关于 + 托盘菜单 + 对话框基础 + 引导步骤 | 搬 ~24 条 | 同上 |
| **R7 切换** | 新层变默认；`--ui next` 开关删除；旧 window 判据退役 | 全套 `--uitest` | `VERIFY-OK layers=5/6`，新判据代替旧判据 |
| **R8 删除** | 删旧层 11.8k 行；lint 规则锁死；LOC 归还；文档 | `loc` + `depscan` | `ls packages/core/PetDock.Core/src/Ui` 为空；core LOC ≤ 双轨前基线 |

**并行机会**：R4/R5/R6 无共享可变状态（页面之间只共享契约），可在契约冻结后用子代理并行；
但每片必须独立跑绿再合并。

---

## 6. 预算与记账（D-03a）

- 双轨峰值：core 现有 38.2k + 新壳约 4–5k + AppCore 搬移（净 0）≈ **43k**。
- 迁移期在 `tools/core-loc-baseline.json` 里如实抬一次预算，并写明**归还条件**：R8 后必须 ≤ 38.2k。
- R8 删除旧 UI 9.6k，净结果约 **33k**（比今天低 5k）。
- 新增 lint：`LOC-TWO-TRACK-OVERDUE` —— 若 R8 未归还，预算条目过期即红（用日期 + 一条 history 记录表达）。

---

## 7. 风险与对策

| 风险 | 级别 | 对策 |
| --- | --- | --- |
| 换泵破坏宠物窗口/命中/性能 | 高 | R0 spike 先做；不达标走 B 案（纯 WinForms 新壳） |
| 双轨期两套判据腐烂 | 中 | 共用夹具层；R7 一次性退役旧判据 |
| 重写丢语义（收起/授权/三态…） | 高 | §4.2「判据先行」；R0b 教训档案逐条对照 |
| 长期没有界面新功能 | 中 | 明确冻结界面功能改动直到 R7（需你确认） |
| LOC 预算爆 | 低 | §6 的临时抬升 + 归还条件 + 过期判红 |
| 重写期间出错无法回退 | 中 | 旧层是默认路径，直到 R7；R7 只改一行开关 |

**不做**：声明式页面 DSL、脚本插件、主题市场、宠物窗口重写、把 AppCore 改造成插件。

---

## 8. 需要你拍板（这次只 4 条）

1. **接受换泵（WPF 独占 Dispatcher）吗？** 推荐接受，但**以 R0 spike 达标为前提**；不达标走 B 案（纯 WinForms 新壳）。
2. **冻结界面功能改动直到 R7 吗？** 推荐冻结 —— 否则每条新需求都要写两遍。
3. **允许一次破坏式契约变更（0.5.0，`uiApi.major=1` 从零开始）吗？** 推荐允许。
4. **删除时点**：R8 一次性删除、不留兼容层；若你希望「先留一个版本可回退」，我可以把 R8 拆成「R8a 关开关 / R8b 删除」。

---

## 9. 与 v1 的关系

- v1 的**问题清单**（20 条）仍然有效，作为「新层不许重复的错误」清单，已并入 `research/ui-lessons.md` 的待办。
- v1 的**契约设计**（能力面 / PageScope / 表面清单 / 撤销）被 v2 继承 —— 它们与「是否复用旧代码」无关。
- v1 被推翻的是**实施路径**：不再「拆分旧契约、迁移三页」，而是新起一层、按判据重建、最后删旧。
