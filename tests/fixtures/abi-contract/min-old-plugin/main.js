// D-11a 基线夹具的 entry 目标（与 packages/plugins/my-first-brain/main.js 同一形态）。
// M1 宿主把入口字节嵌入 WASM 载体模块，本文件内容不参与执行；它的存在意义是
// 「这份旧清单的 entry 指向一个真实存在的文件」，让 ValidateManifest 这一判据不是空转。
module.exports = { name: "M2 ABI baseline fixture", version: "1.0.0" };
