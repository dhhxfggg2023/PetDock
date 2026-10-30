# assets/ — 资产来源登记册（ASSET MANIFEST）

> **这份文件的作用**：回答「这个文件是谁给的、能不能发行、删了会怎样」。
> 在删除、移动、覆盖 **任何** 资产文件之前，先在这里查它一行。查不到就先问所有者，不要删。
>
> 为什么需要它：2026-10-15 有一次真实事故 —— 清理脏树时把所有者放在仓库根的桌宠原图
> `gj-meme-857.webp` 标成「一次性网络下载的表情图，与项目无关」直接删掉，而它未跟踪、
> 无 git 历史、不进回收站，三条恢复路径全部不存在，只能请所有者重新提供。
> 根因不是手滑，是**没有任何地方写清那个文件是什么**。这份登记册就是那个地方。
> 规则见 `原规格 52` §5（素材零分发）与 §6（发布前合规清单）。

## 0. 删除前的硬规则

1. **未跟踪 ≠ 没用。** 所有者常把要用的图直接丢进仓库（本项目的素材政策就是
   「开发测试素材不入库」，见账本 decision 2026-10-14）。这类文件删掉即不可恢复。
2. **在本表里查不到的文件，不要删。** 先问所有者，或先 `read_image` 打开看它是什么。
3. **不要用文件名当判据。** `gj-meme-857.webp` 这名字看起来像表情包，实际是桌宠原图。
4. 确实要删的，先在本表把它标成 `retired` 并写明原因，再从磁盘删。

**摆放规则（2026-10-16 收拢）**：资产一律放在 `assets/` 树下 —— 仓库根**不再**有散落的
图片文件。仓库根只留 `README.md` / `LICENSE` / `*.sln` / `package*.json` / `.gitignore`
这类仓库级文件。两处「本机自用、永不入库」的目录都被 `.gitignore` 整目录排除：
`assets/sample/local/`（最后一位所有者的桌宠素材）与 `assets/user/`（所有者提供的第三方
宠物素材与图标源图）。若仓库根又出现未登记的媒体文件，
`node tools/asset-manifest.mjs` 会报 `UNREGISTERED` 并以 exit 1 结束。

## 1. 登记表

| 路径 | 是什么 | 来源 / 授权 | 可发行？ | 删了会怎样 |
|---|---|---|---|---|
| `assets/icons/`（12 个生成文件 + `README.md`） | 应用图标全套（16…512 PNG + icon.ico + 圆角版），并已由 `PetDock.Core.csproj` 的 `<ApplicationIcon>` 嵌进 exe | **本项目自绘**：`tools/make-icons.py --pixel` 画的像素猫（2026-10-26 所有者从候选里选定；16×16 网格原生、整数倍 NEAREST 放大） | ✅ **是** — 自有输出，随内核以 Apache-2.0 授权 | 可用 `python tools/make-icons.py --pixel` 重新生成（形状在 `draw_pixel_cat()` 里） |
| `assets/user/icons-source/icon.jpg`（163,338 B，512×512，**本机自用**） | **软件图标源图**（银发金瞳少女立绘）。2026-10-17 之前它放在 `assets/icons/source/` 且受 git 跟踪，那套图标全部由它生成 | 朋友转发，**出处与授权均不明** | ❌ **否** — 本机可用；**由它派生的图标同样不得发行**，所以仓库里的图标已改成自绘图 | 已 `git rm --cached` 出库，只在本机；删了请所有者重新提供 |
| `assets/sample/local/`（整个目录，本机自用） | ① `gj-meme-857.webp`：所有者提供的**桌宠原图**（975×975 鲸鱼娘 Q 版，85,770 B）② `pet-sample.png`：由它经 `tools/make-pet-from-image.py` 抠出的 128×128 贴图（23,674 B，四角 alpha=0） | 所有者自行下载提供；形象来自社区共创（主流说法原型是 B 站「上善无形」的角色「溟月」，以 **CC BY-NC-SA 4.0** 开放二创，NC 禁止商业使用） | ❌ **否** — `.gitignore` 排除整个目录，永不入库、永不发行 | 贴图可从原图重新抠；**原图删了不可恢复**，只能请所有者重新提供（见 §2）。**这就是 2026-10-15 被误删的那个文件** |
| `assets/user/`（整个目录，本机自用） | 所有者提供的**真实第三方宠物素材**；当前含 `assets/user/dyberpet/守岸人/`（DyberPet 角色包，349 文件 / 49,854,928 B：310 PNG + 32 WAV + 6 JSON + 1 JPG；`pet_conf.json` / `act_conf.json` / `msg_conf.json` + `action/`、`info/`、`items/`、`note/`）。用途 = **M3 适配器的字段核对素材**（`原规格 99` 把「DyberPet 字段未实测」列为 M3 开工前必修项） | 所有者自行下载提供（《鸣潮》守岸人；形象与音频版权归库洛游戏，**不是** CC 授权素材） | ❌ **否** — `.gitignore` 排除整个目录，永不入库、永不发行 | 可由所有者副本重新放入；删掉会阻断 M3 的字段核对 |
| `assets/sample-pet/`（15 个文件） | M2 自建示例宠物的金图夹具（10 帧 + icon + 3 个 anim json + 主描述）。2026-10-18 由 `fixtures/sample-pet/` 迁入本表下，以符合 §0 的「资产一律放在 `assets/` 树下」 | `tools/gen-sample-pet.mjs` 程序化生成，CC0 | ✅ **是** | 可用 `node tools/gen-sample-pet.mjs --out assets/sample-pet` 重新生成（已验证幂等） |
| `assets/bongocat-pack/`（19 个文件） | M7 的 BongoCat 交互包素材（18 帧 + icon + 7 个 anim json + `petdock-sample.json` 主描述）。7 个片段：`idle`(4) / `look_up|down|left|right`(各 2) / `paw_l|paw_r`(各 3)。**自绘几何猫**，与原 BongoCat 应用的素材无任何关系 | `tools/gen-bongocat-pack.mjs` 程序化生成（编码器复用 `tools/lib/png.mjs`），CC0 | ✅ **是** — 自有输出；原规格 33 §6 的三条法律约束（不引用/不打包/不逆向原应用、素材自有、包名不冒充官方）在本目录上是代码事实 | 可用 `node tools/gen-bongocat-pack.mjs --out assets/bongocat-pack` 重新生成（确定性输出） |

## 2. 桌宠形象：从原图到贴图的完整链路

```
assets/sample/local/gj-meme-857.webp   所有者提供的原图（975×975，白底）
        │  python tools/make-pet-from-image.py \
        │         --src assets/sample/local/gj-meme-857.webp \
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
发布物中第三方素材数量必须为 0（`M-12`）。鲸鱼娘形象来自社区共创（CC BY-NC-SA 4.0，
**NC 条款禁止商业使用**），DyberPet/《鸣潮》素材是商业游戏资产，图标源图来源不明，
因此它们只能存在于所有者本机。

架构上由三件事保证这个边界不会破：
1. `.gitignore` 排除 `assets/sample/local/`（桌宠原图与派生贴图）与 `assets/user/`
   （第三方宠物素材 + 图标源图）—— 仓库里从来没有它们；
2. `SampleImageContent.ImagePath()` **只认** `assets/sample/local/`，仓库侧没有回落资产
   （2026-10-16 删除了那张会被误当成宠物图的程序化蓝团）——本机看到鲸鱼娘，
   而 clone 这个仓库的人看到的是 原规格 31 Sample Pack 的可动示例宠物；
3. 可见宠物由 sprite-seq 的 `PETDOCK_PET_IMAGE` / `PETDOCK_SAMPLE_PACK` 显式指名，
   默认（无环境变量）即 `fixtures/sample-pet/`，来源路径写进 stderr 一行
   （`sprite-seq source=... form=pack|single-image clips=N`）。

应用图标是唯一「曾经破过这个边界」的资产：它原本派生自来源不明的 `icon.jpg`，而派生图是
**受跟踪**的。2026-10-17 已改为 `tools/make-icons.py` 自绘、把源图移出仓库。

**发行前**（`原规格 52` §6 清单）：本表里所有标 ❌ 的项都必须已替换为自有/CC0 资产，
且 `assets/user/` 与 `assets/sample/local/` 下的任何文件都不得出现在任何发布物中。
