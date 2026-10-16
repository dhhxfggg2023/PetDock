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
// 依赖方向仍是 docs/40 §2 允许的 sdk→core 一层（RANK 1→0，差值 1），
// D-02c/D-02d 都不触发；plugins→core 仍无直达边。
//
// M2（t3）：ABI 四接口从 M0 的两个内容源接口扩展到 docs/11 §13 的五个类型 ——
// IPetView / IBrain / IPointerSurface / IPetState 四个 ABI 角色接口，加上
// IClipCatalog（可选声明接口，docs/11 的「片段清单」：SpriteSeqView 同时实现它，
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
