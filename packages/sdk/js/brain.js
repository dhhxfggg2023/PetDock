// @petdock/sdk/brain — 原规格 20 模块面的 brain 位。
// M1 占位：形状对应 原规格 11 的 IBrain（meta/init/dispose/tick(dt,world)→BrainDirective[]/onEvent）。
// 真正的沙箱化宿主与 tick 循环在 M2+（原规格 50）。

/** @typedef {{ name: string, version: string }} Meta */
/** @typedef {{ kind: string, payload?: unknown }} BrainDirective */

/**
 * brain 插件在 JS 侧的形状（占位，M1 无宿主注入）。
 * @typedef {object} Brain
 * @property {() => Meta} meta
 * @property {(ctx: unknown) => Promise<void>} init
 * @property {(dt: number, world: unknown) => BrainDirective[]} tick
 * @property {(event: unknown) => void} onEvent
 * @property {() => Promise<void>} dispose
 */
