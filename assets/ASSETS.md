# assets/ — 资产来源登记册（ASSET MANIFEST）

> **这份文件的作用**：回答「这个文件是谁给的、能不能发行、删了会怎样」。
> 在删除、移动、覆盖 **任何** 资产文件之前，先在这里查它一行。查不到就先问所有者，不要删。
>
> 为什么需要它：2026-10-15 有一次真实事故 —— 清理脏树时把所有者放在仓库根的桌宠原图
> `gj-meme-857.webp` 标成「一次性网络下载的表情图，与项目无关」直接删掉，而它未跟踪、
> 无 git 历史、不进回收站，三条恢复路径全部不存在，只能请所有者重新提供。
> 根因不是手滑，是**没有任何地方写清那个文件是什么**。这份登记册就是那个地方。
> 规则见 `docs/52-legal-licensing.md` §5（素材零分发）与 §6（发布前合规清单）。

## 0. 删除前的硬规则

1. **未跟踪 ≠ 没用。** 所有者常把要用的图直接放在仓库根而不入库（本项目的素材政策就是
   「开发测试素材不入库」，见账本 decision 2026-10-14）。这类文件删掉即不可恢复。
2. **在本表里查不到的文件，不要删。** 先问所有者，或先 `read_image` 打开看它是什么。
3. **不要用文件名当判据。** `gj-meme-857.webp` 这名字看起来像表情包，实际是桌宠原图。
4. 确实要删的，先在本表把它标成 `retired` 并写明原因，再从磁盘删。

## 1. 登记表

| 路径 | 是什么 | 来源 / 授权 | 可发行？ | 删了会怎样 |
|---|---|---|---|---|
| `assets/icons/`（12 个文件） | 应用图标全套（16…512 PNG + icon.ico + 圆角版） | 由 `icon.jpg` 经 `tools/make-icons.py` 生成 | ⚠️ **否** — 源图 `icon.jpg` 来源不明，见 `assets/icons/README.md` 顶部警告块 | 可用 `python tools/make-icons.py` 重新生成 |
| `assets/sample/pet-sample.png` | **retired 2026-10-16（t8 验收 3）**：仓库内自有的程序化示例贴图（128×128，蓝圆团 + 双眼，898 B）。删除原因：它不是宠物，却因为落在「示例宠物图」这个位置上被反复误当成桌宠画面；所有者明确要求「没用的图片删了，不然老是用错」。既无本地贴图又未配包时由内核画代码占位图（`SolidFrameContent`）并在日志标注 placeholder。重新生成方式：`python tools/gen-sample-sprite.py`（确定性，如将来确有必要） |
| `assets/sample/local/`（整个目录） | 所有者**本机自用**的桌宠贴图及中间产物 | 见下方 §2；形象为社区共创鲸鱼娘 | ❌ **否** — `.gitignore` 排除，永不入库 | 从 §2 的原图重新抠即可 |
| `assets/user/dyberpet/守岸人/`（349 个文件 / 49,854,928 B） | 所有者提供的**真实第三方桌宠素材**（DyberPet 角色包）：310 PNG + 32 WAV + 6 JSON + 1 JPG；`pet_conf.json` / `act_conf.json` / `msg_conf.json` + `action/`、`info/`、`items/`、`note/`。用途 = **M3 适配器的字段核对素材**（`docs/99` 把「DyberPet 字段未实测」列为 M3 开工前必修项） | 所有者自行下载提供（《鸣潮》守岸人；形象与音频版权归库洛游戏，**不是** CC 授权素材） | ❌ **否** — `.gitignore:26` 排除整个 `assets/user/`，永不入库、永不发行（`docs/52` §5 素材零分发） | 可由所有者副本重新放入；删掉会阻断 M3 的字段核对 |
| `fixtures/sample-pet/`（15 个文件） | M2 自建示例宠物的金图夹具（10 帧 + icon + 3 个 anim json + 主描述） | `tools/gen-sample-pet.mjs` 程序化生成，CC0 | ✅ **是** | 可用 `node tools/gen-sample-pet.mjs --out fixtures/sample-pet` 重新生成（已验证幂等） |

### 仓库根（不在 `assets/` 下，但同属资产）

| 路径 | 是什么 | 来源 / 授权 | 可发行？ | 删了会怎样 |
|---|---|---|---|---|
| `icon.jpg` | **软件图标**源图（512×512，银发金瞳少女立绘） | 朋友转发，**出处与授权均不明** | ❌ **否** — 发行前必须替换（`docs/51` R-01） | **不可恢复**；发行前必须换成自有/CC0 图标 |
| `gj-meme-857.webp` | **桌宠原图**（975×975，鲸鱼娘 Q 版） | 所有者自行下载提供 | ❌ **否** — 社区共创，CC BY-NC-SA 4.0 | **不可恢复**（未跟踪、不进回收站）。**这就是 2026-10-15 被误删的那个文件** |

## 2. 桌宠形象：从原图到贴图的完整链路

```
gj-meme-857.webp                      所有者提供的原图（975×975，白底）
        │  python tools/make-pet-from-image.py --src gj-meme-857.webp \
        │         --out assets/sample/local/pet-sample.png
        ▼
assets/sample/local/pet-sample.png    128×128 带 alpha，四角 alpha=0（满足 M0 命中穿透契约）
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

`docs/01-principles.md` P-07 与 `docs/52-legal-licensing.md` §5 规定**素材零分发**：
发布物中第三方素材数量必须为 0（`M-12`）。鲸鱼娘形象来自社区共创（主流说法原型是
B 站「上善无形」的角色「溟月」，以 CC BY-NC-SA 4.0 开放二创），**NC 条款禁止商业使用**，
因此它只能存在于所有者本机。

架构上由三件事保证这个边界不会破：
1. `.gitignore` 排除 `assets/sample/local/` 与 `gj-meme-857.webp` —— 仓库里从来没有它们；
2. `SampleImageContent.ImagePath()` **只认** `assets/sample/local/`，仓库侧没有回落资产
   （2026-10-16 删除了那张会被误当成宠物图的程序化蓝团）——本机看到鲸鱼娘，
   而 clone 这个仓库的人看到的是 docs/31 Sample Pack 的可动示例宠物；
3. 可见宠物由 sprite-seq 的 `PETDOCK_PET_IMAGE` / `PETDOCK_SAMPLE_PACK` 显式指名，
   默认（无环境变量）即 `fixtures/sample-pet/`，来源路径写进 stderr 一行
   （`sprite-seq source=... form=pack|single-image clips=N`）。

**发行前**（`docs/52` §6 清单）：本表里所有标 ❌ 的项都必须已替换为自有/CC0 资产，
且 `icon.jpg` 与 `gj-meme-857.webp` 不得出现在任何发布物中。
