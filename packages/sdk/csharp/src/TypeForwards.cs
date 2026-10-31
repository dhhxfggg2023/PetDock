// 把 core 的两个 ABI 类型原样转发给上层（plugins）。
//
// t6 修复（2026-10-16）：t2 曾让本工程零 ProjectReference，把 core 的 IFrameContentProvider /
// IFrameContentSource 按同 namespace 同签名重声明一遍做「投影」。这在 C# 里不成立：
// 同名同命名空间的 interface 编译进不同程序集就是**两个不同的 CLR 类型**，
// typeof(IFrameContentProvider).IsAssignableFrom(type) 恒为假 ——
// 插件实现了 PetDock.Sdk.dll 里那一份，内核 FrameContentLoader.cs:79 用 typeof(...)
// 检定的是 PetDock.Core.dll 里那一份，两个候选程序集全部被跳过，
// 桌宠静默退回纯色兜底（见 .dsh-code/mistakes.md 2026-10-16 条目）。
//
// 正确做法：本工程引用 core（见 PetDock.Sdk.csproj 的 ProjectReference 注释），
// 对 ABI 类型做类型转发，src/ 下**不再有任何重复的 interface 声明**。
// 类型转发让「插件经 sdk 引用」解析到的仍是 core 里那个唯一类型：编译期签名一致、
// 运行期 IsAssignableFrom 为真，且将来 Linux/Mac 下由 core 发布的版本改动会自动生效。
//
// 依赖方向仍是 原规格 40 §2 允许的 sdk→core 一层（RANK 1→0，差值 1），
// D-02c/D-02d 都不触发；plugins→core 仍无直达边。
//
// M2（t3）：ABI 四接口从 M0 的两个内容源接口扩展到 原规格 11 §13 的五个类型 ——
// IPetView / IBrain / IPointerSurface / IPetState 四个 ABI 角色接口，加上
// IClipCatalog（可选声明接口，原规格 11 的「片段清单」：SpriteSeqView 同时实现它，
// --animtest 的 C1 才能读到实现方声明的帧数）。五个类型**全部声明在 core**
// （packages/core/PetDock.Core/src/PetRuntime/PetView.cs:60、Brain.cs:170、
// Interaction.cs:47、State.cs:12、ClipCatalog.cs:15），本工程一条都不重声明。
//
// 转发的两个方向同时成立：
//   编译期：插件 `using PetDock.Core.PetRuntime;` 经本工程的传递引用看到的就是 core 那一份；
//   运行期：`Assembly.LoadFrom` 装载插件 dll 后，typeof(IPetView) 解析到 core 里唯一那条
//           CLR 类型（M1 t6 修复的 IsAssignableFrom 恒假问题在这里同样成立）。
// 这使「插件只引用 sdk、且将来关掉传递引用」仍是唯一能工作的形态。
using System.Runtime.CompilerServices;

[assembly: TypeForwardedTo(typeof(PetDock.Core.Compositor.IFrameContentProvider))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.Compositor.IFrameContentSource))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.IPetView))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.IBrain))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.IPointerSurface))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.IPetState))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.IClipCatalog))]

// 第三方素材包导入器（2026-10-28）：插件实现 IPackImporter，宿主按 kind=importer 装载。
[assembly: TypeForwardedTo(typeof(PetDock.Hosting.IPackImporter))]

// 可选转换进度（2026-10-29）：导入器实现 IPackImporterProgress 仍走同一条 ABI；
// 旧导入器不实现即可，不影响兼容。
[assembly: TypeForwardedTo(typeof(PetDock.Hosting.IPackImporterProgress))]
[assembly: TypeForwardedTo(typeof(PetDock.Hosting.PackImportProgress))]
[assembly: TypeForwardedTo(typeof(PetDock.Hosting.PackImportProgressNotify))]

// RFC-23 / 原规格 20 §3：插件日志面。插件写 `PluginLog.Info("...")`，归属由宿主开作用域决定。
// 转发的是 core 里那唯一一份（与上面七个同一模式）：插件经 PetDock.Sdk 解析到的
// 就是内核实际在用的类型，不是一份同签名的副本。
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.PluginLog))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.PluginLogLevel))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.IPluginLogSink))]

// 2026-10-28「一切皆可插件」：页面插件也走同一条转发。壳只认这三个契约与只读快照，
// 页面实现在插件里；不转发会让插件编译期直接绑到 Core.dll，依赖方向悄悄退化成 plugins→core。
[assembly: TypeForwardedTo(typeof(PetDock.Core.Ui.IUiPlugin))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.Ui.IUiPage))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.Ui.IUiPageContext))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.Ui.UiSnapshot))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.Ui.PetLine))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.Ui.PluginLine))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.Ui.PetReading))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.Ui.SettingsView))]

// 陪伴层 ABI（2026-10-29，P4 第一块）：与上面同一模式 —— core 声明、sdk 转发、插件实现。
// 转发 IPetCompanion 是必须的：宿主用 typeof(IPetCompanion).IsAssignableFrom(type) 检定类型，
// 不转发会让插件实现的是另一份同名接口，检定恒假（t6 的教训，见文件头）。
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.IPetCompanion))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.CompanionContext))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.ICompanionHost))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.CompanionGesture))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.CompanionTick))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.CompanionReport))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.PetRuntime.CompanionCommand))]

// 面板的陪伴行（IUiPageContext.Companions / InvokeCompanionCommand 的载荷），页面插件要用。
[assembly: TypeForwardedTo(typeof(PetDock.Core.Ui.CompanionLine))]
[assembly: TypeForwardedTo(typeof(PetDock.Core.Ui.CompanionCommandLine))]

// 应用契约（R1 起，PetDock.App）：界面层唯一认的那套服务面。同样走转发 ——
// 新页面插件实现/消费的是 core 里那唯一一份，不是同签名的副本（t6 教训见文件头）。
// PetDock.App.Contracts 由 core 引用、经本工程传递可见；此处不加新的 csproj 边。
[assembly: TypeForwardedTo(typeof(PetDock.App.AppResult))]
[assembly: TypeForwardedTo(typeof(PetDock.App.PluginTrashItem))]
[assembly: TypeForwardedTo(typeof(PetDock.App.MarketItem))]
[assembly: TypeForwardedTo(typeof(PetDock.App.MarketResult))]
[assembly: TypeForwardedTo(typeof(PetDock.App.UpdateCheck))]
[assembly: TypeForwardedTo(typeof(PetDock.App.UpdateInstall))]
[assembly: TypeForwardedTo(typeof(PetDock.App.UpdateInstallStage))]
[assembly: TypeForwardedTo(typeof(PetDock.App.AppSettings))]
[assembly: TypeForwardedTo(typeof(PetDock.App.WorkOutcome))]
[assembly: TypeForwardedTo(typeof(PetDock.App.WorkResult))]
[assembly: TypeForwardedTo(typeof(PetDock.App.WorkState))]
[assembly: TypeForwardedTo(typeof(PetDock.App.WorkProgress))]
[assembly: TypeForwardedTo(typeof(PetDock.App.CompanionButton))]
[assembly: TypeForwardedTo(typeof(PetDock.App.CompanionRow))]
[assembly: TypeForwardedTo(typeof(PetDock.App.IDiagnosticsService))]
[assembly: TypeForwardedTo(typeof(PetDock.App.IPetsService))]
[assembly: TypeForwardedTo(typeof(PetDock.App.IPluginsService))]
[assembly: TypeForwardedTo(typeof(PetDock.App.IUpdateService))]
[assembly: TypeForwardedTo(typeof(PetDock.App.IMarketService))]
[assembly: TypeForwardedTo(typeof(PetDock.App.ISettingsService))]
[assembly: TypeForwardedTo(typeof(PetDock.App.IWorkService))]
[assembly: TypeForwardedTo(typeof(PetDock.App.ICompanionService))]

