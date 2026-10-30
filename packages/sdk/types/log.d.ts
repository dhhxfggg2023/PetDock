// 原规格 20 §3 的日志面 —— 对应 js/log.js。
export type LogLevel = 'debug' | 'info' | 'warn' | 'error';

/** 宿主注入的日志转发器（不是插件 API）。 */
export interface LogSink {
  write(level: LogLevel, message: string, error?: unknown): void;
}

export declare const log: {
  debug(message: string): void;
  info(message: string): void;
  warn(message: string, error?: unknown): void;
  error(message: string, error?: unknown): void;
};

export default log;
