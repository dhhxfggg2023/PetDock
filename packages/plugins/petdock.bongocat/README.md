# BongoCat 交互重写（Tier 0，PetDock 自有素材）

这是 `原规格 33` 的实现：把 BongoCat 的三类交互用 PetDock 的 ABI **重写**，
**不嵌入**原 Tauri 应用，也不引用、不打包、不逆向它的任何素材（原规格 33 §6）。

## 这是什么

| | |
|---|---|
| 插件 id | `com.petdock.bongocat.interaction` |
| kind / tier | `brain` / `0`（Tier 0 + 托管 dll，走既有的越界记账，见下） |
| 实现接口 | `IBrain`（`PetDock.Core.PetRuntime.IBrain`） |
| 素材 | `pets/bongocat-pack/`，由 `tools/gen-bongocat-pack.mjs` 程序化生成，**CC0** |
| 许可 | CC0-1.0（素材与代码都是本项目自有） |

## 三类交互怎么实现

### ① 眼睛/头部跟随鼠标

`WorldSnapshot` 每 tick 给出光标位置，Brain 按「光标相对宠物中心」的方位切到
`look_left` / `look_right` / `look_up` / `look_down` 四个朝向片段之一，死区 24px。

**为什么不是 原规格 33 §4 写的 `kind=interaction` + `input.mousePosition` + Interaction surface**：
`IPointerSurface` 只收窗口内的按下/移动/抬起，拿不到全局光标，**也没有任何动画输出通道** ——
它无法让宠物动起来。而 `IBrain` 每 tick 拿得到 `WorldSnapshot`，也发得出 `SetAnimation`。
这个偏离已记在 `RFC-09` 与账本 decision 里。

光标的来源**优先用 `input.pointer.move` 事件**（`pos` 是宿主采样的原始屏幕坐标，窗口外也有值），
事件没到过时才退回 `world.Cursor`（窗口内局部坐标）。理由：`WorldSnapshot.Cursor` 按 原规格 11 §7.1
在光标离开窗口时是 `null`，而「猫看着远处的鼠标」恰恰要求窗口外也继续跟。

### ② 敲键盘 → 猫爪敲击

收 `input.key.down`，按 vkey 落在键盘左半区还是右半区，抬**对侧**的爪子
（`paw_r` / `paw_l`），保持 400ms 后回到 idle。

**这条在 M7 里没有真实事件源**：core 中 `SetWindowsHookEx` 调用点为 0，且
`EventSelfTest.HookGate()` 把「0 个钩子调用点」写成了一条断言。装全局键盘钩子需要托盘上
那个不可隐藏的「暂停全局输入钩子」固定项（原规格 14:110 / 原规格 18:22），是 M4 预算之外的
一块（RFC-05 §D 已裁决不装）。所以：

- 事件到达 → 立刻抬爪（逻辑是真的，已在 `--hosttest` 的 `keyPaw` 判据里用注入事件验证）；
- 事件不到达 → 什么都不发生，且 `DisposeAsync` 的诊断行会打出
  `keyEventsSeen=False (input.key.* has no producer in M7; see 原规格)` ——
  **让「没有事件源」与「实现坏了」在输出里可分辨**。

### ③ 闲置呼吸/眨眼

素材侧已做：`idle` 4 帧，第 4 帧是眨眼。Brain 在无输入时保持 `idle` 即可。

## 素材与片段名

`pets/bongocat-pack/` 是 原规格 31 Sample Pack 形态（`petdock-sample.json` + `anim/*.json` + `img/*/*.png`），
所以现成的 `petdock.presenter.sprite-seq` 直接能渲染，内核不需要认识第二种素材格式
（原规格 01 P-02 第 2 条：core 不认识任何素材格式）。

| 片段 | 帧数 | fps | loop |
|---|---|---|---|
| `idle` | 4 | 6 | loop |
| `look_up` / `look_down` / `look_left` / `look_right` | 2 each | 4 | loop |
| `paw_l` / `paw_r` | 3 each | 8 | once |

重新生成：`node tools/gen-bongocat-pack.mjs --out pets/bongocat-pack`

## 已知越界（显式记账，不埋在注释里）

本插件是 **Tier 0 + 托管 dll**，与 原规格 13 §2「Tier 0 = 声明式资源包，无展开代码」字面冲突。
宿主在每次装载时把这条写进 `surface-overrun` 日志与 `overruns=N` 诊断行（`PetSurfaceRoster.Overruns`），
`--drivetest` 的 `drive-overrun-count` 会把它数出来。方向由 RFC 决定，不由本插件自行决定。

## 怎么跑

```powershell
# 1. 构建（BongoCat 插件与内核一起）
dotnet build PetDock.sln -c Release

# 2. 让宿主用 BongoCat 当行为层，并指向 BongoCat 的素材包
$env:PETDOCK_BRAIN = 'com.petdock.bongocat.interaction'
$env:PETDOCK_SAMPLE_PACK = "$PWD\assets\bongocat-pack"
dotnet run --project packages/core/PetDock.Core -c Release

# 3. 无头自检（不需要交互式桌面）
$env:PETDOCK_PLUGINS_ROOT = "$PWD\packages\plugins"
dotnet exec packages\core\PetDock.Core\bin\Release\net8.0-windows\PetDock.Core.dll --hosttest com.petdock.bongocat.interaction
```

## 授权弹窗与记忆（原规格 33 §7 第三条）

`input.mousePosition` 在本插件里只出现在 manifest 的 `capabilities` 声明中；实现用的是
`WorldSnapshot` 与 `input.pointer.move`，**两者都不需要运行时授权**（宿主本来就在采样光标）。
所以「授权弹窗出现一次后记忆」这条在 M7 的形态是：**声明了但不需要弹窗** ——
M7 没有新增需要用户授权的输入能力（键盘钩子未装），因此也没有新的弹窗路径要记忆。
这一条与 原规格 33 §7 原措辞的差异记在 RFC-09 §3。
