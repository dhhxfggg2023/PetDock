# R0 spike 结果：WPF 泵下旧合成器还能不能用（2026-10-31）

> 目的：在写任何产品代码之前，回答「换泵会不会把宠物窗口搞坏」。
> spike 代码放在 `artifacts/spike-pump/`（gitignored，**不入库**），本文是唯一保留的结论。

## 方法

一个独立的小程序（`net8.0-windows`，WPF + WinForms 都引用）：

1. `new System.Windows.Application().Run()` 起 WPF 泵；
2. 同一线程上建一个 **WinForms `Form`**，`CreateParams` 加 `WS_EX_LAYERED|TOPMOST|TOOLWINDOW|NOACTIVATE`，
   用 `UpdateLayeredWindow` 逐帧画 128×128（中心 64×64 不透明、四周全透明）；
3. 用 `WindowFromPoint` 验命中：中心应是自己、透明角落应是别人；
4. 用嵌套 `DispatcherFrame` 泵 600ms（`Application.DoEvents()` 的替代品），看 WinForms Timer 是否照常触发；
5. 试 `Shell_NotifyIcon` 与 WinForms `NotifyIcon`。

## 结果（本机实测）

| # | 要证的事 | 结果 | 证据 |
| --- | --- | --- | --- |
| 1 | WinForms `Form` 形态的分层窗在 WPF 泵下能出帧 | ✅ | `frames-winforms-timer=150`（4s） |
| 2 | 逐像素命中穿透不变 | ✅ | `opaque=True passthrough=True`（中心=自己，角落=别人） |
| 3 | 不用 `Application.DoEvents()` 也能驱动 | ✅ | 嵌套 `DispatcherFrame` 600ms 内 `ticks=14`，WinForms Timer 照常触发 |
| 4 | WPF 窗口在 WPF 泵下可见 | ✅ | `visible-at-1s=True` |
| 5 | Win32 `Shell_NotifyIcon` 可加图标 | ❓ | 返回 `False`，`GetLastError=0`，`cbSize=976 hwnd/icon 有效`；`explorer` 在本会话（session 1）；WinForms `NotifyIcon` 自报 `Visible=true`（但它不检查返回值） |

## 三个必须写回方案的更正

1. **宠物窗口不是 `NativeWindow`，是 WinForms `Form`**（`LayeredFrameWindow.cs:17`）。
   v2 方案里「仍是 NativeWindow 分层窗」是错的。好消息是 spike 证明 `Form` 在 WPF 泵下工作正常。
2. **帧泵跑在自己的线程上**（`FramePump`：`Thread` + `Thread.Sleep` 补偿，`Compositor/FramePump.cs:11-24`），
   **与 UI 消息泵无关**。所以 spike 里 37fps 的计时器数字**不说明**宠物会被降帧 —— 它只说明这台机器的
   16ms 计时器粒度大约是 26ms。这条把「换泵会掉帧」的风险从「高」降到「低」。
3. **托盘不再承诺纯 Win32**：P/Invoke 探针在本机返回失败且不给错误码，继续钻这个洞不划算。
   新层用 `System.Windows.Forms.NotifyIcon` 包一个**唯一**的托盘适配器（约 100 行），
   lint 规则从「禁止 WinForms」改成「WinForms 只许出现在 `Ui/Tray/NotifyIconTray.cs` 一个文件里」。
   这也意味着「产品零 WinForms」这个目标作废 —— 诚实地写进方案，而不是留一个假的承诺。

## 对 go/no-go 的结论

- R0 的五项里 **4 项通过、1 项待人工确认**（托盘图标是否真的出现）。
- 托盘这一项**不阻塞换泵**：旧层用同一个 `NotifyIcon`，新层换的只是「谁来建它」。
- 因此 **走 A 案（WPF 独占 Dispatcher）**，B 案（纯 WinForms 新壳）不再需要。

## 仍未证 / 交给后续片

- 托盘图标在真机上是否可见（R1 用 `--ui next` 起一次，人眼看一眼；判据只能证明「调用了」。）
- `Form` 的 `DpiChanged`、`Form.Maximize` 这类 WinForms 行为在 WPF 泵下的细节（新层不用 Form，
  宠物窗只用到无边框 + 分层 + 命中，已由 spike 覆盖）。
- 移除 ElementHost 之后 `--uitest` 的截图路径（`RenderTargetBitmap` 仍可用，但要重写夹具）。
