// t5 独立验证夹具（验证者新加，非核心代码）：Tier 1 插件入口，与 my-first-brain/main.js 同为
// 纯 JS 入口形态 —— M1 宿主把入口字节嵌入 WASM 载体模块（导出空 tick），故本文件内容不参与执行。
// 存在意义：证明「往 packages/plugins/ 放一个新目录 → 宿主发现并装载」这条 drop-in 路径，
// 全程不改 packages/core 任何源码。
