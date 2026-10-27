// @petdock/sdk/interaction — 原规格 20 模块面的 interaction 位。
// M1 占位：形状对应 原规格 11 的 IInteractionSurface（id/priority/hitTest/onPointerDown→InteractionSession?）。

/** @typedef {object} InteractionSurface */

/**
 * interaction 插件在 JS 侧的形状（占位，M1 无宿主注入）。
 * @typedef {object} InteractionSurface
 * @property {string} id
 * @property {number} priority
 * @property {(x: number, y: number) => boolean} hitTest
 * @property {(x: number, y: number) => unknown} onPointerDown
 */
