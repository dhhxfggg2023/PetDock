// @petdock/sdk/presenter — docs/20 模块面的 presenter 位。
// M1 占位：形状对应 docs/11 的 IPresenter（meta/init/describe/attach/detach/dispose/
// setAnimation/queryFrame/onEvent），但真正的沙箱化宿主与帧合成在 M2+（docs/50）。
// 现在放它是为了让 docs/20 的模块面在 M1 就完整存在、可被 import，不被写成空文件。
//
// 注意：Tier 1 的 presenter 跑在 WASM 沙箱里（t1 契约 §4），本文件只是它在沙箱外的
// 「文档化形状」——沙箱内插件拿到的对象由 t4 的宿主注入，不是这个对象。

/** @typedef {{ name: string, version: string }} Meta */

/**
 * presenter 插件在 JS 侧的形状（占位，M1 无宿主注入）。
 * 每个成员都是「宿主会调用它」的约定签名，实现方按 docs/11 §7 逐项实现。
 * @typedef {object} Presenter
 * @property {() => Meta} meta
 * @property {(ctx: unknown) => Promise<void>} init
 * @property {() => unknown} describe
 * @property {() => Promise<void>} attach
 * @property {() => Promise<void>} detach
 * @property {() => Promise<void>} dispose
 */
