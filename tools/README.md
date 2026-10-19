# tools/ — 仓库机器门禁（docs/40 D-01 ~ D-04b）

三条 Node 脚本把 `docs/40-development-paradigm.md` 的机器可执行规则落成可执行命令。
零 npm 依赖（只用 Node 内置模块），ESM（`.mjs`），要求 Node >= 20（见根 `package.json` 的 `engines`）。

## 总入口

```powershell
npm run lint        # 依次跑 depscan -> lint:core -> lint:docs，任一红即停
npm run depscan     # 只跑依赖方向门
npm run lint:core   # 只跑内核规模 / 概念门
npm run lint:docs   # 只跑文档引用门
```

单文件直跑：`node tools/depscan.mjs`、`node tools/lint-core.mjs`、`node tools/doclink.mjs`。
pwsh 不支持 `&&`，串联命令用 `;`。

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

## ID 命名空间登记处（grep 确认，2026-10-16）

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
