// @petdock/sdk/capabilities — docs/20 §2 的能力面。
// M1 只放签名占位（无实现）：Windows M1 的能力授权 UI 在 M5（docs/50），
// 现在也还没有宿主把能力句柄注入进来。这里只固定「插件看到什么形状」。
//
// has() / enumerate() 同步；request(id, justification) 异步，可能抛 CapabilityDeniedError。
// 例外类型见 docs/20 §5（CapabilityDeniedError / WrongContextError / BudgetExceededError /
// ManifestError / DisposedError / ExperimentalRequiredError）。

/** @typedef {'input.mousePosition'|'persist.write:self'|string} CapabilityId */

/**
 * 同步判断某个能力是否已授予给本插件。
 * @param {CapabilityId} id
 * @returns {boolean}
 */
export function has(id) {
  throw new CapabilityDeniedError(id, 'M1: capabilities host not yet provided (see docs/50 M5)');
}

/**
 * 列出本插件已获得的能力句柄集合。
 * @returns {ReadonlyArray<CapabilityId>}
 */
export function enumerate() {
  return Object.freeze([]);
}

/**
 * 请求一个尚未授予的能力。异步是因为宿主可能需要仲裁（M5 的授权 UI）。
 * @param {CapabilityId} id
 * @param {string} justification 插件自述理由，宿主转述给用户
 * @returns {Promise<void>}
 */
export async function request(id, justification) {
  throw new CapabilityDeniedError(id, 'M1: no capability broker is installed');
}

/** 能力被拒（docs/20 §5）。 */
export class CapabilityDeniedError extends Error {
  /**
   * @param {CapabilityId} id
   * @param {string} [detail]
   */
  constructor(id, detail) {
    super(`capability denied: ${id}${detail ? ` (${detail})` : ''}`);
    this.name = 'CapabilityDeniedError';
    this.capabilityId = id;
  }
}

/** 用错上下文（docs/20 §5）：例如在非 tick 上下文里调需要 tick 的接口。 */
export class WrongContextError extends Error {
  /**
   * @param {string} expected
   * @param {string} actual
   */
  constructor(expected, actual) {
    super(`wrong context: expected ${expected}, got ${actual}`);
    this.name = 'WrongContextError';
    this.expectedContext = expected;
    this.actualContext = actual;
  }
}

/** 配额超限（docs/20 §5，对应 docs/14 §5 的 CPU/内存/句柄/网络四项）。 */
export class BudgetExceededError extends Error {
  /**
   * @param {'cpu'|'memory'|'handles'|'network'} budget
   * @param {string} [detail]
   */
  constructor(budget, detail) {
    super(`budget exceeded: ${budget}${detail ? ` (${detail})` : ''}`);
    this.name = 'BudgetExceededError';
    this.budget = budget;
  }
}
