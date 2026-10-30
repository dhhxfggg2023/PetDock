// @petdock/sdk — 入口（原规格 20 的模块面）。
// 插件只能 import 本包导出的模块；直接 import 内核内部包（@petdock/core/*）在构建期即失败
// （原规格 20 第 27 行 / D-02 lint）。
//
// 六个错误类统一从 ./errors.js 出（原规格 20 §5 全集），能力面从 ./capabilities.js 出。
export {
  CapabilityDeniedError,
  WrongContextError,
  BudgetExceededError,
  ManifestError,
  DisposedError,
  ExperimentalRequiredError,
} from './errors.js';
export { has, enumerate, request } from './capabilities.js';
export { log } from './log.js';

/** SDK 自身版本（不是 api 版本，原规格 20 §4 把两者分开）。 */
export const VERSION = '0.1.0';

/**
 * 原规格 20 §4 的 api 版本。major 不匹配 = 宿主拒绝加载；minor 不一致 = 警告但加载。
 * 插件可以 `import { API } from '@petdock/sdk'` 在运行时自检，而不是信任文档。
 */
export const API = Object.freeze({ major: 1, minor: 0 });
