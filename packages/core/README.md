# packages/core —— 内核

> 本目录内容受 `docs/40-development-paradigm.md` 的 D-02/D-03 约束：
> 依赖方向只能是 `plugins → sdk → core`；内核源码里**禁止**出现任何具体桌宠生态的专有名词
> （`DyberPet`、`BongoCat`、`moc3`、`actions.xml`、`balloon`、`Live2D` 等，见 docs/01 P-02 与 docs/02 §2 黑名单），
> 命中即构建失败（D-03b）。内核 LOC 上限 8,000（D-03a），单文件上限 400 行（D-03c）。

当前只有 `PetDock.Core`：WinForms 宿主 + `WS_EX_LAYERED` 窗口合成器（docs/12）。
宠物内容、插件加载、行为逻辑都还没有，也不该出现在这里。
