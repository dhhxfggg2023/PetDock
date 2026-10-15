# Windows Window APIs for a Per-Pixel-Transparent Desktop Pet

Paper-only research. Every claim carries a URL; unverifiable claims are marked **unverified**.

## 1. API per requirement

| Requirement | API / style | Source |
|---|---|---|
| Per-pixel transparency | `WS_EX_LAYERED` + `UpdateLayeredWindow` (`ULW_ALPHA`, `AC_SRC_ALPHA`) | [UpdateLayeredWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow) |
| Whole-window alpha / color-key | `WS_EX_LAYERED` + `SetLayeredWindowAttributes(LWA_ALPHA\|LWA_COLORKEY)` | [SetLayeredWindowAttributes](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setlayeredwindowattributes) |
| Click-through **by alpha** | `UpdateLayeredWindow` — OS does it | [Layered Windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features) |
| Click-through **by region** (you compute) | `WM_NCHITTEST` → `HTTRANSPARENT` | [WM_NCHITTEST](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest) |
| Click-through **entirely** | `WS_EX_TRANSPARENT` (needs `WS_EX_LAYERED` in practice) | [Layered Windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features) |
| GPU-composited per-pixel alpha | `WS_EX_NOREDIRECTIONBITMAP` + `CreateSwapChainForComposition` + DirectComposition | [MSDN Mag 2014](https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine) |
| Always-on-top | `SetWindowPos(…, HWND_TOPMOST, …, SWP_NOACTIVATE)` | [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos) |
| No focus steal | `WS_EX_NOACTIVATE` | [Ext. Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles) |
| Hide from Alt-Tab | `WS_EX_TOOLWINDOW` | [Ext. Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles) |
| Capture exclusion | `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` | [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity) |
| DPI | PMv2 manifest + `WM_DPICHANGED` | [High DPI](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows) |
| Global keyboard/mouse | `SetWindowsHookEx(WH_KEYBOARD_LL\|WH_MOUSE_LL)` | [SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw) |

## 2. Layered windows

`WS_EX_LAYERED` (0x00080000): forbidden with class styles `CS_OWNDC`/`CS_CLASSDC`; **Windows 8** extended it from top-level-only to top-level *and child*. The window "will not become visible until `SetLayeredWindowAttributes` or `UpdateLayeredWindow` has been called". ([Ext. Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles), [Window Features](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features))

`SetLayeredWindowAttributes` redirects painting off-screen and recomposes — "Legacy applications do not need to restructure their painting code". `UpdateLayeredWindow` is the per-pixel path, "for faster and more efficient animation or if per-pixel alpha is needed", and "uses memory more efficiently, because the system does not need the additional memory required for storing the image of the redirected window". Mutually exclusive: "once `SetLayeredWindowAttributes` has been called for a layered window, subsequent `UpdateLayeredWindow` calls will fail until the layering style bit is cleared and set again." ([Window Features](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features), [SetLayeredWindowAttributes](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setlayeredwindowattributes))

**Hit testing, verbatim** ([Layered Windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)):

> "Hit testing of a layered window is based on the shape and transparency of the window. This means that the areas of the window that are color-keyed or whose alpha value is zero will let the mouse messages through. However, if the layered window has the **WS_EX_TRANSPARENT** extended window style, the shape of the layered window will be ignored and the mouse events will be passed to other windows underneath the layered window."

MSDN Magazine 2009: "Hit testing of a layered window is based on the alpha values of the bitmap, allowing mouse messages through if the pixel at a particular point is transparent. As a result, a copy of the bitmap is required in system memory to allow this to happen." MSDN Magazine 2014: "layered windows are processed by the CPU, primarily to support hit testing of alpha-blended pixels"; "provided I don't need per-pixel hit testing. **The whole window will hit test uniformly.**" ([2009](https://learn.microsoft.com/en-us/archive/msdn-magazine/2009/december/windows-with-c-layered-windows-with-direct2d), [2014](https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine))

**Color-key:** yes, clicks pass through (quote above). Caveat: only the exact key color is punched out, so antialiased edges become clickable holes. A Microsoft engineer confirms: "Once you specify the transparency color, you will be able to click through the transparent area." ([Q&A](https://learn.microsoft.com/en-us/answers/questions/2153247/directcomposition-click-through-in-transparent-are))

**`WS_EX_TRANSPARENT`** (0x20) is documented as *painting order*: "The window should not be painted until siblings beneath the window (that were created by the same thread) have been painted." Alone it does not make a window click-through; with `WS_EX_LAYERED` it overrides layered hit testing — a reporter got "all my window as acting as transparent, but never got any `WM_NCHITTEST` event, so became unusable". ([Ext. Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles))

## 3. `WM_NCHITTEST` / `HTTRANSPARENT`

`HTTRANSPARENT` (-1): "In a window currently covered by another window **in the same thread** (the message will be sent to underlying windows in the same thread until one of them returns a code that is not `HTTRANSPARENT`)." ([doc](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest))

Returning it makes the message skip your window, so you get **no** `WM_MOUSEMOVE`/`WM_SETCURSOR` for that point — `WM_SETCURSOR` fires only when "the mouse causes the cursor to move within a window", and its `lParam` carries the hit-test result. Thread-scoped: returning it to a parent on another thread makes Windows bounce the message between threads, burning kernel time in `win32k!xxxWindowHitTest` and starving input. ([WM_SETCURSOR](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-setcursor), [HTTRANSPARENT is evil](https://www.virtualdub.org/blog2/entry_147.html))

| | OS alpha hit-test (`UpdateLayeredWindow`) | Your hit-test (`WM_NCHITTEST`) |
|---|---|---|
| Who computes | User32, from bitmap alpha | you, from your geometry |
| Cost | CPU surface copy + CPU hit test ([Kerr 2014](https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine)) | cheap; window must be hit-testable |
| Works with DirectComposition | **no** | yes |

Use alpha hit-test when the silhouette is too complex to express (Live2D meshes, soft shadows, particles); your own when transparency lives on the GPU, or interactive region ≠ visible region.

## 4. DWM blur-behind and modern backdrops

`DwmEnableBlurBehindWindow` (Vista+): "**Beginning with Windows 8, calling this function doesn't result in the blur effect**, due to a style change in the way windows are rendered." Top-level only; re-call on `WM_DWMCOMPOSITIONCHANGED`. ([doc](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmenableblurbehindwindow))

**As a hit-test region:** `DWM_BLURBEHIND` still takes `hRgnBlur` ("The region within the client area where the blur behind will be applied"), and nothing says the region stops being honoured — only the blur does. It survives as the *empty-region transparency hack* (`fEnable = TRUE`, region `(0,0,-1,-1)`), which Tauri/`tao` still uses. **No Microsoft statement that the region still drives hit testing on Win8+, so "blur-behind region ⇒ click-through region" is unverified**; the documented effect is only "transparent client area". ([struct](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ns-dwmapi-dwm_blurbehind), [overview](https://learn.microsoft.com/en-us/windows/win32/dwm/blur-ovw), [tauri#15947](https://github.com/tauri-apps/tauri/issues/15947))

`DwmExtendFrameIntoClientArea` with `MARGINS{-1}`: "Negative margins create the 'sheet of glass' effect, where the client area is rendered as a solid surface with no window border." ([doc](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea))

Modern: `DWMWA_SYSTEMBACKDROP_TYPE` (Win11 22621) = `DWMSBT_MAINWINDOW` (Mica) / `DWMSBT_TRANSIENTWINDOW` (Desktop Acrylic) / `DWMSBT_TABBEDWINDOW` (Mica Alt); `DWMWA_USE_IMMERSIVE_DARK_MODE` (Win11 22000). These paint a *backdrop* — no per-pixel alpha. `DWMWA_REDIRECTIONBITMAP_ALPHA` (Win11 26100): "If this attribute is set to true, the window must contain premultiplied alpha values in each pixel." ([enum](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute), [backdrop](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type))

## 5. `SetWindowDisplayAffinity`

`WDA_NONE` 0x0 (no restriction); `WDA_MONITOR` 0x1 ("displayed only on a monitor. Everywhere else, the window appears with no content"); `WDA_EXCLUDEFROMCAPTURE` 0x11 ("Everywhere else, the window does not appear at all"), **Windows 10 2004**. Top-level, same process, works "only when the Desktop Window Manager (DWM) is composing the desktop", and explicitly not DRM ("there is no guarantee that using `SetWindowDisplayAffinity` … will strictly protect windowed content, for example where someone takes a photograph of the screen"). On older Windows it "will behave as if `WDA_MONITOR` is applied" — a black box. ([doc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity))

**For a pet: `WDA_NONE`.** Excluding from capture makes the pet vanish from every screen share, OBS recording and screenshot, which reads as a bug; reserve it for an explicit stealth mode. Win11 has known failures (FALSE, `GetLastError` = `ERROR_NOT_ENOUGH_MEMORY` (8)); Microsoft confirmed "there is a problem with using `SetWindowDisplayAffinity` in other non-Win32 applications in Win11". ([Q&A](https://learn.microsoft.com/en-us/answers/questions/700122/setwindowdisplayaffinity-on-windows-11))

## 6. Always-on-top

`HWND_TOPMOST` (-1) "Places the window above all non-topmost windows. The window maintains its topmost position even when it is deactivated." `HWND_TOP` (0) only "Places the window at the top of the Z order" — within its group, so a later-activated non-topmost window still covers it. Use `HWND_TOPMOST` with `SWP_NOACTIVATE`. ([doc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos))

`WS_EX_NOACTIVATE` (0x08000000): "does not become the foreground window when the user clicks it", off the taskbar by default. `WS_EX_TOOLWINDOW` (0x80): "does not appear in the taskbar or in the dialog that appears when the user presses ALT+TAB". ([Ext. Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles))

**Limits.** Topmost is a band inside the DWM's desktop, not above:
- **UAC / secure desktop, lock screen** — a separate desktop; other apps' windows are not on it. Microsoft describes the secure desktop as protecting input/output ([Q&A](https://learn.microsoft.com/en-us/answers/questions/238459/uac-prompt-is-blocked-when-i-use-quick-assist)). No formal statement that a topmost window cannot appear there → **unverified**.
- **Full-screen exclusive games** — FSE "gives your game complete ownership of the display"; DWM is bypassed, and the only documented overlay route is intercepting render/present, which "can cause problems including performance regressions, instability and issues with anti-cheat". **Borderless windowed works.** Since Win10, Fullscreen Optimizations often makes a game *believe* it is FSE while actually running borderless windowed — there, topmost overlays do work. ([DirectX blog](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations))

## 7. Multi-monitor + DPI

Modes: **Unaware** (96 DPI, stretched, blurry), **System** (primary monitor's DPI, stretched elsewhere), **PMv1** (Win8.1), **PMv2** (Win10 1703). "It is recommended that desktop applications be updated to use per-monitor DPI awareness mode"; "Per-Monitor V1 (PMv1) awareness is very limited. It is recommended that applications use PMv2." PMv2 adds DPI notification to child HWNDs, raw pixels, and automatic non-client/dialog/theme scaling. ([High DPI](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows))

Set it **in the manifest**: "It is recommended that you set the process-default DPI awareness via application manifest, not an API call … Setting the process-default DPI awareness via API call can lead to unexpected application behavior." `dpiAwareness` = `PerMonitorV2`. The API fallback `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` (Win10 1703+) must run before any HWND exists, and "Once API awareness is set for an app, any future calls to this API will fail." ([manifest](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process), [API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setprocessdpiawarenesscontext))

**`WM_DPICHANGED`:** `LOWORD(wParam)`/`HIWORD(wParam)` = new X/Y DPI; `lParam` = "a pointer to a RECT structure that provides a suggested size and position of the current window scaled for the new DPI". A topmost borderless window must apply it via `SetWindowPos(hWnd, NULL, …, SWP_NOZORDER | SWP_NOACTIVATE)` — `NULL`, not `HWND_TOPMOST`, so a DPI change does not re-order the window. Ignoring it makes the window "grow or shrink with respect to everything else on the new monitor" and can cause a recursive DPI-change cycle. ([WM_DPICHANGED](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged))

**Multi-monitor model.** The virtual screen is the bounding rect of all monitors; the primary holds (0,0) and "does not have to be in the upper left", so "all applications should be designed to work with negative coordinates"; bounds are signed 16-bit. And: "Do not use the `LOWORD` or `HIWORD` macros to extract the x- and y- coordinates of the cursor position because these macros return incorrect results on systems with multiple monitors." ([Virtual Screen](https://learn.microsoft.com/en-us/windows/win32/gdi/the-virtual-screen), [WM_NCHITTEST](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest))

**Documented bug classes:** an initial render area larger than 1×1 permanently blocking clicks ([nwjs#8342](https://github.com/nwjs/nw.js/issues/8342)); runtime toggling of `WS_EX_TRANSPARENT|WS_EX_LAYERED` forcing a DWM composition-tree rebuild that sporadically renders transparent areas or the wallpaper black ([tauri#15947](https://github.com/tauri-apps/tauri/issues/15947)); `UpdateLayeredWindow` failing at the OS level, which WPF works around by retrying ([WPF](https://dwayneneed.github.io/wpf/2008/09/08/transparent-windows-in-wpf.html)).

## 8. Global input hooks

`WH_KEYBOARD_LL` (13) / `WH_MOUSE_LL` (14) are **Global only**; the hook proc must be in a DLL when `dwThreadId` is 0 or names another process's thread. ([doc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw))

Message loop, verbatim: "This hook is called in the context of the thread that installed it. The call is made by sending a message to the thread that installed the hook. **Therefore, the thread that installed the hook must have a message loop.**"

Timeout, verbatim: "The hook procedure should process a message in less time than the data entry specified in the **LowLevelHooksTimeout** value in the following registry key: `HKEY_CURRENT_USER\Control Panel\Desktop` … If the hook procedure times out, the system passes the message to the next hook. **However, on Windows 7 and later, the hook is silently removed without being called. There is no way for the application to know whether the hook is removed.**" and "**Windows 10 version 1709 and later** The maximum timeout value the system allows is 1000 milliseconds (1 second)." Microsoft's advice: "In most cases where the application needs to use low level hooks, it should monitor raw input instead." Inside the callback, "the asynchronous state of the key cannot be determined by calling `GetAsyncKeyState`". ([kbd](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc), [mouse](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc))

**Session 0: no.** "Services cannot directly interact with a user as of Windows Vista"; "All services run in Terminal Services session 0", so a service's UI and hooks belong to session 0, not the user's desktop. ([doc](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services))

**UIPI: yes, it blocks you.** "User Interface Privilege Isolation (UIPI) implements restrictions in the Windows subsystem that prevent lower-privilege applications from sending messages or installing hooks in higher-privilege processes." A medium-integrity app cannot hook an elevated window. The escape hatch is UIAccess (signed, under `%ProgramFiles%`/`%WinDir%`), which can "Use read input for all integrity levels by using low-level hooks, raw input, `GetKeyState`, `GetAsyncKeyState`, and `GetKeyboardInput`." ([policy](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/user-account-control-only-elevate-uiaccess-applications-that-are-installed-in-secure-locations))

| Alternative | Use for | Caveat |
|---|---|---|
| `RegisterHotKey` | One global shortcut (summon pet) | Reserved combos fail; F12 reserved for the debugger; `MOD_WIN` is OS-reserved ([doc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)) |
| `RegisterRawInputDevices` | Mouse/keyboard even when unfocused | "By default, an application does not receive raw input"; one window per device class per process ([doc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices)) |
| `GetAsyncKeyState` | Cheap modifier checks | Only the sign bit (down) is trustworthy; the LSB "should not be relied upon because it can report false negatives" ([doc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate)) |
| `GetForegroundWindow` | Which app is active | NULL while a window is losing activation ([doc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow)) |
| `SetWinEventHook` | React to foreground changes | "The client thread that calls `SetWinEventHook` must have a message loop" ([doc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)) |

## 9. DirectComposition / D3D interop

Route: `WS_EX_NOREDIRECTIONBITMAP` (0x00200000L, "does not render to a redirection surface") — **not** `WS_EX_LAYERED`; then `CreateSwapChainForComposition` (`B8G8R8A8_UNORM`, `DXGI_ALPHA_MODE_PREMULTIPLIED`, `FLIP_SEQUENTIAL`, `BufferCount` 2, `SampleDesc.Count` 1); then an `IDCompositionTarget` on the HWND with the swap chain as root visual content. ([styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles), [desc](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_swap_chain_desc1), [MSDN Mag 2014](https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine))

**Buys:** "the result is pixel-perfect alpha blending on the desktop. It's also incredibly fast because there's no unnecessary copying within the GPU and certainly no copies over the bus to system memory." Versus layered, which needs "a copy of the bitmap … in system memory" for User32's hit test and then "is sent straight back to the GPU", and which "forces batched commands to be flushed and the CPU to wait until the GPU has completed". ([2014](https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine), [2009](https://learn.microsoft.com/en-us/archive/msdn-magazine/2009/december/windows-with-c-layered-windows-with-direct2d))

**Costs:** "The whole window will hit test uniformly" — no OS per-pixel hit test, so you implement picking. One reporter found `WM_NCHITTEST` apparently inert on such a window: "even returning `HTNOWHERE` in all cases isn't really doing anything"; the Microsoft engineer's advice was to switch to `WS_EX_LAYERED` + `SetLayeredWindowAttributes`. **No Microsoft statement that `WM_NCHITTEST` is ignored there → unverified, but test early.** ([Q&A](https://learn.microsoft.com/en-us/answers/questions/2153247/directcomposition-click-through-in-transparent-are))

**Users:** UWP/WinUI/XAML — "It's the same technique that powers the Windows 8.1 XAML implementation"; IE's Trident used it for panning/zoom and CSS animations. Microsoft now recommends `Windows.UI.Composition` over raw DirectComposition. ([portal](https://learn.microsoft.com/en-us/windows/win32/directcomp/directcomposition-portal))

## 10. Per-frame cost, and what real pets do

No Microsoft fps benchmark of `UpdateLayeredWindow` vs a composition swap chain exists that I could find (**unverified**). The evidence is qualitative, from Kerr's two columns: the layered path pays GPU→CPU copy, CPU hit test, CPU→GPU copy, and a GPU/CPU sync per frame, purely to give the CPU the pixels for hit testing; the composition path pays none. Microsoft separately calls blt-model presentation obsolete — "it's time to stop!" — and says flip model "eliminates performance penalties normally associated with windowed mode rendering". ([flip](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model))

- **Chromium pets/overlays (NW.js, Electron, Tauri/WebView2)** historically got OS per-pixel alpha hit testing free via the GDI redirection-bitmap path. **Chromium 139's `RemoveRedirectionBitmap` applies `WS_EX_NOREDIRECTIONBITMAP` to the top-level window**, which "flips the overlay off the classic path onto DirectComposition" so "the OS stops doing per-pixel hit-testing". Reported boundary: NW.js 0.101.2 (Chromium 138.0.7204.97) good → 0.102.0 (Chromium 139.0.7258.66) bad. ([nwjs#8342](https://github.com/nwjs/nw.js/issues/8342))
- **Tauri/tao** uses `DwmEnableBlurBehindWindow` + empty `(0,0,-1,-1)` region and toggles `WS_EX_TRANSPARENT|WS_EX_LAYERED` at runtime as a "cursor guard"; a `no_redirection_bitmap` option is landing as the structural fix. ([tauri#15410](https://github.com/tauri-apps/tauri/pull/15410))
- **BongoCat** (Tauri + Live2D, 23.6k★) and **Open-LLM-VTuber** advertise "transparent background; mouse click-through for non-interactive areas" through their window layer. ([BongoCat](https://github.com/ayangweb/bongocat), [Open-LLM-VTuber](https://open-llm-vtuber.github.io/en/docs/user-guide/frontend/electron/))
- **Desktop Mate** — no first-party engineering write-up found → **unverified**. **Wallpaper Engine** — claims a "highly optimized" core and auto-pausing for games, but no evidence of `UpdateLayeredWindow` or DirectComposition use; wallpapers render *behind* icons via a different, undocumented mechanism → **unverified**. ([wallpaperengine.io](https://www.wallpaperengine.io))
- **Godot/Unity pet tutorials** use per-pixel transparency plus mouse passthrough computed from app geometry — the `WM_NCHITTEST` route. ([Godot](https://forum.godotengine.org/t/how-can-i-click-through-the-screen-while-keeping-a-transparent-screen/64195))

## 11. Recommended architecture

**Primary: `WS_EX_LAYERED` + `UpdateLayeredWindow`, premultiplied BGRA DIB, window sized to the pet's bounding box.** Add `WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`; no `WS_EX_TRANSPARENT`; borderless `WS_POPUP`; PMv2.

It is the only route where **Windows itself** does the per-pixel hit test — the exact requirement, stated on the Learn page — so the pet is interactive on opaque pixels and click-through on transparent ones with no picking code, and it survives the silhouette changing every frame.

**The tradeoff:**
- **OS alpha hit-test (`UpdateLayeredWindow`)** — correct by construction; costs a GPU→CPU→GPU round trip plus a CPU hit test per frame, and forces rendering into a GDI-compatible surface. Cost scales with *window area*, not pet area, which is why the docs say "the layered window should be as small as possible". ([doc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow))
- **DirectComposition (you compute hit-test)** — GPU-composited, no copies, no CPU hit test; but the OS stops per-pixel hit testing, you must implement picking, and one report says `WM_NCHITTEST` did nothing at all on such a window. Right for a full-screen, heavily animated pet; wrong for v1.

Do **not** use `WS_EX_TRANSPARENT` (discards the shape), `LWA_COLORKEY` (one exact color; antialiased edges become holes), or `WS_EX_NOREDIRECTIONBITMAP` unless committing to DirectComposition *and* hand-written picking. Hybrid worth prototyping: layered window for the pet, separate small windows for UI chrome, keeping the CPU hit-test surface minimal.

## 12. Gotchas

| Symptom | Cause | Fix |
|---|---|---|
| `UpdateLayeredWindow` returns FALSE | `SetLayeredWindowAttributes` called first — later calls "will fail until the layering style bit is cleared and set again" | Clear and re-set `WS_EX_LAYERED`, or never mix |
| Window never appears | Not visible "until `SetLayeredWindowAttributes` or `UpdateLayeredWindow` has been called" | Call one after `CreateWindowEx` |
| Black halo / dark fringe | Non-premultiplied alpha, or GDI destroyed alpha ("Some GDI operations don't preserve alpha values") | Premultiplied BGRA, `AC_SRC_ALPHA`; draw with Direct2D/GDI+ |
| Clicks fall through the pet's *body* | `WS_EX_TRANSPARENT` — "the shape of the layered window will be ignored" | Remove it |
| A rectangle around the pet eats clicks | `LWA_ALPHA` only → global alpha, uniform hit test | Switch to `UpdateLayeredWindow` |
| `HTTRANSPARENT` does nothing, or input stalls / CPU burns in `win32k!xxxWindowHitTest` | Target window is on another thread; the doc scopes it to "the same thread" | Don't use it across threads; use layered alpha |
| Wrong click coordinates on a second monitor | `LOWORD`/`HIWORD` on `lParam` — "incorrect results on systems with multiple monitors" | `GET_X_LPARAM`/`GET_Y_LPARAM` |
| Blurry on one monitor, crisp on another | System-DPI-aware or unaware process | PMv2 in the manifest |
| Wrong resize after a DPI change, or a resize loop | Suggested `lParam` rect in `WM_DPICHANGED` ignored | `SetWindowPos(..., NULL, ..., SWP_NOZORDER \| SWP_NOACTIVATE)` |
| Pet absent from Alt-Tab/taskbar | `WS_EX_TOOLWINDOW` and/or `WS_EX_NOACTIVATE` — both documented to remove it | Expected; add `WS_EX_APPWINDOW` if a taskbar entry is wanted |
| Pet steals focus on click | `WS_EX_NOACTIVATE` missing, or `SetWindowPos` without `SWP_NOACTIVATE` | Add both |
| Pet invisible in OBS/Discord/screenshots | `WDA_EXCLUDEFROMCAPTURE` set | `WDA_NONE`; expect `WDA_MONITOR`'s black box on pre-2004 Windows |
| `SetWindowDisplayAffinity` FALSE, `GetLastError` = 8 | Known Win11 issue; only "normal Win32 applications" are reliable | Best-effort; never fail the app on it |
| Global hook dies silently after minutes | Exceeded `LowLevelHooksTimeout`; "the hook is silently removed … no way for the application to know" | Dedicated hook thread handing off to a worker; or raw input |
| Hook never fires | Installing thread has no message loop | Pump messages on that thread |
| Hook blind while an elevated app is focused | UIPI — lower-privilege apps cannot install hooks in higher-privilege processes | Accept, or ship signed UIAccess from Program Files |
| `GetAsyncKeyState` misses a press | LSB "can report false negatives" | Use the sign bit only; raw input for events |
| Transparent areas or the wallpaper flash black when toggling click-through or minimizing | Toggling `WS_EX_LAYERED` forces a DWM composition-tree rebuild racing the blur-behind hack | Don't toggle `WS_EX_LAYERED` at runtime; change the bitmap instead ([tauri#15947](https://github.com/tauri-apps/tauri/issues/15947)) |
| First render area permanently blocks clicks | Compositor never re-initialized alpha state; NW.js used a 1×1 initial window ([nwjs#8342](https://github.com/nwjs/nwjs/issues/8342)) | Create small, resize before first present |
| Overlay cannot draw over a full-screen game | FSE gives the game "complete ownership of the display"; DWM bypassed | Borderless-windowed only; Fullscreen Optimizations games are already borderless |
| `WM_NCHITTEST` seems entirely ignored | Reported on `WS_EX_NOREDIRECTIONBITMAP` + DirectComposition top-level windows (**unverified**) | Test early; fall back to a layered window for the interactive region |
