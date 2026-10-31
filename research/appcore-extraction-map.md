# AppCore 抽取映射（R1 设计输入）

> 目的：在新 UI 动手之前，把 `UiHost` 里「宿主服务」与「界面接线」分开。
> 判据：抽完之后，新层只依赖 AppCore；旧层退化成「AppCore + 旧 UI 的适配器」。

## 现状（实测）

`UiHost` 由 **21 个 partial** 组成，合计约 **3,830 行**（`packages/core/PetDock.Core/src/Ui/UiHost*.cs`），
它同时是**组合根**和 WinForms 泵的宿主（`UiHost : ApplicationContext`，`UiHost.cs:24`）：

| 文件 | 行 | 现在干的事 | 归属 |
| --- | --- | --- | --- |
| `UiHost.cs` | 380 | 组合根：建设置/插件宿主/导入/帧循环/宠物集/托盘/面板 | **AppHost（新）** |
| `UiHost.Pets.cs` / `.Pets.Desktop.cs` | 337 | 宠物实例：加、删、复制、上/下桌面 | **服务** |
| `UiHost.Position.cs` | 236 | 位置持久化与恢复 | **服务** |
| `UiHost.Panel.Plugins.cs` | 268 | 插件启用/停用/安装/删除（判定与落账） | **服务** |
| `UiHost.Update.cs` | 240 | 检查/下载/安装更新（三态） | **服务** |
| `UiHost.Market.cs` | 141 | 拉索引 + 从市场装 | **服务**（出网） |
| `UiHost.Work.cs` / `.Progress.cs` | 432 | 长任务的执行、取消、进度文案 | **服务执行 + UI 进度**（拆） |
| `UiHost.Companions.cs` / `.Celestial.cs` / `.Hook.cs` | 488 | 陪伴层、天体模式、键盘钩子 | **服务** |
| `UiHost.Diagnostics.cs` | 78 | 诊断/审计/导出 | **服务** |
| `UiHost.Metadata.cs` | 40 | 宠物元数据装饰 | **服务** |
| `UiHost.Appearance.cs` / `.Language.cs` | 112 | 材质/亮度/语言 + **重建面板** | **服务（应用）+ UI（重建）** |
| `UiHost.Panel.cs` | 327 | 开面板、取快照、授权复核弹窗 | **UI 接线**（快照构造是服务，弹窗是 UI） |
| `UiHost.Tray.cs` | 339 | 托盘菜单模型 + 建/刷新托盘 | **UI 接线** |
| `UiHost.UiPages.cs` | 187 | 实现旧 `IUiPageContext` + 发现页面插件 | **UI 接线** |
| `UiHost.Onboarding.cs` | 151 | 引导窗与引导状态 | **UI 接线**（一次性标记是服务） |
| `UiHost.Restart.cs` | 74 | 重启（自我交接） | **服务** |

## 目标形状

```
AppHost（新，组合根，无 UI 类型）
  ├─ 服务：SettingsStore · PluginHost · PluginInventory · PetImportService
  │        MarketClient · UpdateService · Diagnostics · AuditTrail
  │        I18n(数据) · PetRegistry(实例/桌面/位置) · CompanionRegistry
  ├─ 运行时：ResidentFrameLoop + FramePump + PetWindowSet（合成器，允许 WinForms）
  ├─ 意图 API：返回 `string?`（null=成功）或 `Task<結果>`，**不弹窗、不碰 UI**
  └─ 事件：`PetsChanged` / `PluginsChanged` / `SettingsChanged` / `WorkChanged` / `UpdateStaged`

旧 UI（R8 前）：UiHost = AppHost + 旧面板/托盘/对话框（把上面每个调用转成一行委托）
新 UI（R1 起）：PetDock.Ui = AppHost + 新壳/新页面
```

### 三条硬约束

1. **AppHost 不引用 `PetDock.Core.Ui`**（旧命名空间），也不引用 `PetDock.Ui`（新壳）。
2. **AppHost 的方法不弹窗、不写状态栏、不碰 `I18n` 的 UI 用法**：失败原因用「机器码 + 已本地化文案」二选一，
   由 UI 决定怎么说。现有方法返回 `string?` 的做法保留（返回的是**给用户看的一句话**，属于服务边界内的既有约定）。
3. **事件只报「什么变了」，不带 UI 文案**。

## 抽取顺序（每步编译绿、行为零变化）

| 步 | 做什么 | 完成标志 |
| --- | --- | --- |
| R1a | 新建 `PetDock.AppHost` 项目；把「纯服务」partial 里的实现搬进 `AppHost`，原 `UiHost` 成员改成一行委托 | `dotnet build` 绿；`--uitest` 295 条不变 |
| R1b | 把宠物运行时（PetWindowSet / Position / Pets.Desktop / 帧循环）搬进 AppHost | 同上；`--selftest`/`--residenttest` 绿 |
| R1c | 旧 `IUiPageContext` 的实现改为「读 AppHost」；删掉搬空的 partial | 同上；LOC 净 0（搬移） |
| R1d | `Program.Main` 分叉：`--ui next` → WPF 泵 + `PetDock.Ui` 空壳；否则旧路径 | 两条路径都能启动；旧路径零变化 |

## 风险与对策

| 风险 | 对策 |
| --- | --- |
| 搬移期 295 条判据跟着崩 | 每步只搬移不改变语义；判据与实现在同一提交里改（只改命名空间/调用面） |
| `UiHost` 是 `ApplicationContext`（WinForms 泵的宿主） | R1d 之前保持它在旧路径原位；新路径另建 `AppHost.Run()`，两者共享服务实例的构造代码 |
| 事件在后台线程触发 | AppHost 事件统一「在调度线程派发」：`RunOnUi` 由宿主注入，服务只 `Post(...)` |

## 与新层契约的关系

新层的能力面（`IPetsSurface` / `IPluginsSurface` / …）**不是** AppHost 的接口，
而是 `PetDock.Ui.Contracts` 里、由壳从 AppHost 适配出来的**更窄**接口：

```
AppHost.Pets.Remove(id)            ← 服务：真的删
IPetsSurface.RequestDelete(id)     ← 契约：带确认与撤销票据
```

这样「确认/撤销/文案」这些**界面策略**永远不在服务里，服务保持可被判据直接驱动。
