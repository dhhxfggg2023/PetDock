# tools/ — 仓库门禁、构建与素材脚本

零 npm 依赖，只用 Node 内置模块；ESM（`.mjs`），要求 Node >= 20（见根 `package.json` 的 `engines`）。

## 总入口

```powershell
npm run lint                 # 八道静态门，任一红即停
node tools/verify.mjs        # 验证门：默认 static + build + contract 三层
node tools/verify.mjs --all  # 六层全跑（会建窗口、跑性能、打包）
node tools/verify.mjs --list # 打印层表与每层「证明什么 / 不证明什么」
```

单门直跑：`node tools/depscan.mjs`、`node tools/lint-core.mjs`、`node tools/validate-manifest.mjs` 等；
每道门自己打印 `XXX-PASS` / `XXX-FAIL` 并以退出码表达结论。

## 静态门（npm run lint）

| 脚本 | 管什么 |
|---|---|
| `depscan.mjs` | 包依赖方向：`packages/` 只允许 core / sdk / plugins，禁止反向边与跨层直达 |
| `version-parity.mjs` | 版本号三处一致：`package.json` / `PetDock.Core.csproj` / 发布清单，任一处漂移即红 |
| `lint-core.mjs` | D-03a 内核行数预算（`core-loc-baseline.json` 是唯一事实源）、D-03b 概念黑名单、D-03c 单文件 400 行、D-04a 临时标记 |
| `validate-manifest.mjs` | 插件清单校验。**唯一权威是内核的 ManifestValidator**：脚本只负责把 `--validate-plugins` 跑起来，不再维护第二份 JSON Schema |
| `asset-manifest.mjs` | 素材清单与「零第三方素材入库」 |
| `asset-fingerprint.mjs` | 商业 SDK 扩展名、角色包目录结构、已知哈希（默认扫 `git ls-files`） |
| `i18n-lint.mjs` | `src/Ui` 与 `src/Host` 的代码字符串里不许留用户可见中文 |
| `i18n-parity.mjs` | `assets/i18n/{zh-CN,en-US}` 键集与占位符形状一致 |

## 验证门 verify.mjs

| 层 | 内容 | marker |
|---|---|---|
| `static` | 上面八道静态门逐条跑 | 各门 PASS |
| `build` | `dotnet build PetDock.sln -c <Config>` | 退出码 |
| `contract` | `--hosttest` / `--drivetest` / `--eventtest` / `--animtest` | HOST-OK / DRIVE-OK / EVENT-OK / ANIM-OK |
| `window` | `--uitest`（截图与面板判据） | UI-OK |
| `perf` | `--perftest` / `--residenttest` | PERF-OK / RESIDENT-PERF-OK |
| `dist` | release → pack → portable-selftest → market-index → zip 摘要 → repro（重跑比对）→ release-align | RELEASE-OK / PACK-OK / PORTABLE-OK / MARKET-OK / REPRO-OK / RELEASE-VERIFY-OK |

`--only <层>[:<步>]` 单跑，`--config release` 换被验的产物，`--keep-going` 跨层继续。
每步的 `.out`/`.err`、`verify.json` 与自动生成的 `README.md` 落在 `artifacts/verify/<stamp>/`。
默认三层不等于全验：窗口观感、穿透手感、多屏/混合 DPI 仍然要人眼验收。

## 发布

```powershell
node tools/release.mjs              # 装配到 artifacts/release/<version>/
node tools/release.mjs --dry-run    # 跑完检查但不碰最终目录
node tools/pack.mjs                 # 打成解压即用的 portable zip
node tools/portable-selftest.mjs    # 解压后直接跑内核，验证发布物自洽
```

插件按各自的 `manifest.entry` 逐工程 publish；出口检查看的是**要发出去的那棵树**。

## 其它

- `petdock-cli/`：`petdock` 命令行（初始化/校验/打包等子命令）。
- `gen-sample-pet.mjs`、`make-icons.py`：程序化素材生成。
- `bench/`、`probes/`：性能浸泡与一次性探针，不属于门禁。
