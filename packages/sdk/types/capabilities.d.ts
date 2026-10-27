// 原规格 20 §2 的能力面 —— 对应 js/capabilities.js。
import type { CapabilityId } from './common.js';

/** 宿主注入的能力 broker 形状（不是插件 API）。 */
export interface CapabilityBroker {
  has(id: CapabilityId): boolean;
  enumerate(): readonly CapabilityId[];
  request(id: CapabilityId, justification: string): Promise<boolean>;
}

/** 同步判断能力是否已授予。没有 broker 时返回 false，不抛错。 */
export declare function has(id: CapabilityId): boolean;

/** 列出已获得的能力。 */
export declare function enumerate(): readonly CapabilityId[];

/**
 * 请求能力。tick / queryFrame 内调用抛 WrongContextError；
 * 返回 false 表示用户拒绝，插件必须降级运行。
 */
export declare function request(id: CapabilityId, justification: string): Promise<boolean>;
