// @petdock/sdk — 入口（docs/20 的模块面）。
// 插件只能 import 本包导出的模块；直接 import 内核内部包（@petdock/core/*）在构建期即失败
// （docs/20 第 27 行 / D-02 lint）。
export { has, enumerate, request, CapabilityDeniedError, WrongContextError, BudgetExceededError } from './capabilities.js';
export { log } from './log.js';
