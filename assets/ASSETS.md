# assets/ — 资产来源登记册（ASSET MANIFEST）

> **这份文件的作用**：回答「这个文件是谁给的、能不能发行、删了会怎样」。
> 在删除、移动、覆盖 **任何** 资产文件之前，先在这里查它一行。查不到就先问所有者，不要删。
>
> 为什么需要它：2026-10-15 有一次真实事故 —— 清理脏树时把所有者放在仓库根的一张桌宠原图
> 无 git 历史、不进回收站，三条恢复路径全部不存在，只能请所有者重新提供。
> 根因不是手滑，是**没有任何地方写清那个文件是什么**。这份登记册就是那个地方。
> 规则见 `原规格 52` §5（素材零分发）与 §6（发布前合规清单）。

## 0. 删除前的硬规则

1. **未跟踪 ≠ 没用。** 所有者常把要用的图直接丢进仓库（本项目的素材政策就是
   「开发测试素材不入库」，见账本 decision 2026-10-14）。这类文件删掉即不可恢复。
2. **在本表里查不到的文件，不要删。** 先问所有者，或先 `read_image` 打开看它是什么。
3. **不要用文件名当判据。** 有些原图的名字看起来像表情包，实际是桌宠原图。
4. 确实要删的，先在本表把它标成 `retired` 并写明原因，再从磁盘删。

**摆放规则（2026-10-30 修订）**：`assets/` 只放**宿主资源**（图标、界面文案表）。
**宠物包不放 `assets/`** —— 它们和用户自己导入的桌宠是同一类东西，统一放在仓库根的
`pets/` 下，运行期再复制进数据区的 `packs/`（`pets/` → `data/packs/`，同名目录，一一对应）。
仓库根只留 `README.md` / `LICENSE` / `*.sln` / `package*.json` / `.gitignore` 这类仓库级文件，
以及 `pets/` 这一个内容目录。两处「本机自用、永不入库」的目录被 `.gitignore` 整目录排除：
`assets/sample/local/` 与 `assets/user/`。若仓库根又出现未登记的媒体文件，
`node tools/asset-manifest.mjs` 会报 `UNREGISTERED` 并以 exit 1 结束。

## 1. 登记表

| 路径 | 是什么 | 来源 / 授权 | 可发行？ | 删了会怎样 |
|---|---|---|---|---|
| `assets/icons/`（12 个生成文件 + `README.md`） | 应用图标全套（16…512 PNG + icon.ico + 圆角版），并已由 `PetDock.Core.csproj` 的 `<ApplicationIcon>` 嵌进 exe | **本项目自绘**：`tools/make-icons.py --pixel` 画的像素猫（2026-10-26 所有者从候选里选定；16×16 网格原生、整数倍 NEAREST 放大） | ✅ **是** — 自有输出，随内核以 Apache-2.0 授权 | 可用 `python tools/make-icons.py --pixel` 重新生成（形状在 `draw_pixel_cat()` 里） |
| `assets/user/icons-source/`（本机自用） | 图标源图。2026-10-17 之前它是仓库跟踪文件，后因**出处与授权无法追溯**整目录出库，仓库里的图标改为自绘 | **授权不明**，因此不得再参与任何发行物 | ❌ **否** — 本机留存；由它派生的产物同样不得发行 | 只在本机；仓库侧不需要它（`tools/make-icons.py --pixel` 可独立重新生成整套图标） |
| `assets/sample/local/`（整个目录，本机自用） | 所有者自留的桌宠原图与由其抠出的贴图（具体文件名与尺寸不入册，避免把私人素材清单写进公开文档） | 所有者自行提供；**不随仓库分发** | ❌ **否** — `.gitignore` 排除整个目录，永不入库、永不发行 | 贴图可从原图重新抠；原图删了不可恢复。**2026-10-15 有过一次误删事故**，§0 的三条规矩因此而来 |
| `assets/user/`（整个目录，本机自用） | 所有者自留的第三方宠物素材（具体包名、文件数、体积不入册）。用途 = 导入器的字段核对素材 | 所有者自行提供的第三方素材；**版权归原作者，不是 CC 授权素材** | ❌ **否** — `.gitignore` 排除整个目录，永不入库、永不发行 | 可由所有者副本重新放入；删掉会阻断导入器的字段核对 |
| `pets/sample-pet/`（15 个文件） | M2 自建示例宠物的金图夹具（10 帧 + icon + 3 个 anim json + 主描述）。2026-10-18 由 `fixtures/sample-pet/` 迁入；2026-10-30 随「宠物包不入 `assets/`」的规则移到 `pets/`（见 §0） | `tools/gen-sample-pet.mjs` 程序化生成，CC0 | ✅ **是** | 可用 `node tools/gen-sample-pet.mjs --out pets/sample-pet` 重新生成（已验证幂等） |

## 2. 桌宠形象：从原图到贴图的完整链路

```
assets/sample/local/<原图文件名>          所有者提供的原图（本机自留，不入库）
        │  python tools/make-pet-from-image.py \
│         --src assets/sample/local/<原图文件名> \
        │         --out assets/sample/local/pet-sample.png
        ▼
assets/sample/local/pet-sample.png      128×128 带 alpha，四角 alpha=0（满足 M0 命中穿透契约）
        │  PETDOCK_PET_IMAGE=assets/sample/local/pet-sample.png（显式指名，sprite-seq 读它）
        ▼
屏幕上看到的桌宠
```

- 抠图工具：`tools/make-pet-from-image.py`。从画面四边向内做**连通域洪泛**，只把与边缘
  连通的近白像素判为背景（`min(R,G,B)>=236` 且 `max-min<=16`），因此不会把角色身上的
  白色衣服挖穿；再按不透明外接框等比缩放居中。
- 实测（2026-10-15）：`KNOCKOUT 背景占比=46.3%`、`opaque=7136 transparent=7806`、
  `CENTRE rgba=(82,97,128,255)`、`SPRITE-OK`。装入后 `--selftest` 报
  `dib-sample center=bgra=163,139,125,255`、`hit transparent=pass`、`SELF-OK`。
- 中心色 `(82,97,128)` 与 `tools/probes/_verify-t7-report-wb.md` 记的
  `SRC centre(64,64) rgba=82,97,128,255` 逐字节一致 —— 即当前贴图与 t7 判定字节同源。

## 3. 为什么本机自用素材不入库

`原规格 01` P-07 与 `原规格 52` §5 规定**素材零分发**：
发布物中第三方素材数量必须为 0（`M-12`）。本机自留的桌宠原图是社区共创形象（CC BY-NC-SA 4.0，
**NC 条款禁止商业使用**），DyberPet/《鸣潮》素材是商业游戏资产，图标源图来源不明，
因此它们只能存在于所有者本机。

架构上由三件事保证这个边界不会破：
1. `.gitignore` 排除 `assets/sample/local/`（桌宠原图与派生贴图）与 `assets/user/`
   （第三方宠物素材 + 图标源图）—— 仓库里从来没有它们；
2. `SampleImageContent.ImagePath()` **只认** `assets/sample/local/`，仓库侧没有回落资产
（2026-10-16 删除了那张会被误当成宠物图的程序化蓝团）——本机看到的是所有者自留素材，
   而 clone 这个仓库的人看到的是 原规格 31 Sample Pack 的可动示例宠物；
3. 可见宠物由 sprite-seq 的 `PETDOCK_PET_IMAGE` / `PETDOCK_SAMPLE_PACK` 显式指名，
   默认（无环境变量）即 `fixtures/sample-pet/`，来源路径写进 stderr 一行
   （`sprite-seq source=... form=pack|single-image clips=N`）。

应用图标是唯一「曾经破过这个边界」的资产：它原本派生自来源不明的 `icon.jpg`，而派生图是
**受跟踪**的。2026-10-17 已改为 `tools/make-icons.py` 自绘、把源图移出仓库。

**发行前**（`原规格 52` §6 清单）：本表里所有标 ❌ 的项都必须已替换为自有/CC0 资产，
且 `assets/user/` 与 `assets/sample/local/` 下的任何文件都不得出现在任何发布物中。
