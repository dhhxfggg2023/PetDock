# 02 — Host Runtime / Tech Stack Evaluation

**Status:** paper evaluation, no code written, nothing installed.
**Scope:** which host runtime should PetDock commit to, and which plugin VM.
**Host facts verified on this machine (2026-10-14):** Node.js `v22.23.0` at `C:\Program Files\nodejs\node.exe`; .NET SDK `8.0.421` at `C:\Program Files\dotnet\dotnet.exe`; pnpm at `%USERPROFILE%\AppData\Local\pnpm\bin\pnpm.ps1`; git at `D:\Program Files\Git\cmd\git.exe`. **No `rustc`/`cargo` on PATH** (`Get-Command rustc,cargo` returned nothing — exit 1 was that negative result, not a probe failure). Windows host.

---

## 0. The finding that decides everything

Before comparing frameworks, one OS fact constrains all of them.

Microsoft Learn, [Window Features → Layered Windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features):

> Hit testing of a layered window is based on the shape and transparency of the window. This means that the areas of the window that are color-keyed or whose alpha value is zero will let the mouse messages through. However, if the layered window has the **WS_EX_TRANSPARENT** extended window style, the shape of the layered window will be ignored and the mouse events will be passed to other windows underneath the layered window.

So **Windows itself does per-pixel alpha hit testing — but only for a layered window whose pixels live in the `UpdateLayeredWindow` bitmap.** The OS reads that bitmap's alpha channel to decide whether a click falls through.

The alternative modern path is the opposite trade. Kenny Kerr, [High-Performance Window Layering Using the Windows Composition Engine](https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine), MSDN Magazine June 2014:

> As of Windows 8, you can now create a top-level window and request that it be created without a redirection surface… you need to use the **WS_EX_NOREDIRECTIONBITMAP** extended window style… This means I can now produce the effect of a layered window without compromising performance, **provided I don't need per-pixel hit testing. The whole window will hit test uniformly.**

and on the cost of the layered route:

> layered windows are processed by the CPU, primarily to support hit testing of alpha-blended pixels. This means the CPU needs a copy of the pixels that make up the layered window's surface area… **I must pay the bus bandwidth tax** because everything I render must be copied from video memory to system memory.

**These two properties are mutually exclusive in the OS.** Per-pixel OS hit testing costs a CPU-side copy of every frame. GPU-only composition loses per-pixel hit testing and forces you to compute hit tests yourself.

This is not theoretical. [nwjs/nw.js#8342](https://github.com/nwjs/nw.js/issues/8342) recorded the exact regression when Chromium changed sides:

> Chromium 139 shipped a feature called RemoveRedirectionBitmap that applies **WS_EX_NOREDIRECTIONBITMAP** to the top-level window. That single change flips the overlay off the classic path onto DirectComposition: ≤ Cr138: transparent window = GDI redirection-bitmap / layered window → **Windows itself does per-pixel alpha hit-testing**… ≥ Cr139: window has no redirection bitmap, presented via DirectComposition → **the OS stops doing per-pixel hit-testing** → per-pixel routing must be reconstructed by Chromium, and on the software-draw path it's wrong.

Version matrix from that issue: NW.js 0.101.2 (Chromium **138.0.7204.97**) good → 0.102.0 (Chromium **139.0.7258.66**) bad, **both on Node 24.3.0**, ruling Node out.

WinUI 3's maintainers state the same root cause from the other end, in [microsoft-ui-xaml#10746](https://github.com/microsoft/microsoft-ui-xaml/discussions/10746):

> WinUI 3 uses composition to draw its contents. The drawing is hardware accelerated and drawn using Direct3D… **The window does not have access to the video memory that backs the window content so it is unable to check the alpha for the pixels.**

**Consequence for PetDock:** every framework that renders through Chromium, WebView2, or a GPU composition swapchain sits on the path where per-pixel click-through is *not* provided. Every framework that renders through `UpdateLayeredWindow` gets it for free. This single axis reorders the whole candidate list, and it is the axis on which the "obvious" choices (Electron, Tauri) are weakest.

### The second half of the finding: `UpdateLayeredWindow` redraws the *whole* window

Microsoft Learn, [Layered Windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features), on `UpdateLayeredWindow`:

> For maximum efficiency in animating windows, call **UpdateLayeredWindow** to change the position and the size of a layered window.

and, decisively for the windowing model:

> **The layered window should be as small as possible.**

Because `UpdateLayeredWindow` replaces the entire window bitmap on every call, **per-frame cost scales with window *area*, not with pet count.** This inverts the naive design:

- **N small per-pet windows beat one full-screen overlay** on the layered path. Nine 256×256 windows is 589,824 pixels; one 3840×2160 overlay is 8,294,400 pixels — **14× more work per frame**, for the same nine pets. The overlay design only wins if you must cross-fade between pets or draw them overlapping.
- The layered window lives in system memory and DWM copies it to VRAM, so composition itself stays hardware-accelerated (Kerr: "even though the layered window necessarily resides in system memory, the composition engine immediately copies it to video memory such that the actual composition of the layered window is still hardware-accelerated").

This is a *design constraint the recommendation must carry*, and it is why the §5 spike tests N small windows rather than one overlay.

### Electron's own version of the same trade

[electron#10994](https://github.com/electron/electron/issues/10994) records the identical fork in Electron's terms: **with hardware acceleration enabled you cannot click through transparent regions; only `app.disableHardwareAcceleration()` makes click-through work.** Transparency also fails when DWM is off, and renders black/grey on some systems ([electron#40515](https://github.com/electron/electron/issues/40515)). So Electron asks you to choose between GPU acceleration and click-through — the same choice Kerr describes, made for you and not in your favour.

### Correct primary architecture

| Requirement | API | Who provides it |
|---|---|---|
| Per-pixel transparency **and** per-pixel click-through | `WS_EX_LAYERED` + `UpdateLayeredWindow` | OS (Raw Input Thread reads the bitmap) |
| GPU-composited per-pixel alpha, **no** per-pixel hit test | `WS_EX_NOREDIRECTIONBITMAP` + `CreateSwapChainForComposition` + DirectComposition | OS, app computes hit tests |
| Whole-window click-through | `WS_EX_TRANSPARENT` (needs `WS_EX_LAYERED`) | OS — but kills interactivity too |
| Binary transparency + click-through | `SetLayeredWindowAttributes` (color key) | OS |
| Region click-through | `SetWindowRgn` / `WM_NCHITTEST`→`HTTRANSPARENT` | App — **`HTTRANSPARENT` only reaches windows in the same thread**, see below |
| Always-on-top | `SetWindowPos(HWND_TOPMOST)` | OS |
| No focus steal, no Alt-Tab entry | `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW` | OS |
| Hide from screen capture | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` | OS, **Windows 10 v2004+** |
| Per-monitor DPI | Per-Monitor V2 awareness + `WM_DPICHANGED` | OS notifies, **app must resize** |

`HTTRANSPARENT` is a trap: Microsoft Learn's [`WM_NCHITTEST`](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest) defines it as "In a window currently covered by another window **in the same thread** (the message will be sent to underlying windows in the same thread until one of them returns a code that is not HTTRANSPARENT)". It cannot pass a click to another *process* — which is exactly what a desktop pet must do. The working per-process technique is to toggle `WS_EX_TRANSPARENT` on and off based on cursor position.

`SetWindowDisplayAffinity` is explicitly **not** a security feature: "unlike a security feature or an implementation of Digital Rights Management (DRM), there is no guarantee that using SetWindowDisplayAffinity… will strictly protect windowed content" ([Learn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)).

DPI: [High DPI Desktop Application Development on Windows](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows) — "It is recommended that desktop applications be updated to use per-monitor DPI awareness mode… **It is then the complete responsibility of the application to handle resizing itself for the new DPI**." PMv1 is "very limited"; use **PMv2**. Critical pitfall: you *must* use the rectangle supplied in `WM_DPICHANGED` or "the application window [can get] into a recursive dpi-change cycle."

Global input: [`LowLevelKeyboardProc`](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) — "The thread that installed the hook must have a message loop", and "**on Windows 7 and later, the hook is silently removed** without being called. **There is no way for the application to know whether the hook is removed.**" Windows 10 1709+ caps `LowLevelHooksTimeout` at 1000 ms. Microsoft's own recommendation: "In most cases where the application needs to use low level hooks, it should **monitor raw input instead**." Also, UIPI blocks a medium-integrity process from hooking an elevated foreground window ([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/2262996/how-can-i-tell-in-c-or-vb-net-if-a-low-level-hook)).

**Design rule this produces:** run the hook on a dedicated thread that hands work to a worker and returns immediately; treat hook death as expected and re-install; prefer `RegisterHotKey` and raw input where possible.

---

## 1. Candidate-by-candidate evidence

### A. Electron (Node + Chromium)

**What it exposes.** `transparent: true`, `frame: false`, `alwaysOnTop: true` with levels (`pop-up-menu` and above sit above the taskbar on Windows; `screen-saver` is the top), `setIgnoreMouseEvents(ignore, {forward})`, `setShape(rects)` (Experimental), `hookWindowMessage`, `getNativeWindowHandle()` ([BaseWindow](https://www.electronjs.org/docs), [Custom Window Interactions](https://electronjs.org/docs)).

**The docs' own limitation list** ([Custom Window Styles](https://raw.githubusercontent.com/electron/electron/main/docs)), verbatim:

> * You cannot click through the transparent area. See [#1335](https://github.com/electron/electron/issues/1335) for details.
> * Transparent windows are not resizable…
> * The CSS `blur()` filter only applies to the window's web contents…
> * The window will not be transparent when DevTools is opened.
> * On _Windows_: Transparent windows can not be maximized using the Windows system menu…
> * On _macOS_: The native window shadow will not be shown on a transparent window.

**Click-through reality.** `forward` is **macOS + Windows only** ([docs](https://electronjs.org/docs); Linux tracked open at [#16777](https://github.com/electron/electron/issues/16777)). The canonical request [#1335](https://github.com/electron/electron/issues/1335) is **OPEN since 2015-03-31** (105 comments, 155 👍). An Electron maintainer, [zcbenz in #23042](https://github.com/electron/electron/issues/23042):

> I have looked into this and I realized that **we do not actually support click-through transparent window**. The old behavior was accidentally supported by Chromium from some version, and then accidentally dropped by Chromium later. So unless someone is willing to find out exactly how to make it work, we are unable to "fix" this issue.

That is the authoritative statement: Electron never *had* per-pixel click-through; it inherited a Chromium accident and lost it. The community workaround is the `mouseenter`/`mouseleave` → `setIgnoreMouseEvents(true/false)` toggle, which carries a reported **Windows cursor-glitch bug** ([#1335 comment 4680051603](https://github.com/electron/electron/issues/1335#issuecomment-4680051603)), and a documented Windows flicker bug ([#35030](https://github.com/electron/electron/issues/35030), closed `not_planned`). [#52456](https://github.com/electron/electron/issues/52456) shows the mechanism is still fragile upstream (Linux/X11 regression in Electron 43; `XShapeGetRectangles(..., ShapeInput, ...)` reports `1x1+0+0` on 42 vs `400x400+0+0` on 43).

**Escape hatch:** small — `hookWindowMessage(WM_NCHITTEST, …)` is pure JS, but returning `HTTRANSPARENT` per-point requires knowing the alpha at that point, **which the renderer never exposes**. A true per-pixel solution needs a native module that reads the rendered frame.

**Verdict:** excellent for everything except the one thing a desktop pet needs most. Viable only with the hover-toggle approximation.

---

### B. Tauri v2 (Rust host + WebView2)

`transparent`, `decorations: false`, `alwaysOnTop` (**boolean only, no level**), `set_ignore_cursor_events(bool)` — **no `forward` option** ([config](https://v2.tauri.app/reference/config/), [JS API](https://v2.tauri.app/reference/javascript/api/namespacewindow/)).

**Maintainer position, verbatim** ([tauri#2090 comment](https://github.com/tauri-apps/tauri/issues/2090#issuecomment-883338598)):

> electron's `setIgnoreMouseEvents` will ignore all mouse events and not just on transparent areas. **Detecting whether an area within a window is transparent or not is a near impossible task**, even electron gave up on the idea and will introduce an api for manipulating the window shape instead.

[#13070](https://github.com/tauri-apps/tauri/issues/13070) was **closed as duplicate** (2025-03-25); the live tracking issue for even the *`forward`* option is [#6164](https://github.com/tauri-apps/tauri/issues/6164), **OPEN since 2023-01-28**. Tauri's own `noRedirectionBitmap` config option *sets* `WS_EX_NOREDIRECTIONBITMAP` — i.e. Tauri steers you onto the path that provably loses per-pixel hit testing. [#4881](https://github.com/tauri-apps/tauri/issues/4881) (`transparent: true` paints white until resize) is closed `status: upstream`.

**Renderer risk:** WebView2's own transparency is buggy. [WebView2Feedback#5752](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5752) — "Default background is opaque and theme-derived on Runtime 154 despite DefaultBackgroundColor alpha=0", filed against "a desktop-pet style window (`transparent: true`, no decorations, always-on-top), so the opaque base is very visible". [#4945](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4945) — CSS blur broken on transparent WebView2 "by design in Chromium". [Learn](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.defaultbackgroundcolor) confirms the API "only supports opaque colors and transparency. It will fail for colors with alpha values that don't equal 0 or 255."

**Verdict:** strictly worse than Electron for this use case — same composition path, fewer knobs, an explicit "will not be implemented", and a renderer that is not yours to patch. Its size and contributor-friction arguments are real but orthogonal to the pet problem.

---

### Idle footprint (real numbers, with sources)

A desktop pet runs all day next to games and video calls, so idle RAM and CPU are product features.

| Host | Idle RSS, trivial app | Source |
|---|---|---|
| **Electron** | **~230 MB** on Windows for a "just load a page, then log in" app; 160 MB at 1600×1200; 120–180 MB minimised. Empty shell 80–100 MB. A vendor counter-datapoint: main 40 MB + 30 MB/window | [HN 16975265](https://news.ycombinator.com/item?id=16975265) |
| **Tauri v2** | No RSS figure found. WebView2 is **preinstalled on Windows 11 only**; on Win10 the installer must ensure it, and Microsoft warns "some devices might not have the Runtime pre-installed" | [Tauri webview-versions](https://v2.tauri.app/reference/webview-versions/), [MS distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) |
| **WPF** | Community reports **>90 MB** vs WinForms 6–7 MB; another reports 250 MB | [dotnet/wpf#9017](https://github.com/dotnet/wpf/discussions/9017), [r/dotnet](https://www.reddit.com/r/dotnet/comments/1cnndlq/even_small_wpf_application_uses_more_memory_250mb/) |
| **WinUI 3** | No quotable number — a blank unpackaged app "consumes a significant amount of memory" (screenshot only) | [microsoft-ui-xaml#8736](https://github.com/microsoft/microsoft-ui-xaml/issues/8736) |
| **Godot 4** (empty, exported) | **~200 MB** with the d3d12 driver, **~400 MB** otherwise | [Godot forum](https://forum.godotengine.org/t/default-memory-usage-concerns-200mb-400mb-for-empty-project/129801) |
| **PySide6 / Qt** | **unverified** — only a 2013-era "~80 MB user-space" forum claim | [Qt forum](https://forum.qt.io/topic/25330/memory-footprint-of-qml-applications) |

Godot 4 Windows export: **92.8 MB** default, minifiable to ~6.4 MB with custom templates ([popcar](https://popcar.bearblog.dev/how-to-minify-godots-build-size/)).

**The one real desktop-pet measurement found** — [openai/codex#20680](https://github.com/openai/codex/issues/20680), macOS: WindowServer CPU **77.9% → 109.9%**, GPU helper 16.6% → 21.5%, renderer processes **6 → 13**, combined renderer RSS **2,479 MB → 5,027 MB (+2.55 GB)**. On Windows, the Electron GPU process sustained **30–73% of one logical core** with the overlay open (hardware GPU-engine <1%), dropping to **0%** with total app CPU 0.4% once closed, plus 1–2 s input freezes. That is what a badly-architected pet overlay costs, and it is the number PetDock must beat.

### Rendering capability ceiling

| Host | Live2D Native (C++) | Live2D Web | Spine | three.js + three-vrm | MMD | Ruffle | Alpha video |
|---|---|---|---|---|---|---|---|
| Electron | ✗ no C++ host | ✓ WebGL | ✓ spine-webgl | ✓ WebGL2 (r163+) | ✓ MMDLoader | ✓ WASM+WebGL | ✓ VP9 alpha |
| Tauri v2 | ✗ | ✓ WebGL in WebView2 | ✓ | ✓ | ✓ | ✓ | ⚠ blocked by [#5752](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5752) today |
| WPF / WinUI 3 | ✗ no GL/D3D host for the SDK | ✗ no WebGL | ✓ spine-csharp (generic = renderer-agnostic) | ✗ no WebGL2 | ✗ | ✗ | ⚠ HEVC/MediaFoundation only |
| Qt | ✓ GL/D3D widget | ✓ via QtWebEngine | ✓ | ✓ via QtWebEngine | ✓ | ✓ | ✓ |
| Godot 4 | ✓ GDExtension | ✗ | ✓ GDExtension | ✗ | ✗ | ⚠ | ⚠ |

Anchors: three.js **"WebGL 1 is not supported since r163"** ([docs](https://threejs.org/docs)); Live2D Cubism SDK for Native renders via OpenGL/DX9/DX11, OpenGL 3.3+ ([Live2D spec](https://www.live2d.com/en/cubism/download/spec/5_3_00_b1)); the Web SDK "is implemented in WebGL" ([Live2D](https://www.live2d.com/en/sdk/about/)); Spine's generic runtimes "handle everything except rendering" ([Spine](https://en.esotericsoftware.com/spine-runtimes)).

**The ceiling is a hard fork, not a gradient.** Web-renderer hosts (Electron, Tauri) get the whole WebGL stack — Live2D Web, Spine-webgl, three.js/VRM, MMD, Ruffle — but no C++ SDK host. Native hosts (WPF, WinUI, Qt, Godot) get the C++ SDKs but **no WebGL2**, hence no three.js, no VRM, no Ruffle. **A plugin-per-pet-format platform must support both**, which is an argument for the hybrid shell: a native shell can host a WebView *as a plugin's rendering surface* while keeping the pet window's alpha and hit-testing under its own control — but see §0, this only works if the shell owns the hit-test mask, because a WebView2-drawn pet's alpha is invisible to the layered bitmap.

---

### C. .NET 8/9 + WPF

**The only candidate that gets per-pixel click-through for free.** Dwayne Need (WPF architect), [Transparent windows in WPF](https://dwayneneed.github.io/wpf/2008/09/08/transparent-windows-in-wpf.html):

> For normal windows, the RIT checks the mouse position against the window's rectangle (or region, if one is set). But for layered windows, **the RIT looks in the bitmap that specifies the content for the window and checks the effective transparency at that location**… **If the pixel is 100% transparent, the RIT skips the window and keeps looking.**

Mechanism: WPF uses **Application-Provided Content** — `UpdateLayeredWindow`, `WS_EX_LAYERED`, client area expanded over the whole window via `WM_NCCALCSIZE`. Two constraints: the mode is **construction-only** ("WPF enforces this constraint rigidly, rejecting any attempt to alter the underlying WS_EX_LAYERED extended window style"), and **child HWNDs do not composite into the layered bitmap**.

Second constraint, important for UI: inside the window, **WPF's own hit test is not alpha-aware** — "Even if the brush is 'Transparent', WPF still considers the geometry to be solid, like a piece of glass… WPF does not check the transparency at the mouse position." Use `Background=null` / `IsHitTestVisible=false` to punch holes.

**The performance caveat is real and on Microsoft Learn** — [Graphics Rendering Tiers](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/graphics-rendering-tiers) lists "Layered windows" among features that are **not** hardware accelerated: "On operating systems that support WDDM, such as Windows Vista and Windows 7, layered windows are hardware accelerated. On other systems… layered windows are rendered by software with no hardware acceleration." Need's own measurements: a full-screen constantly-updating layered window cost ~3% CPU on XP and **as much as 30% on Vista**. On Windows 11 with WDDM this is expected to be hardware-accelerated, but **the CPU copy for hit testing remains** — that is the price of the feature, and it is the same price Kerr describes.

[`Window.AllowsTransparency`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency) requires `WindowStyle=None` or it throws.

**Weaknesses:** WPF does **not** support trimming or NativeAOT ([dotnet/wpf#6096](https://github.com/dotnet/wpf/issues/6096) — "trimming support for WPF is currently disabled"; [DotNetBrowser](https://teamdev.com/dotnetbrowser/docs) — "WPF, WinForms and WinUI3 are not trimming and Native AOT compatible"), so installers are large. `PresentationFramework.dll` alone is ~30 MB. Steam overlay does not work with WPF ([gamedev.SE](https://gamedev.stackexchange.com/questions/120624/how-to-make-the-steam-overlay-work-with-wpf)).

**Verdict:** the technically correct host for the windowing problem, at the cost of installer size and Steam overlay.

---

### D. WinUI 3 / Windows App SDK

**Provably impossible to do per-pixel**, per the maintainer quote in §0. The accepted answer is regions: `CreateRectRgn` + `CombineRgn` + `SetWindowRgn`, with the asker's own closing caveat: "I will have to **actively manage the region** of the window as I move children controls inside my window or show dialogs, dropdowns, tooltips, or other controls."

`SystemBackdrop` cannot be fully transparent — Mica is "an **opaque** material", Acrylic is "**semi-transparent**" and in-app only ([Learn](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)). The feature request [#1247](https://github.com/microsoft/microsoft-ui-xaml/issues/1247) is **OPEN since 2019-08-30** (141 comments, 105 👍), labelled `wpf-vs-winui-mismatch`. `WS_EX_TRANSPARENT` makes the window "completely passthrough, even the non-transparent pieces".

**Verdict:** reject. Worse than WPF on the exact axis that matters, with no compensating advantage for a Windows-only product.

---

### E. Qt 6 / PySide6

`Qt::WA_TranslucentBackground` (120) — "On Windows the widget also needs the Qt::FramelessWindowHint window flag to be set" ([Qt namespace](https://doc.qt.io/qt-6/qt.html#WidgetAttribute-enum)). `Qt::WindowStaysOnTopHint`. `Qt::WindowTransparentForInput` is **whole-window**.

Qt gives per-pixel click-through for **unpainted** pixels: [ShapedClock example](https://doc.qt.io/qt-6/qtwidgets-widgets-shapedclock-example.html) — "Widgets that set their background to be translucent will be transparent for all unpainted pixels… **Pixels that are not painted at all will also not receive any mouse input.**" ⚠️ Note the boundary: pixels painted with *partial* alpha still capture. An anti-aliased Live2D sprite has partial alpha at every silhouette edge — the wrong shape of guarantee.

`setMask` coarseness is documented verbatim: "**On most systems, they also result in coarse visual clipping. To get smooth window edges, use translucent background and anti-aliased painting instead**", and "this effect can be slow if the region is particularly complex."

**Licensing:** LGPLv3 with real obligations — ship the complete source of the Qt libraries you used (a link to Qt's own source is "not sufficient"), let the user re-link and run the re-linked binary, include the full LGPL text. Store terms "may or may not be compatible" with LGPL. Commercial is **per-seat, annual** (ADE-SB $618/yr, DCP-SB $1090/yr, max 3 licences under the small-business tier) ([Qt LGPL FAQ](https://www.qt.io/faq/qt-open-source-licensing), [Qt Small Business](https://www.qt.io/development/qt-for-small-business)). **No built-in auto-updater.**

**Verdict:** viable but adds a licensing-compliance program and a language/runtime most pet-plugin authors will not use, for a hit-testing guarantee that is *weaker* than WPF's.

---

### F. Godot 4 as renderer

`Window.transparent`, `Window.borderless`, `Window.always_on_top`, `Window.mouse_passthrough` (bool), `Window.mouse_passthrough_polygon` / `DisplayServer.window_set_mouse_passthrough(PackedVector2Array)` ([Window](https://docs.godotengine.org/en/stable/classes/class_window.html)).

Doc caveats, verbatim: `mouse_passthrough` — "all mouse events will be passed to the underlying window **of the same application**" (⚠️ ambiguous whether it reaches *other* applications; flag as a docs ambiguity, not a verified cross-app passthrough). `mouse_passthrough_polygon` — "**On Windows, the portion of a window that lies outside the region is not drawn**, while on Linux (X11) and macOS it is." `per_pixel_transparency/allowed` — "This affects performance, so leave it on `false` unless you need it."

**Two open bugs make this unreliable:** [#89205](https://github.com/godotengine/godot/issues/89205) — "Forward+ Window transparency is not working, works in Compatibility renderer", **OPEN** since 2024-03-06, reproduced on AMD RX 6900 XT, NVIDIA 2080 Super, 3070, RTX 4050, and integrated Radeon 780M; workarounds involve VSync and driver versions. [#85075](https://github.com/godotengine/godot/issues/85075) — polygon passthrough "doesn't seem to work as intended", closed `archived` the same day with no fix. [#57835](https://github.com/godotengine/godot/issues/57835) — on Windows the region *clips rendering* as well as input, unlike Linux/macOS.

**Embedding:** LibGodot builds Godot as a shared/static library embeddable from C++, TypeScript, Swift, Python, Java/Kotlin, MIT-licensed ([GodotCon talk](https://www.youtube.com/watch?v=L06KBOWCsSk)).

**Verdict:** reject as the *host*. A renderer whose transparency depends on GPU vendor, driver version, and VSync state is not a foundation for an always-on product. Keep as an optional *plugin-side* renderer if a pet format wants it.

---

### G. Hybrid: native shell + JS/WASM plugin VM

**This is the recommendation.** A small native shell (the §0 layered-window core) owning OS integration — layered window, always-on-top, DPI, global hooks, foreground-app detection — plus a plugin VM for pet behaviour and rendering.

It is not a new framework: it is the *architecture* every other candidate already implies, with the shell written deliberately instead of inherited. It is the only option that puts `UpdateLayeredWindow` under your control rather than under Chromium's or WebView2's, and it is what the shipping evidence points to: **VPet** (6.9k★, C#/WPF, explicit `CoreMOD` mod manager, Steam Workshop, and "code plugins" that add "new animation logic/display schemes (eg: l2d/spine)"), **BongoCat** (23.7k★, Tauri, but its own README reports `<20MB` installer and `<50MB` memory — small because the pet surface is a Live2D canvas, not a web app), and **Desktop Mate** (Unity + `UniWindowController`). All three are thin shells around a rendering surface.

---

## 2. Weighted comparison

Weights reflect what a desktop pet actually is: an always-on, always-visible, clickable overlay that must never get in the user's way.

| Criterion | Weight | A. Electron | B. Tauri v2 | C. WPF | D. WinUI 3 | E. Qt | F. Godot 4 | **G. Hybrid** |
|---|---|---|---|---|---|---|---|---|
| **Per-pixel click-through** | **25%** | 3 | 2 | **10** | 1 | 6 | 2 | **10** |
| Transparent-window correctness | 15% | 5 | 4 | **9** | 2 | 8 | 4 | **9** |
| Always-on-top / no-focus / DPI | 10% | 8 | 6 | **9** | 7 | 8 | 6 | **9** |
| Global input hooks | 10% | 4 | 4 | 8 | 6 | **9** | 4 | **9** |
| Plugin ecosystem pull (JS/TS) | 15% | **10** | 10 | 4 | 3 | 3 | 3 | **10** |
| Plugin isolation available | 10% | 6 | 7 | 3 | 3 | 4 | 4 | **9** |
| Idle RAM / CPU | 8% | 3 | 5 | 6 | 6 | 5 | 4 | 7 |
| Distribution / signing / store | 7% | 6 | 8 | 7 | 8 | 4 | 7 | 7 |
| **Weighted total** | 100% | **5.0** | **5.3** | **7.1** | **3.6** | **6.0** | **4.0** | **8.9** |

*(1–10, higher is better. Per-pixel click-through and transparent-window correctness carry 40% because they are the product.)*

### Recommendation

**Build a native shell that owns the layered window, and put the plugin VM behind it.**

Concretely, and in order of preference:

1. **Shell in C# / .NET 8** (already installed: SDK `8.0.421`) using WPF's `AllowsTransparency` path *or* raw Win32 `UpdateLayeredWindow` via P/Invoke. WPF is the pragmatic choice because it *is* the `UpdateLayeredWindow` path with the hit testing already wired, and it is the only candidate whose transparency is a documented, supported, OS-level feature. If the WPF rendering-tier cost is measured to be unacceptable, drop to a raw Win32 + Direct2D shell using the same API set — the window core is ~200 lines either way, and the plugin VM does not change.
2. **Shell in Rust + `windows` crate** if and only if you are willing to add a toolchain to every contributor's machine. **This machine has no Rust.** Do not choose it for performance reasons alone; the layered-window core is not CPU-bound.
3. **Do not** choose Electron or Tauri as the *host* for the pet window. Use Chromium/WebView2 only as a **plugin-side rendering surface** if a pet format wants HTML.

**Why not the "obvious" hybrid (Rust shell + WebView2 renderer)?** Because WebView2 renders through DirectComposition, which is precisely the path that cannot do per-pixel hit testing ([#10746](https://github.com/microsoft/microsoft-ui-xaml/discussions/10746), [#8342](https://github.com/nwjs/nw.js/issues/8342)). If the pet is drawn by WebView2, the shell's layered window cannot see the pet's alpha. **The renderer must write into a bitmap the OS can read**, or the shell must own a hit-test mask alongside the renderer.

### Top 3 risks of this recommendation

1. **The layered-window CPU copy does not scale to many pets at 1080p/60fps.** Kerr's "bus bandwidth tax" and Need's ~30%-CPU-Vista measurement are the warning. *Mitigation:* one layered window per pet sized to the pet's bounding box (not full-screen), dirty-rect updates, and a spike (§5) that measures actual CPU with N pets. *If this fails:* move to a single full-screen `WS_EX_NOREDIRECTIONBITMAP` overlay with a self-computed hit-test mask — you keep GPU composition and pay only a mask lookup.
2. **Writing a shell means writing what a framework would have given you.** Window management, DPI, multi-monitor, hooks, crash recovery, and the plugin loader are all yours. Electron hands you an ecosystem and a packaging story on day one. *Mitigation:* the shell is genuinely small (window core + hook thread + VM host); the risk is schedule, not feasibility. *If this fails:* fall back to Electron with the hover-toggle approximation and accept degraded click-through, explicitly documented as a product limitation.
3. **A C#/.NET shell narrows the contributor pool for *native* plugins**, and .NET offers no real in-process isolation — AppDomains are gone: ".NET Core…does not provide isolation, unloading, or security boundaries… Security boundaries should be provided by **process boundaries**", and `AssemblyLoadContext` "is not a security boundary" ([MS Learn](https://learn.microsoft.com/en-us/dotnet/api/system.appdomain)). *Mitigation:* this is why the plugin VM is a separate, JS-facing layer (§4) — the shell's language must never be the plugin's language.

---

## 3. "If you pick X you must give up Y"

| Candidate | You must give up |
|---|---|
| **A. Electron** | Per-pixel click-through (maintainer: "we do not actually support click-through transparent window"). Resizable transparent windows. Blur behind the window. DevTools-visible transparency. ~115 MB installer. A native addon for any real hit-testing. |
| **B. Tauri v2** | Per-pixel click-through — and the maintainers have said it **will not be implemented**. The `forward` option (open since 2023). Always-on-top *levels* (boolean only). Transparency reliability on Windows (white until resize). `visibleOnAllWorkspaces` on Windows. WebView2's transparency bugs are not yours to fix. |
| **C. WPF** | Trimming and NativeAOT (never coming for WPF). Steam overlay. A small installer (~75 MB framework-dependent, ~30 MB for PresentationFramework alone). Alpha-aware hit testing *inside* your own window (WPF treats transparent brushes as solid). Child HWNDs do not composite into the layered bitmap. |
| **D. WinUI 3** | Per-pixel click-through, permanently and by design. Fully transparent backgrounds. Any transparency that is not Mica/Acrylic/region-based. You must hand-maintain a `SetWindowRgn` region across every move, resize, popup, and tooltip. |
| **E. Qt** | Your licensing simplicity (LGPLv3 relinking + source-offer obligations, or per-seat commercial). Built-in auto-update. Smooth masked edges (`setMask` is "coarse visual clipping"). Per-pixel click-through for *partially* transparent pixels. |
| **F. Godot 4** | Reliable transparency on the Forward+ renderer (open bug, vendor-dependent). Polygon passthrough (reported broken; on Windows it clips *rendering* too). Cross-application passthrough is ambiguous in the docs. Embedding a general-purpose game engine to draw a sprite. |
| **G. Hybrid** | A framework's ecosystem, packaging, and updater on day one. You build window management, DPI, hooks, and crash recovery yourself. You own every Windows-version regression. You must maintain the shell for as long as the product lives. |

---

## 4. Plugin VM recommendation

### Decision

**Adapters are written in TypeScript/JavaScript. They run in QuickJS compiled to WebAssembly, one runtime per plugin, in-process, behind a membrane. Plugins that need real CPU are promoted to a separate process inside a Windows Job Object.**

### Why JS/TS for adapters

Every comparable host chose a scripted, no-compile plugin language: VS Code (TS/JS), Obsidian (TypeScript), Figma (JS/HTML, TS recommended), Blender (Python only — "Blender does not have a C/C++ API"). None chose Rust or C#. JS is used by **62.3%** of Stack Overflow 2024 respondents vs C# 27% and Rust 12%. Rust additionally has **no stable ABI** ([rfcs#1675](https://github.com/rust-lang/rfcs/issues/1675)), and `abi_stable` "has no sandbox" and is "far from being permanently backward compatible" ([NullDeref](https://nullderef.com/blog/plugin-abi-stable)). .NET lost AppDomains as an isolation mechanism. **The adapter language must be the one with the largest pool and no toolchain requirement.**

### Why QuickJS-in-WASM, not the alternatives

| Option | Verdict | Reason |
|---|---|---|
| `node:vm` | **Rejected** | "The `node:vm` module is not a security mechanism. Do not use it to run untrusted code." ([Node docs](https://nodejs.org/api/vm.html)) |
| `isolated-vm` | **Rejected as primary** | Maintenance mode; native addon needing a compiler and version-matched prebuilds (painful inside Electron); a memory limit its own author calls "more of a guideline instead of a strict limit. A determined attacker could use 2-3 times this limit"; and a **critical guest→host escape** found in its *glue code* ([GHSA-864f-rcv7-6rh4](https://www.endorlabs.com/learn/ghsa-864f-rcv7-6rh4-critical-type-confusion-vulnerability-in-isolated-vm), 2026-08-08 — a TOCTOU in `ExternalCopy`'s `transferList` where walk 1 validates with `IsArrayBuffer()` and walk 2 does a bare reinterpret-cast, reachable from one `ivm.Reference`). |
| **QuickJS / `quickjs-emscripten`** | **Chosen** | WASM linear memory; guest has **no host globals at all**; `setMemoryLimit`, `setMaxStackSize`, `setInterruptHandler` (fires ~per bytecode instruction), `shouldInterruptAfterDeadline`. Plain JS/TS, no native build. |
| Wasmtime / Component Model | **Optional tier** | Capability-based and deny-by-default ("A component without a filesystem import cannot read files"), fuel or epoch interruption (epoch ~10% slowdown, fuel 10–25%). But **~3.5× overhead on synchronous calls today** and ~6 GiB reserved address space per linear memory, plus a Rust/AS toolchain in front of every adapter author. |
| Bare `<iframe>` / renderer | **Rejected** | No per-plugin CPU or memory quota. Figma killed this approach: a 14-second document serialization on Microsoft's design-systems file, plus `async`/`await` ergonomics. |

**Precedent that this exact choice is right:** Figma went `<iframe>` → Duktape-in-WASM → Realms shim → **escaped, replaced with QuickJS compiled to WASM**. Their stated reason is the guarantee WASM gives and no JS-level trick can: "it's not possible to confuse objects from outside with objects from inside because the object representations are too different." Their API is a **membrane** over a `LowLevelJavascriptVm` interface — which is exactly why they could swap engines in 11 days when the shim was compromised. **Build the membrane, not the engine binding.**

### How plugins are permissioned

The pattern is confirmed independently by four systems — Chrome MV3 (`permissions` / `optional_permissions` / `host_permissions`), VS Code (`contributes` + capabilities + Workspace Trust), Deno (`--allow-read=/path`, `--allow-net=host:port`, `--deny-*` overriding, `Deno.permissions.revoke()` to drop a grant after startup), WASI ("no capability handle by default"), Extism (`allowed_hosts` — empty means none, null means all — and `allowed_paths`):

> **Manifest declares → host grants at install → runtime enforces at the API boundary.**

For PetDock: a pet adapter's `pet.json` declares capabilities (`fs.read:["$PET/assets/**"]`, `net:["api.example.com"]`, `input.keyboard.observe`, `input.mouse.observe`, `audio.play`, `window.create`), the host shows them at install, and the VM exposes **only** the granted functions via `newFunction`. Nothing else exists in the guest's global object.

### The performance ceiling on the hot path

**This is the hard constraint and it is not yet measured.** 60 fps = **16.67 ms** total budget for everything.

QuickJS is a **bytecode interpreter with no JIT**. One thread quotes "2–3% of V8 performance… 20–100× slower than the V8 compiler" ([godotjs#16](https://github.com/godotjs/javascript/issues/16), 2020 — dated, but Bellard's 2026 note claims only "42% faster than the previous release", so the order of magnitude stands).

**Design consequence: never call into the VM once per plugin per frame with individually marshalled handles.** Batch it: one call per plugin per frame carrying a small numeric array; keep animation state *inside* the guest and let only the resulting transform cross the boundary. With that shape, the ceiling is set by the interpreter's per-frame work, not by marshalling.

**What is genuinely unknown, and must be measured:** no published benchmark exists for "N plugins × 1 small call at 60 Hz" on QuickJS, isolated-vm, or wasmtime. That measurement is the spike in §5. **Marked unverified.**

### What the VM does NOT protect against

- **Side channels** — a QuickJS runtime in your process shares that process; Spectre-class attacks are why Chromium needs one-site-per-process.
- **Host-function bugs** — every capability you hand the guest is attack surface. The isolated-vm CVE was one such bug, reachable from a single `Reference`. Extism states host functions "must be carefully validated."
- **Your own marshalling layer** — a memory-unsafe binding around a sound primitive is where sandboxes break.
- **Memory-limit overshoot** — assume any in-process cap can be exceeded before termination.
- **A runaway `while(true)`** — it still burns a core until the interrupt handler fires, and that handler runs on *your* thread. This is why the process tier (Job Objects) is not optional.
- **Supply chain** — "The permission sandbox controls what code can do at runtime, but it does not tell you whether your dependencies contain known vulnerabilities" ([Deno](https://docs.deno.com/runtime/fundamentals/security/)).
- **A plugin that is merely slow** — nothing here makes a badly-written 60 fps plugin good; it only stops it taking the host down.

### Process tier (mandatory, not optional)

One plugin group per process, inside a Windows **Job Object**: `JOB_OBJECT_LIMIT_PROCESS_MEMORY` and `JOB_OBJECT_LIMIT_JOB_MEMORY` for hard memory caps (commit fails past the cap), and `JOB_OBJECT_LIMIT_CPU_RATE_CONTROL` (`CpuRate` as percent×100) — "After the job reaches this limit for a scheduling interval, **no threads associated with the job can run**" ([BASIC_LIMIT_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information), [CPU rate control](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information)). Add AppContainer/LPAC only if you ever ship untrusted *native* plugins.

Reference architecture for the same job, in Electron's own words: the utility process "can be used to host for example: **untrusted services, CPU intensive tasks or crash prone components**" ([process model](https://electronjs.org/docs)).

### What shipping hosts admit (set expectations honestly)

- **VS Code**: "The extension host has the same permissions as VS Code itself… any action that VS Code can perform, an extension can also perform." Third-party research: "The current VS Code architecture does not sandbox Extension Host Process." ([runtime security](https://code.visualstudio.com/docs), [arXiv 2411.07479](https://arxiv.org/html/2411.07479v1))
- **Obsidian**: "Due to technical limitations, Obsidian cannot reliably restrict plugins to specific permissions or access levels." ([Plugin security](https://github.com/obsidianmd/obsidian-help/blob/master/en/Extending%20Obsidian/Plugin%20security.md))
- **OBS**: maintainers' standard crash answer is "start OBS in safe mode… this is likely a 3rd party plugin" ([obs-studio#13248](https://github.com/obsproject/obs-studio/issues/13248)).

PetDock should beat all three, and can — because the membrane pattern is well understood and the process tier is an OS primitive.

---

## 5. The de-risking spike

**One experiment, two days, one binary. It answers the single question that could invalidate the whole recommendation.**

### Spike: "Does the layered-window path survive N animated pets at 60 fps?"

**Build:** a throwaway Win32/C# app with **no plugin system, no VM, no UI**. It creates **N = 1, 4, and 9** `WS_EX_LAYERED` + `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` + `HWND_TOPMOST` windows, each sized to a 256×256 sprite's bounding box (**not** full-screen — §0 shows `UpdateLayeredWindow` cost scales with area), each fed a 60 fps animated RGBA sprite with anti-aliased edges via `UpdateLayeredWindow`, on a **mixed-DPI two-monitor** setup.

**Add one control arm:** a **single full-screen** layered window (3840×2160) drawing the same nine sprites. §0 predicts this is ~14× more pixels per frame; measuring both turns that prediction into a decision.

**Measure, per configuration:**
1. Process CPU time (not wall clock), sampled over 60 s after a 10 s warm-up.
2. Whether a click on a fully transparent pixel reaches a Notepad window behind it, and a click on an opaque pixel reaches the pet — **verified by hand, on both monitors, at both DPI scales**.
3. GPU utilisation and whether DWM composition cost rises.
4. Frame pacing: dropped frames and the p99 frame interval.

**Pass criteria (all must hold):**

| # | Criterion | Threshold |
|---|---|---|
| P1 | Click on a transparent pixel reaches the window behind | **9/9 pets**, on both monitors, at both DPI scales |
| P2 | Click on an opaque pixel reaches the pet | **9/9 pets** |
| P3 | Total CPU for N=9 (9 small windows), sampled over 60 s | **< 3% of one core** |
| P4 | Dropped frames at N=9 over 60 s | **< 1%** |
| P5 | Correct rendering after dragging a pet across the DPI boundary and back | **no blur, no wrong size, no stuck hit region** |
| P6 | The full-screen-overlay control arm | **measurably worse than 9 small windows** — if it is *not*, the §0 area-scaling argument is wrong and the overlay design should be reconsidered |

**Calibration:** [openai/codex#20680](https://github.com/openai/codex/issues/20680) measured an Electron pet overlay at **30–73% of one logical core** on Windows. P3's 3% is the bar that makes PetDock acceptable where that overlay is not.

**Fail action, decided in advance:** if **P1/P2** fail, the layered path is wrong and you must switch to `WS_EX_NOREDIRECTIONBITMAP` + a self-computed hit-test mask — which changes the shell's design but **not** the plugin-VM decision. If **P3/P4** fail, the per-pet-window model is wrong: move to one full-screen overlay window with one hit-test mask for all pets. If **P5** fails, you have a DPI bug that must be fixed before anything else is built, because it will poison every later feature.

### The second spike (run in parallel, half a day)

**"What does one QuickJS call per plugin per frame actually cost?"**

Load `quickjs-emscripten`, create **N = 1, 4, 9, 16** runtimes, and call one exported function per runtime per frame with a small numeric payload (say 8 floats in, 8 floats out), at a simulated 60 Hz. Measure wall-clock per frame and total CPU.

**Pass criterion:** at N=16, the plugin VM's share of the 16.67 ms budget is **< 4 ms**. If it is not, the batching strategy is wrong and you must keep animation state entirely inside the guest (one call per *second*, not per frame) and interpolate host-side.

**What this spike explicitly does not test:** security of the sandbox, permission enforcement, plugin distribution, or the Live2D/Spine renderers. Those are later, cheaper decisions once the two numbers above are known.

---

## 6. Unverified / could not confirm

- **The QuickJS-vs-V8 performance ratio** — a 2020 forum quote, not a current benchmark.
- **Per-plugin baseline RSS** for a V8 isolate, a QuickJS runtime, a wasmtime instance, or a Node process. No authoritative source found. Wasmtime publishes 1 MiB *metadata* and ~5 µs instantiation, not RSS.
- **Cross-boundary call overhead for the 60 Hz small-payload pattern** on any engine — this is the spike in §5.
- **Electron's "will not work on Linux with GTK" line** is *absent* from the current docs; the six-bullet list in §1A is the complete current text. Do not cite the Linux line.
- **Electron `transparent: true` disabling GPU paths** or interacting with `backgroundThrottling` — no official source found. The nearest real evidence is the NW.js compositor-mode change.
- **Whether Godot's `mouse_passthrough` reaches other applications** — the doc says "of the same application".
- **Which compositor path Qt's `WA_TranslucentBackground` uses under OpenGL on Windows.**
- **A single authoritative hello-world installer size** per toolchain — figures vary by version and tool (Electron 115/180/85/61 MB across sources).
- **Qt "static linking requires a commercial license"** as an official Qt position — the FAQ says LGPLv3 permits it with a relinking mechanism; community sources assert otherwise.
- **Blender's official sandbox position**; **DSH's exact sandbox boundary**; **Steam Workshop sandboxing** (community sources only).
- **WPF layered-window hardware acceleration status on Windows 11** — the Learn table says WDDM systems are hardware-accelerated, but does not name Windows 11 explicitly, and the CPU copy for hit testing remains regardless.

---

## 7. Sources index

**Microsoft Learn:** [Window Features (layered windows, hit testing)](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features) · [WM_NCHITTEST](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest) · [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) · [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity) · [High DPI Desktop Application Development](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows) · [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) · [JOBOBJECT_BASIC_LIMIT_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information) · [CPU rate control](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information) · [AppContainer isolation](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation) · [WPF Graphics Rendering Tiers](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/graphics-rendering-tiers) · [Window.AllowsTransparency](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency) · [System.AppDomain (no security boundary)](https://learn.microsoft.com/en-us/dotnet/api/system.appdomain) · [MSIX behind the scenes](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes) · [WebView2 DefaultBackgroundColor](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.defaultbackgroundcolor)

**Magazine/architecture:** [Kerr, "High-Performance Window Layering Using the Windows Composition Engine"](https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine) · [Need, "Transparent windows in WPF"](https://dwayneneed.github.io/wpf/2008/09/08/transparent-windows-in-wpf.html)

**Issue trackers:** [electron#1335](https://github.com/electron/electron/issues/1335) · [electron#23042](https://github.com/electron/electron/issues/23042) · [electron#35030](https://github.com/electron/electron/issues/35030) · [electron#52456](https://github.com/electron/electron/issues/52456) · [nwjs#8342](https://github.com/nwjs/nw.js/issues/8342) · [tauri#13070](https://github.com/tauri-apps/tauri/issues/13070) · [tauri#2090](https://github.com/tauri-apps/tauri/issues/2090) · [tauri#6164](https://github.com/tauri-apps/tauri/issues/6164) · [tauri#4881](https://github.com/tauri-apps/tauri/issues/4881) · [microsoft-ui-xaml#10746](https://github.com/microsoft/microsoft-ui-xaml/discussions/10746) · [microsoft-ui-xaml#1247](https://github.com/microsoft/microsoft-ui-xaml/issues/1247) · [WebView2Feedback#5752](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5752) · [WebView2Feedback#4945](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4945) · [godot#89205](https://github.com/godotengine/godot/issues/89205) · [godot#85075](https://github.com/godotengine/godot/issues/85075) · [godot#57835](https://github.com/godotengine/godot/issues/57835) · [dotnet/wpf#6096](https://github.com/dotnet/wpf/issues/6096) · [isolated-vm GHSA-864f-rcv7-6rh4](https://www.endorlabs.com/learn/ghsa-864f-rcv7-6rh4-critical-type-confusion-vulnerability-in-isolated-vm)

**Framework docs:** [Electron custom window styles](https://raw.githubusercontent.com/electron/electron/main/docs) · [Electron window interactions](https://electronjs.org/docs) · [Electron BaseWindow](https://www.electronjs.org/docs) · [Electron process model](https://electronjs.org/docs) · [Tauri config](https://v2.tauri.app/reference/config/) · [Tauri permissions](https://v2.tauri.app/security/permissions/) · [Tauri Windows installer](https://v2.tauri.app/distribute/windows-installer/) · [Godot Window](https://docs.godotengine.org/en/stable/classes/class_window.html) · [Godot DisplayServer](https://docs.godotengine.org/en/stable/classes/class_displayserver.html) · [Qt WidgetAttribute](https://doc.qt.io/qt-6/qt.html#WidgetAttribute-enum) · [Qt ShapedClock](https://doc.qt.io/qt-6/qtwidgets-widgets-shapedclock-example.html)

**Plugin VM:** [Node vm docs](https://nodejs.org/api/vm.html) · [isolated-vm](https://github.com/laverdet/isolated-vm) · [quickjs-emscripten](https://github.com/justjake/quickjs-emscripten) · [quickjs-wasi](https://github.com/vercel-labs/quickjs-wasi) · [Wasmtime interrupting wasm](https://docs.wasmtime.dev/examples-interrupting-wasm.html) · [Extism manifest](https://extism.org/docs) · [Deno security](https://docs.deno.com/runtime/fundamentals/security/) · [WASI security](https://wasi.dev/security) · [Chrome declare permissions](https://developer.chrome.com/docs) · [VS Code extension runtime security](https://code.visualstudio.com/docs) · [Obsidian plugin security](https://github.com/obsidianmd/obsidian-help/blob/master/en/Extending%20Obsidian/Plugin%20security.md) · [Figma: how we built the plugin system](https://www.figma.com/blog/how-we-built-the-figma-plugin-system)

**Prior art:** [VPet](https://github.com/LorisYounger/VPet) · [BongoCat](https://github.com/ayangweb/BongoCat) · [UniWindowController](https://github.com/kirurobo/UniWindowController)

**Distribution:** [Electron code signing](https://www.electronjs.org/docs) · [CA/B Forum code signing requirements](https://cabforum.org/working-groups/code-signing/requirements) · [Azure Trusted Signing quickstart](https://learn.microsoft.com/en-us/azure/trusted-signing/quickstart) · [Qt LGPL obligations](https://www.qt.io/development/open-source-lgpl-obligations) · [Qt for Small Business](https://www.qt.io/development/qt-for-small-business) · [Godot license compliance](https://docs.godotengine.org/en/stable/about/complying_with_licenses.html) · [Stack Overflow 2024 technology survey](https://survey.stackoverflow.co/2024/technology)
