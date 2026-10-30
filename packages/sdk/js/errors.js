// @petdock/sdk/errors —— 原规格 20 §5 的错误类型全集。
//
// 为什么单独一个模块：原规格 20 §5 登记了 6 个错误类，而 M1 的占位实现只把其中 3 个写在
// capabilities.js 里（CapabilityDeniedError / WrongContextError / BudgetExceededError），
// 另外 3 个（ManifestError / DisposedError / ExperimentalRequiredError）根本不存在。
// 一个「文档里有、import 不到」的错误类会让作者写出 `catch (e) { if (e instanceof X) }`
// 然后永远为假 —— 静默失效比缺功能更贵。
//
// 所有错误都可被插件 try/catch（原规格 20 §5 末句）；宿主捕获未处理异常后按 原规格 11 §9 降级。
// 字段名与 原规格 20 §5 表格逐条对应，不额外发明。

/** 能力被拒（含重试）：`request()` 得到否定答复，或 SDK 层直接拒绝。 */
export class CapabilityDeniedError extends Error {
  /**
   * @param {string} capabilityId
   * @param {string} [detail]
   */
  constructor(capabilityId, detail) {
    super(`capability denied: ${capabilityId}${detail ? ` (${detail})` : ''}`);
    this.name = 'CapabilityDeniedError';
    /** 原规格 20 §2 的字段名是 `capability`；旧占位叫 capabilityId，两个都留着以免作者代码失效。 */
    this.capability = capabilityId;
    this.capabilityId = capabilityId;
  }
}

/** 在错误时机调用（如 tick 内调 request，原规格 16 §4）。 */
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

/** 帧/内存/CPU/句柄/网络配额超限（原规格 14 §5 的四项预算）。 */
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

/** manifest 缺字段 / schema 失败（原规格 21）。`errors` 是逐字段的判定行。 */
export class ManifestError extends Error {
  /**
   * @param {string} manifestPath
   * @param {string[]} [errors]
   */
  constructor(manifestPath, errors = []) {
    const tail = errors.length > 0 ? `: ${errors.join('; ')}` : '';
    super(`manifest invalid: ${manifestPath}${tail}`);
    this.name = 'ManifestError';
    this.manifestPath = manifestPath;
    this.errors = Object.freeze([...errors]);
  }
}

/** 插件已 dispose 后仍被调用（原规格 11 §10 的生命周期终态）。 */
export class DisposedError extends Error {
  /** @param {string} [what] */
  constructor(what) {
    super(`already disposed${what ? `: ${what}` : ''}`);
    this.name = 'DisposedError';
  }
}

/** 使用了 experimental API 但 manifest 未声明（原规格 20 §6）。 */
export class ExperimentalRequiredError extends Error {
  /**
   * @param {string} feature
   * @param {string} [pluginId]
   */
  constructor(feature, pluginId) {
    super(`experimental feature '${feature}' not declared in manifest`
      + `${pluginId ? ` (plugin ${pluginId})` : ''}; add "experimental": ["${feature}"]`);
    this.name = 'ExperimentalRequiredError';
    this.feature = feature;
  }
}

export default {
  CapabilityDeniedError,
  WrongContextError,
  BudgetExceededError,
  ManifestError,
  DisposedError,
  ExperimentalRequiredError,
};
