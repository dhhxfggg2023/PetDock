// @petdock/sdk 的 TypeScript 声明入口 —— 对应 js/index.js。
import type { CapabilityId } from './common.js';

export type * from './common.js';
export * from './errors.js';
export * from './capabilities.js';
export { log } from './log.js';
export type { LogLevel, LogSink } from './log.js';

export declare const VERSION: string;
export declare const API: { readonly major: number; readonly minor: number };
export type { CapabilityId };
