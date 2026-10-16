// @petdock/sdk/log — docs/20 §3 的日志面。
// 插件不得直接用 console：日志要经宿主转发，宿主才知道是哪个插件、哪一帧打的。
// M1 只固定形状（宿主的转发器还没接）。

export const log = {
  /** @param {string} message */
  debug(message) { /* M1: 宿主任意 */ },
  /** @param {string} message */
  info(message) { /* M1: 宿主任意 */ },
  /**
   * @param {string} message
   * @param {unknown} [error]
   */
  warn(message, error) { /* M1: 宿主任意 */ },
  /**
   * @param {string} message
   * @param {unknown} [error]
   */
  error(message, error) { /* M1: 宿主任意 */ },
};

export default log;
