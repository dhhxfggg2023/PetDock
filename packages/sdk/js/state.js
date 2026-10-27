// @petdock/sdk/state — 原规格 20 模块面的 state 位。
// M1 占位：形状对应 原规格 11 的 IPetState（schemaVersion/readAll/read/write/commit/rollback/migrate）。

/** @typedef {object} PetState */

/**
 * state 插件在 JS 侧的形状（占位，M1 无宿主注入）。
 * @typedef {object} PetState
 * @property {number} schemaVersion
 * @property {() => unknown} readAll
 * @property {(key: string) => unknown} read
 * @property {(key: string, value: unknown) => void} write
 * @property {() => void} commit
 * @property {() => void} rollback
 * @property {(from: number, to: number) => void} migrate
 */
