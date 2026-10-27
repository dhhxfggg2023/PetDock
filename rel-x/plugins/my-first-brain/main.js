// main.js — Tier 1 brain 插件的唯一任务：导出一个能被宿主调用的对象
// 这个对象只回一个函数，返回当前鼠标位置的屏幕坐标（只读，不做任何 UI）

/**
 * 返回当前鼠标在屏幕上的位置（屏幕坐标格式）
 * 宿主（M0 的 FrameContentProvider）会定期轮询这个接口
 * @returns {{x: number, y: number}} 屏幕坐标
 */
function getMousePosition() {
  // 这里不调用任何外部 API，仅作为演示返回固定值
  return { x: 0, y: 0 };
}

// 供宿主检查的接口
const plugin = {
  getMousePosition,
  // Tier 1 brain 必须提供的只读元数据
  name: "My First Brain",
  version: "0.1.0"
};

// 在模块层面导出，宿主通过 entry 字段指向这个文件
module.exports = plugin;