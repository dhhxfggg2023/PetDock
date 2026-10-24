# tools/ — 仓库机器门禁（docs/40 D-01 ~ D-04b，D-16 见 verify.mjs）

这批 Node 脚本把 `docs/40-development-paradigm.md` 的机器可执行规则落成可执行命令。
零 npm 依赖（只用 Node 内置模块），ESM（`.mjs`），要求 Node >= 20（见根 `package.json` 的 `engines`）。

## 总入口

```powershell
npm run verify      # D-16 验证门禁：默认三层 static + build + contract（秒级）
npm run verify:all  # 七层全跑（会闪桌面、跑 publish，分钟级）
npm run lint        # 只跑静态门：depscan -> lint:core -> lint:docs，任一红即停
npm run depscan     # 只跑依赖方向门
npm run lint:core   # 只跑内核规模 / 概念门
npm run lint:docs   # 只跑文档引用门
```

单文件直跑：`node tools/verify.mjs`、`node tools/depscan.mjs`、`node tools/lint-core.mjs`、`node tools/doclink.mjs`。
pwsh 不支持 `&&`，串联命令用 `;`。

## verify.mjs — D-16 本地验证门（七层一条命令）

`docs/40` §5.3 的 D-16 条文落成的入口：七层，每层具名、可单独复现，每层声明**证明什么 / 不证明什么**，
每步直接写 `.out`/`.err` 文件（不接管道，避开「合并管道把成功报成 1」），marker 缺失即红。

| 层 | 内容 | 判绿的 marker |
| --- | --- | --- |
| `static` (L0) | 七道静态门逐条跑 | 各门自己的 PASS |
| `build` (L1) | `dotnet build PetDock.sln -c Debug` | 只判退出码（dotnet 结论行随区域设置漂） |
| `contract` (L2) | `--hosttest` / `--drivetest` / `--eventtest` / `--animtest idle walk hover` | HOST-OK / DRIVE-OK / EVENT-OK / ANIM-OK |
| `window` (L3) | `--uitest --log <证据>/uitest.log` | UI-OK |
| `perf` (L4) | `--perftest` / `--residenttest` | PERF-OK / RESIDENT-PERF-OK |
| `m7` (L5) | `dotnet run --project tests/m7/PetDock.BongoCat.Tests -c Release` | M7-OK |
| `dist` (L6) | release → pack → zip sha256 → portable-selftest | RELEASE-OK / PACK-OK / PORTABLE-OK |

```powershell
node tools/verify.mjs --list                 # 只打印层表与每层的证明 / 不证明
node tools/verify.mjs --only window          # 只跑一层
node tools/verify.mjs --only static:depscan  # 只跑一层里的一步
node tools/verify.mjs --keep-going           # 一层红也继续跑后面的层
```

判绿看末行 `VERIFY-OK layers=n/7 steps=n evidence=artifacts/verify/<stamp>/`；失败是
`VERIFY-FAIL layer=<名> step=<名> exit=<n> marker=missing`，并打印该步 `.out` 的末 20 行。
证据目录里是每步的 `.out`/`.err`、`verify.json`，以及自动生成的 `README.md`（逐层「证明什么 /
不证明什么」+ 本轮没跑的层 + 全局不证明项）。

末行之后还会打印 `NOT RUN:` —— **默认三层不是全验**：窗口观感、穿透手感、两只以上排布、
托盘「移除」真点、多屏/混合 DPI 这些从来不是机器判据能证明的事，别把绿当成人眼验收。

`contract` 层把 `TEMP`/`TMP` 指进本次证据目录（沙箱拒绝在 `%TEMP%` 根下建目录），
`window` 层**故意不指**——`ui-audit-trail-not-in-repo` 会因临时目录进仓库而红。

## depscan.mjs — D-01a / D-01b / D-02a / D-02b / D-02c / D-02d

| 规则 | 检查内容 |
|---|---|
| D-01a | `import` / `import()` / `require` 静态解析目标：跨包相对路径裸引用（跨层、同层跨包、越出 `packages/`）即失败 |
| D-01b | `packages/` 顶层目录只允许 `core` / `sdk` / `plugins`，其余目录即失败 |
| D-02a | 各包 `package.json` 的 `dependencies` / `devDependencies` 出现上层包名即失败 |
| D-02b | import 图反向边 `core->sdk`、`core->plugins`、`sdk->plugins` 即失败；C# 侧为 `*.csproj` 的 `<ProjectReference>` 越出 `packages/` 或指向上层 |
| D-02d | 跨层直达边（跳过中间层，如 `plugins->core`、`sdk->core`）即失败；`packages/sdk` 不存在时 `plugins->core` 暂放行，该豁免随 `sdk` 目录出现自动失效 |
| D-02c | `packages/core/test/` 下的桩件引用 `plugins` 层即失败 |

唯一豁免：`core/test -> sdk` 是按 P-03 第 4 条认可的桩件注册路径（D-02c 只点名 plugins）。
缺失的层（如尚未创建的 `sdk` / `plugins`）按空层处理，不报错、不崩溃。
扫描范围：`packages/` 下全部三层（`core` / `sdk` / `plugins`，缺失层按空层处理）中的 `.js/.mjs/.cjs/.ts/.tsx/.jsx/.mts/.cts` 与 `**/*.csproj`；扫描前先输出一行 `STAT csproj-files N` 记录实际扫到的 csproj 数。

失败样例：

```
STAT csproj-files 2
VIOLATION D-01b packages/ top-level directory 'scratch' is not one of core/sdk/plugins
VIOLATION D-01a packages/core/src/net.ts bare cross-package relative import '../sdk/index.ts' -> packages/sdk/index.ts
VIOLATION D-02b packages/core/src/net.ts core -> sdk via relative import '../sdk/index.ts'
VIOLATION D-02d packages/plugins/x/P.csproj plugins -> core via ProjectReference '..\..\..\core\PetDock.Core\PetDock.Core.csproj'
DEPSCAN-FAIL
```

通过输出单行 `STAT csproj-files N` + `DEPSCAN-PASS`，exit 0；有违规时先输出 `STAT` 行与若干 `VIOLATION ...` 行，再输出 `DEPSCAN-FAIL`，exit 1。

## lint-core.mjs — D-03a / D-03b / D-03c / D-04a

| 规则 | 检查内容 |
|---|---|
| D-03a | `packages/core/**/*.cs` 行数合计（排除 `obj/`、`bin/`、`test/`）超过 `tools/core-loc-baseline.json` 的 `budget` 即失败。该文件是**唯一事实源**，人工维护：抬升预算 = 在 `history` 追加一条带逐切片归属表（`slice`/`files`/`lines`）的记录，不走 RFC（`docs/rfcs/RFC-11.md`）。门禁同时校验归属表行数求和 = 相邻两条 `measured` 之差、归属文件真实存在、末条 `budget` = 顶层 `budget`；归属表**内容的诚实性**机器判不了，登记为审查项 `D-08d` |
| D-03b | 概念黑名单（词表抄自 `docs/01-principles.md` P-02 与 `docs/02-glossary.md` §2），在 `packages/core` 下所有源码与配置文件中大小写不敏感逐行搜索，命中即失败并给行号 |
| D-03c | `packages/core` 下任何 `.cs` 源文件超过 `MAX_FILE_LINES = 400` 行即失败（含 `test/`） |
| D-04a | 源码中整词出现 `T<临时标记>`（大小写不敏感，词表见脚本 `TEMP_MARKERS`）即失败，扫描 `packages/` 全部层 |

范围细节：D-03b 扫描源码与配置文件扩展名（`.md` 不计，与 `docs/40:85` 对 TODO 的说明同理——
文档自己解释规则不算违规）；D-04a 只扫源码扩展名；`obj/`、`bin/`、`node_modules/` 一律跳过。
通过态先输出三行 `STAT`（`core-loc` 计数/预算 / `core-files` 计数 / `core-loc-trend` 自创世条的增减与上一条申报额）供 AM-03 趋势观测。

失败样例（本 README 自身受同一黑名单约束，故黑名单词与临时标记一律用 `<...>` 占位）：

```
STAT core-loc 9000/8450
STAT core-files 64
VIOLATION D-03a packages/core 9000 lines exceeds budget 8450 (declare the raise in tools/core-loc-baseline.json history, RFC-11)
VIOLATION D-03b packages/core/Host.cs:17 <blacklisted-term>
VIOLATION D-03c packages/core/Host.cs 452 lines exceeds MAX_FILE_LINES 400
VIOLATION D-04a packages/core/Host.cs:23 <temp-marker>
LINT-CORE-FAIL
```

通过输出 `LINT-CORE-PASS`，exit 0；有违规时先输出若干 `VIOLATION ...` 行，再输出 `LINT-CORE-FAIL`，exit 1。
包目录不存在（如 `packages/core` 尚未创建）按空层通过，不崩溃。

## doclink.mjs — D-04b

| 规则 | 检查内容 |
|---|---|
| D-04b 悬空引用 | `docs/**/*.md` 与根 `README.md` 中所有 `docs/NN-xxx.md`、`research/NN-xxx.md` 引用，目标不存在即失败 |
| D-04b ID 登记 | 同一扫描范围内的 `M-nn` / `AM-nn` / `N-nn` / `P-nn` / `D-nn` / `R-nn` / `SM-nn` 必须在该命名空间登记处存在，未登记即失败 |

豁免：被删除线 `~~...~~` 包住的引用与 ID 一律跳过（D-04b 规定的豁免写法）。
另有一条上下文豁免：同一行内已被删除线标记过的 ID，其未划线的重复出现视为历史反例
（如 `docs/99-review-report.md:40` 的 `AM-20/AM-21`），不再重复报违规。
ID 正则允许后缀字母（`D-01a` 命中基 ID `D-01`）。

失败样例：

```
VIOLATION D-04b docs/20-plugin-sdk-spec.md:88 dangling reference docs/99-nonexistent.md
VIOLATION D-04b docs/11-pet-runtime-abi.md:78 unregistered ID D-99
DOCLINK-FAIL
```

通过输出 `DOCLINK-PASS`，exit 0；有违规时先输出若干 `VIOLATION ...` 行，再输出 `DOCLINK-FAIL`，exit 1。

## asset-fingerprint.mjs — D-13a 素材指纹扫描

| 判据 | 检查内容 |
|---|---|
| 商业 SDK 扩展名 | `.moc3` / `.model3.json` / `.motion3.json` / `.skel` / `.atlas` / `.mtn` 命中即失败 |
| 生态角色包结构 | DyberPet（`info/info.json` + 兄弟 `pet`/`action`/`res` 目录）、BongoCat（`conf.json` + `assets`/`img`/`image`）、Shimeji（`conf/actions.xml`） |
| 已知哈希 | `tools/fingerprint-known.json` 里登记的 sha256（空表 = 尚未登记任何已知侵权文件） |

扫描范围默认 `git ls-files`，**不是磁盘**：`assets/user/`（所有者本机自用素材）已 gitignore、
永不发行，判它违规只会训练人忽略告警。`--all` 扫磁盘供人工排查；`--root DIR` 扫指定目录（负例自测）。

目录结构判据必须在**该目录内**判定（`dir + '/' + 清单路径` 精确相等），不能用全局 `endsWith`——
否则仓库任何一处存在该路径，就会把命中报给每一个目录（2026-10-19 实际踩过：自家合成夹具被判成
真实素材）。**零文件即失败**：git 不可用时它不能打 PASS。

```
FP-HIT pack/model.moc3  ← Live2D Cubism 模型（docs/52 §3：永不打包）
STAT fingerprint scanned=322 scope=git-tracked known-hashes=0 hits=1
FINGERPRINT-FAIL 真实素材/商业 SDK 二进制不得入库，见 docs/41 D-13a 与 docs/52 §5
```

通过输出 `STAT fingerprint ... hits=0` + `FINGERPRINT-PASS`，exit 0；有命中输出若干 `FP-HIT` 行
再 `FINGERPRINT-FAIL`，exit 1。

## release.mjs — M9 发布装配

把「能跑的仓库」变成「能发出去的目录」：`dotnet publish` 内核 → **按每个 manifest 的 `entry`
逐工程 publish 插件** → 图标 → LICENSE/NOTICE/README → 出口合规检查 → 写 `release-manifest.json`
（逐文件 sha256）。版本号唯一事实源 = `package.json` 的 `version`。

为什么插件要按 entry 发布：manifest 的 `entry` 形如
`PetDock.SamplePet/bin/Release/net8.0-windows/PetDock.SamplePet.dll`，整棵树复制（排除 `bin/`）
会让发布物里只有 `.cs` 源码，**装完一个插件都加载不了**，而出口检查只查 manifest 存在时照样报
`RELEASE-OK`（2026-10-19 实际踩过）。所以判据是「entry 指向的文件在不在」。

```
node tools/release.mjs              # 装配到 artifacts/release/<version>/
node tools/release.mjs --dry-run    # 仍然真的 publish 到临时目录并跑完出口检查，只是不碰最终目录
node tools/release.mjs --skip-publish
```

出口检查（扫**要发出去的那棵树**，不是仓库）：商业 SDK 扩展名、LICENSE/NOTICE、
`PetDock.Core.exe`、每个插件 manifest 的 `license.spdx`、`assets/icons/icon-32.png`。
失败打 `RELEASE-CHECK-FAIL ...` + `RELEASE-FAIL checks=N`，exit 1。

| 命名空间 | 登记处 |
|---|---|
| M-nn | `docs/00-vision.md` §6.2 |
| AM-nn | `docs/00-vision.md` §6.3 |
| N-nn | `docs/00-vision.md` §5 |
| P-nn | `docs/01-principles.md` |
| D-nn | `docs/40-development-paradigm.md` + `docs/41-testing-strategy.md` + `docs/43-rfc-and-adr.md` + `docs/42-contribution-guide.md` |
| SM-nn | `docs/10-architecture-overview.md` §10（唯一登记处） |
| R-nn | `docs/51-risk-register.md` |

注：`docs/02-glossary.md` §3 对 D-nn 的列举只列了 40/41/43；`D-20` 的权威定义实际在
`docs/42-contribution-guide.md`（启用条件，机器可执行），故 doclink 把 42 也作为 D-nn
登记处，避免把有完整定义的规则误报为未登记。该文档间不一致建议后续 RFC 修正。
