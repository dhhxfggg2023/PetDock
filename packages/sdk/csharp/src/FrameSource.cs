// 本文件已废弃：t6 修复（2026-10-16）把 core 的两个 ABI 接口改成引用 core + TypeForwardedTo
// 类型转发（见 TypeForwards.cs），src/ 下不再保留任何重复的 interface 声明。
// 历史原因：t2 曾在此按同 namespace 同签名重声明 IFrameContentSource，编译进 PetDock.Sdk.dll
// 后与 PetDock.Core.dll 里那份形成两个不同的 CLR 类型，导致内核 typeof().IsAssignableFrom()
// 恒为假、桌宠静默退回纯色兜底。详见 .dsh-code/mistakes.md 2026-10-16 条目。

