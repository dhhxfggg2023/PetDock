// @petdock/sdk/capabilities —— 原规格 20 §2 的能力面。
//
// **M5 的能力边界（重要）**：宿主尚未把能力句柄注入到插件里（`PluginHost.Lifecycle.cs:81`
// 的 `new CapabilityLease(granted)` 是宿主侧硬编码测试集，没有传到 JS 侧）。因此本模块提供的是
// **可装配的能力面**：宿主（或测试夹具）在加载插件前调用 `__bind(host)` 装上一个 broker，
// 插件侧看到的 `has` / `enumerate` / `request` 就变成真实现；没有装上 broker 时，
// `has` 返回 false、`enumerate` 返回空、`request` 抛 CapabilityDeniedError ——
// 这是「没有能力」的诚实表示，而不是 M1 那样一律 throw（那样连 `if (!has(x))` 的降级分支
// 都走不到，插件只能崩）。
//
// has() / enumerate() 同步；request(id, justification) 异步，可能抛 CapabilityDeniedError。
// 例外类型见 原规格 20 §5，实现集中在 ./errors.js（六类齐全）。

import { CapabilityDeniedError, WrongContextError } from './errors.js';

/** @typedef {'input.mousePosition'|'persist.write:self'|string} CapabilityId */

/** 宿主注入的 broker；null = 尚未注入（SDK 独立可 import，不等于插件在宿主里跑）。 */
let broker = null;

/** 当前调用上下文：'tick' | 'queryFrame' | null。由宿主在调度前后设置。 */
let context = null;

/**
 * 宿主/夹具专用：装上能力 broker 并返回卸载函数。
 * 这不是插件 API（原规格 20 §1 没有它），插件不得调用 —— 前缀双下划线即为此。
 *
 * broker 的形状：
 *   { has(id) -> boolean, enumerate() -> string[], request(id, justification) -> Promise<boolean> }
 *
 * @param {{has: (id: string) => boolean, enumerate: () => string[],
 *          request: (id: string, justification: string) => Promise<boolean>} | null} next
 * @returns {() => void} 恢复上一个 broker
 */
export function __bind(next) {
  const previous = broker;
  broker = next;
  return () => { broker = previous; };
}

/**
 * 宿主/夹具专用：进入某个上下文（原规格 16 §4 规定 tick / queryFrame 内不得申请能力）。
 * @param {'tick'|'queryFrame'|null} next
 * @returns {() => void} 恢复上一个上下文
 */
export function __context(next) {
  const previous = context;
  context = next;
  return () => { context = previous; };
}

/**
 * 同步判断某个能力是否已授予给本插件。
 * 没有 broker 时返回 false（= 没有能力），不抛错：让 `if (!has(x))` 的降级分支可用。
 * @param {CapabilityId} id
 * @returns {boolean}
 */
export function has(id) {
  if (broker === null) return false;
  return broker.has(id) === true;
}

/**
 * 列出本插件已获得的能力句柄集合。
 * @returns {ReadonlyArray<CapabilityId>}
 */
export function enumerate() {
  if (broker === null) return Object.freeze([]);
  const list = broker.enumerate();
  return Object.freeze(Array.isArray(list) ? [...list] : []);
}

/**
 * 请求一个尚未授予的能力。异步是因为宿主可能要征询用户（原规格 18 §3 授权弹窗）。
 *
 * 契约（原规格 20 §2）：
 *   * 只允许在安装时（首次激活）或用户主动操作时调用；
 *   * tick / queryFrame 内调用抛 WrongContextError（原规格 16 §4：200ms 内被拒并审计）；
 *   * 返回 false = 用户拒绝，插件必须降级运行，不得反复请求。
 *
 * @param {CapabilityId} id
 * @param {string} justification 插件自述理由，宿主转达给用户
 * @returns {Promise<boolean>}
 */
export async function request(id, justification) {
  if (context !== null) {
    throw new WrongContextError('init or user action', context);
  }
  if (typeof justification !== 'string' || justification.length === 0) {
    throw new TypeError('request(id, justification): justification 必须是非空字符串（原规格 18 §3 要展示给用户）');
  }
  if (broker === null) {
    throw new CapabilityDeniedError(id, 'no capability broker installed (SDK used outside the host)');
  }
  return (await broker.request(id, justification)) === true;
}

export default { has, enumerate, request };
