// @petdock/sdk/log — docs/20 §3 的日志面。
// 插件不得直接用 console：日志要经宿主转发，宿主才知道是哪个插件、哪一帧打的。
//
// 实现策略：和 capabilities.js 同一个模式 —— 宿主用 `__bind(sink)` 注入转发器；
// 没注入时退回 console（带 `[petdock:<level>]` 前缀）。为什么不再像 M1 那样留空函数体：
// 空函数体让「日志没出现」和「插件没执行到那行」无法区分，是调试时最贵的一种沉默。
// 退回 console 是明确的降级：开发期在终端里看得见，进宿主后由宿主接管。

/** @typedef {'debug'|'info'|'warn'|'error'} LogLevel */

/**
 * @typedef {object} LogSink
 * @property {(level: LogLevel, message: string, error?: unknown) => void} write
 */

/** @type {LogSink | null} */
let sink = null;

/**
 * 宿主/夹具专用：装上日志转发器并返回卸载函数。
 * @param {LogSink | null} next
 * @returns {() => void}
 */
export function __bind(next) {
  const previous = sink;
  sink = next;
  return () => { sink = previous; };
}

/**
 * 错误对象序列化成一行可读文本；不吞 stack。
 * @param {unknown} error
 * @returns {string | undefined}
 */
function describe(error) {
  if (error === undefined) return undefined;
  if (error instanceof Error) return error.stack ?? `${error.name}: ${error.message}`;
  try {
    return typeof error === 'string' ? error : JSON.stringify(error);
  } catch {
    return String(error);
  }
}

/**
 * @param {LogLevel} level
 * @param {string} message
 * @param {unknown} [error]
 */
function emit(level, message, error) {
  if (sink !== null) {
    sink.write(level, message, error);
    return;
  }
  const line = `[petdock:${level}] ${message}`;
  const detail = describe(error);
  // console 是 M5 的降级出口，不是插件 API：插件仍然只调 log.*。
  if (level === 'error') console.error(line, ...(detail ? [detail] : []));
  else if (level === 'warn') console.warn(line, ...(detail ? [detail] : []));
  else console.log(line, ...(detail ? [detail] : []));
}

export const log = {
  /** @param {string} message */
  debug(message) { emit('debug', message); },
  /** @param {string} message */
  info(message) { emit('info', message); },
  /**
   * @param {string} message
   * @param {unknown} [error]
   */
  warn(message, error) { emit('warn', message, error); },
  /**
   * @param {string} message
   * @param {unknown} [error]
   */
  error(message, error) { emit('error', message, error); },
};

export default log;
