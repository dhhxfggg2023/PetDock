# PetDock 交接文档

> **这是唯一的交接件。** 新会话/接手的人先读这一份，读完就能接着干。
> 旧机制（`.dsh-code/` 账本、`docs/` 交接树、`ledger-export` 与它的 pre-commit 钩子）
> 自 2026-10-31 起**全部废弃**，见 §0 与 §7。不要在仓库外或历史目录里找它们。
>
> 转公开前请处理 §5 与 §7（里面是本机事实）。

## 0. 这份文档怎么用

| 事 | 规定 |
| --- | --- |
| 交接 | **只有这一个文件**。不建账本、不建 docs 树、不建交接目录 |
| 位置 | 仓库根 `HANDOVER.md`，**入库**（跟代码一起走） |
| 为什么入库 | 2026-10-31 发现：未入库的记忆会消失 —— 旧的活账本 + `docs/` 树都不见了，只剩一份 10-28 的跨卷快照。git 是这台机器上唯一被证明能活下来的机制 |
| 谁来更新 | 每一次「发布 / 改判断口径 / 决定架构」之后更新对应小节；不要另起文件 |
| 看到旧东西怎么办 | 机器上若有 `%USERPROFILE%\petdock-ledger-backup\` 或 `.dsh-code/`，那是历史快照，**不要恢复、不要往里写**（§7） |
| 代码注释里的 `原规格 NN §M` / `RFC-NN §M` / `docs/NN` | 是**历史理由**，那些文档不在仓库里（§7），别去找 |

## 1. 现在在哪（都可核对）

- 仓库 `E:\projects\PetDock`；`HEAD` 见 `git log -1`，与 `origin/main` 同步，工作树干净。
- 门禁（跑法与口径见 `tools/verify.mjs` 头部注释）：
  - `node tools/verify.mjs --config release --only static,build,contract` → 8 + 1 + 4 步，末行 `VERIFY-OK layers=3/6 steps=13`
  - `dotnet exec packages/core/PetDock.Core/bin/Release/net8.0-windows/PetDock.Core.dll --uitest --log <路径>` → 末行 `UI-OK skipped=0`
  - `--only dist` 九步里 `release-align` **预期为红**：`release-manifest.json` 在 zip 里且嵌着来源提交，任何提交都会让 zip 变 —— 只有 HEAD 正是已发布那一版时才绿。**不要为了消这个红点去发版**。
  - LOC：`node tools/lint-core.mjs` 末行 `STAT core-loc <实测>/<预算>`。2026-10-31 实测 36926/36950 —— **只剩 24 行**；下次改内核 src 必须抬预算，并给 `tools/core-loc-baseline.json` 的 history 写一条归属精确到行的记录（各切片之和必须等于实测增量）。
  - D-03c：单文件 ≤400 行。D-03b：`packages/core/**` 逐行不许出现黑名单子串（`shell/action/sprite/presenter/balloon/ghost/pet.json/行为树` 等，大小写不敏感）。
  - 自检里 `--selftest` 在本机因自身控制台窗口挡住探针而红（`SELF-FAIL hit opaque=miss`），它不在任何门禁层里。
- 已发布（都还在）：

| tag | 发布 | zip 下载数 | 包 sha256（前 12） |
| --- | --- | --- | --- |
| v0.1.0 | 2026-10-30 10:01 | 5 | — |
| v0.1.1 | 2026-10-30 14:44 | 3 | — |
| v0.2.0 | 2026-10-31 01:27 | 1 | — |
| v0.3.0 | 2026-10-31 01:44 | 4 | 8824d61bfb6d（6566914 B） |
| v0.4.0 | 2026-10-31 02:30 | 2 | 7739f3470383（6673486 B） |
| v0.4.1 | 2026-10-31 02:45 | 6 | 470c4c0a1d75（6672527 B） |

发布物形态：解压即用、`PetDock.Core.exe` + 更新用的 `PetDock.Update.exe` 两个 exe、
顶层目录固定叫 `PetDock`（zip 名带版本）、数据在程序旁 `data\`、删目录即卸载、不写注册表。

## 2. 已经做完的（每个提交都先过门禁）

| 提交 | 内容 |
| --- | --- |
| （多次） | 0.3.0–0.4.1 六次发布；`release-align` 那条「同一提交才可比」的不变量 |
| 812ec22 | 发布正文重构：`## 这次改了什么` 占主位、`## 怎么升级` 跟版本各写各的、指纹折叠到最后；判据 5 条 → 7 条（`ix-release-4b` 必须有改动清单、`ix-release-4c` 必须有 `release-notes/<版本>.md`）；三个已发布 Release 正文回填 |
| 2b11e8e | SmartScreen：`InternetMark`（`File.Copy` 会把 `Zone.Identifier` 一起带走 → 搬运工副本自己会弹框，而那时宿主已经退出、没有窗口能提示）+ 判据 `ui-update-helper-copy-unblocked`（正反各验一次） |
| 3529199 | 修正说明文案：Windows 11 的框上直接就有「仍要运行」，没有「更多信息」 |
| 27c4f00 | **收起之后取不出来**（见下） |

### 2.1 收起取不出来：一个概念两个属主

用户报「宠物收起无法正常取出」。根因不在收起，在**面板快照**：

- `BuildExtraPetLines` 明确 `continue` 跳过主窗那只，注释写「由 UiRoster 出」；
- `UiRoster.Build` 只在 `pet` 不为空时出行，而 `Snapshot()` 传的是 `_primaryRemoved ? null : _pet`。

两个函数都以为对方会出这一行 → 收起后 `rows=[]`：列表整个空了，也就没有「在桌面上显示」可点。
（那一行里的 `primaryOnDesktop: !_primaryRemoved` 早说明本意是要出行的：出行时传 null，这个参数就是死参数。）
2026-10-30 修同一症状时，口径只落到了非主窗那只身上。

连带第二处：`IsPrimary` 带着 `!_primaryRemoved &&`，收起的行因此**失去身份** ——
删除会掉进「非主窗那只」那一支（去找 `PetWindowSet` 槽位，主窗那只从来不在里面），用户得到「找不到这只宠物」。
现在 `IsPrimary` 只答身份，需要可见性的调用方各自判。

第三处是**修出来的洞**：新增 `_primaryDeleted`。收起与删除都需要 `_primaryRemoved`，
但只有删除该让那一行消失 —— 判据当场抓到「删除之后清单已空而那一行还在（换成了实例 id 的幽灵行）」。

判据 `packages/core/PetDock.Core/test/SelfCheck/PetsDesktopChecks.cs`，三条**每条都红过一次**才绿的：

| 判据 | 修前 | 修后 |
| --- | --- | --- |
| `ui-pets-desktop-stays-listed` | `FAIL rows=[(none)]` | `PASS rows=[user.src-pack:hidden]` |
| `ui-pets-desktop-roundtrip` | 宿主侧本来就对（钉住） | `PASS ... visible=True` |
| `ui-pets-desktop-delete-while-hidden` | `FAIL delete=找不到这只宠物。` | `PASS left=0 rows=[(none)]` |

判据写法（下次照抄）：`PETDOCK_DATA_ROOT` 指临时目录 → `new PetDataStore(root)` + `StagePack` + `RegisterActive` →
`PetDriveTest.MakeInstance(out _, out _)` → `new UiHost(window, pet, null, null, null, null)` → `host.TickOnce()` → 断言 `Snapshot()`。

## 3. 查明、但还没修的

### 3.1 插件商店根本找不到 —— 它**没有入口**

| 层 | 状态 |
| --- | --- |
| 文案 15 条 `cp.market.*` | 齐（`assets/i18n/zh-CN/controlpanel.json`） |
| 后端 | 齐（`PluginMarket.cs`、`UiHost.Market.cs`） |
| 判据 | 绿（`MarketChecks`） |
| **界面** | **不存在**：`packages/plugins` 下大小写不敏感搜 `market` 零命中；出厂 UI 只有 `PetsPage/PluginsPage/SettingsPage` 三页；15 条里只有 4 条**报错**文案被内核引用（`UiHost.Market.cs`），其余 11 条零引用 |

### 3.2 检查更新失败 / 商店拉不到索引 —— 同一个根因

两个出口都写死在 `raw.githubusercontent.com`（`UpdateService.cs:29`、`PluginMarket.cs:43`）。
在所有者这台机器上：该域名解析到 127.0.0.1（hosts 里 53 条把 GitHub 系域名指到本机），
本地加速器接了 TCP 但不回话，于是 30 秒超时 →「没查到（网络不通或更新源不可用）」。
而 `api.github.com` / `github.com` **是通的**（所以 zip 下载成功、元数据失败）。

修法：两个出口改走 `api.github.com/repos/<owner>/<repo>/contents/<path>`（base64 解码；同一台服务、
地址照样写死、只走 https），或给一个可覆盖的镜像；同时把失败文案改成**点名主机**，别只说「网络不通」。

### 3.3 钩子（已移除）

`.githooks/pre-commit` 只为废弃的账本服务，2026-10-31 删除；`core.hooksPath` 仍指向
`.githooks` 但那里不再有钩子文件。原来的失败不是脚本错：`ledger-export.mjs` 找不到账本目录（源没了）。
**不要重新引入账本或它的钩子**（§0）。

### 3.4 缺口清单（可达性扫描，算法见 §4.2）

401 条文案里产品代码零引用的 18 条：

| 键 | 性质 |
| --- | --- |
| `cp.market.title/browse/col.*/install/installed/loading/empty/update`（11） | 整个市场页面缺失 |
| `cp.settings.update.downloaded/appliedNote/cancelledNote/handoffNote`（4） | 与 `ui.update.*` 是同一件事的两份文案 → 死文案 |
| `cp.pets.delete.failed`（1） | 「删除失败」没有地方显示 |
| `ui.bubble.poke1/2/3`（3） | **误报**：产品代码拼键 `"ui.bubble.poke" + n` —— 判据必须认得动态拼接 |

### 3.5 开放决策（都还没定）

1. `<IncludeSourceRevisionInInformationalVersion>`：SDK 默认把提交号塞进 `ProductVersion`，
   于是**任何提交都改变全部 39 个二进制**（`0.4.1+<40hex>`）。关掉能少一层噪音，但救不了 `release-align`（manifest 里同样嵌提交号）。
2. 不要为把 `release-align` 弄绿而发 0.4.2：它的二进制与 0.4.1 只差嵌进去的提交号。
3. 代码签名：微软自己的文档说「单纯为避开 SmartScreen 而买 EV 已不再划算」，且 Artifact Signing 的公共信任证书
   只发给美/加/欧/英的组织 —— 这里不符合资格。唯一能让用户**永远**看不到那个框的是上架 Microsoft Store。
   在此之前，正确做法是把说明写在用户会看的那一屏（发布正文开头 + 包内 INSTALL.md 的「怎么用」第 2 步）。

## 4. 那个「一直想避免、一直没抓到」的东西

### 4.1 判据的**视角**问题

八道静态门 + 282 条 UI 判据，覆盖的全是**内部自洽**：版本号三处一致、两门语言键与占位符一致、
清单/素材/指纹一致、sha256 与提交号同源、LOC 预算、依赖方向。
**没有一条在问「用户能不能走到 / 走出 / 看懂这一步」** —— 于是下面四个症状全都发生在门禁全绿的情况下：

| 症状 | 本该管它的判据族 | 为什么现有的门没话说 |
| --- | --- | --- |
| 商店找不到 | 可达性 | `i18n-parity` 只查「键在不在两种语言里」，两边都在 |
| 检查更新失败 | 可诊断性 | 文案诚实但没点名主机，用户查不到 hosts |
| 收起取不出来 | 可逆性 | 没有门要求「每个能进入的状态都有可发现的出口」 |
| 发布页不说改了什么 / 说明书教旧口径 | 代理性（用户此刻看到什么） | 直到这次才补上 `ix-release-4b/4c` 与 release 的 ⑨b |

### 4.2 一个更具体的子形态：**一个概念，两个属主**

这一轮三个症状是同一个形状 —— 同一件事在两处实现，只改了一处（或一处以为另一处会做）：

- 装行：`UiRoster.Build` 与 `BuildExtraPetLines` 互相以为对方出主窗那一行；
- 更新状态文案：`cp.settings.update.*` 与 `ui.update.*` 两份；
- 一个 `bool` 两个含义：`_primaryRemoved` 同时表示「收起」与「删除」。

可机械化的三族新判据（**都还没落地**）：

1. **可达性** —— 每个 i18n 键必须被产品代码引用；数据驱动的键（如拼键的气泡）必须在显式清单里登记并写明理由。
   算法已跑通：读 `assets/i18n/zh-CN/*.json` 的键，扫 `packages`+`tools`+`assets` 的产品代码，
   区分「产品引用 / 只有判据引用 / 谁都不引用」。落成 `tools/i18n-reachability.mjs` + 静态层一步。
2. **可逆性** —— 每个改变状态的命令必须有一张对应的「反命令」表，并断言反命令能从这个状态的**用户入口**走到。
3. **可诊断性** —— 每条用户可见的失败文案必须带「哪个操作、哪台主机/哪个文件、下一步做什么」，
   不允许一句孤零零的「失败」。

## 5. 本机陷阱（按「会白花一小时」排序；转公开前删掉本节）

1. `run_code` 子进程的 `process.env` 是**空的**：`pwsh` 命令里要自己铺
   `PATH`/`SystemRoot`/`TEMP`/`USERPROFILE`/`APPDATA`…，否则 `dotnet build` 会失败。
2. 少 `git` 在 PATH → `--only static` 的 `fingerprint` 报「扫描到 0 个文件」（**假红**）；
   少 `dotnet` 在 PATH → `manifest` 报 `spawnSync dotnet ENOENT`（**假红**）。
3. **项目目录之外的写入会卡死到超时**（连删一个 ADS 都一样）。读没问题；要改外面的文件就把命令交给所有者。
4. PowerShell 5.1 读 `.ps1` 按 ANSI：含中文的脚本必须 **UTF-8 BOM + CRLF**，
   否则 here-string 解析失败或整段乱码；`-EncodedCommand` 那条路挂过两次，别用。
   JS 里生成脚本用 `String.raw`（模板串不能含反引号），要反引号就先用占位符再 `split/join` 替换。
5. 取 GitHub 令牌（绝不落盘）：`$env:GCM_INTERACTIVE='never'; $env:GIT_TERMINAL_PROMPT='0'`，
   再把 `@('protocol=https','host=github.com','') -join [char]10` 喂给 `git credential fill`，
   取 `password=` 那一行（40 字符 `gho_`）。工具路径：git `E:\tools\AzurLaneAutoScript\toolkit\Git\bin\git.exe`。
6. `git checkout --force <tag>` **会摧毁未提交改动** —— 毁过一次（三个文件）。切标签前先提交。
7. Node 24 直连 `raw.githubusercontent.com` 报 `UNABLE_TO_VERIFY_LEAF_SIGNATURE`（本机 MITM）；
   `tools/verify-release.mjs` 里有 `trustSystemRoots()` 可抄。
8. PATCH 已发布 Release 的正文时，**sha256/提交号必须用该 Release 自己那份资产的值**，
   不能拿本地重建的指纹；GitHub 读接口在 PATCH 后会短暂返回旧值。
9. PS 5.1 里 `$req.Headers.Add('User-Agent', …)` 会抛 `ArgumentException`，用 `$req.UserAgent = …`。

## 6. 下一步（按顺序）

1. ~~收起取不出来~~ —— 已完成（`27c4f00`）。
2. **可达性判据落地**：`tools/i18n-reachability.mjs` + 静态层一步 + 显式清单
   （动态拼键登记；市场那 11 条登记成「功能待建」，让「缺一个页面」变成仓库里看得见的一行）。
3. **两个出口的主机**改成 `api.github.com` contents API，并把失败文案点名主机（§3.2）。
4. **插件市场页面**（后端与文案都在，缺的就是那一页，§3.1）。
5. §3.5 四个开放决策各定一次。

## 7. 已废弃的东西与失效引用（不要复活它们）

| 东西 | 状态 |
| --- | --- |
| `.dsh-code/`、`docs/` 树、`docs/handoff/`、`ledger/` | **废弃**（2026-10-31）。机器上只有一份 2026-10-28 的历史快照 `%USERPROFILE%\petdock-ledger-backup\`，**不要恢复它来重建这套机制** |
| `tools/ledger-export.mjs` | 随账本停用（钩子已删）。留着只为读懂历史 |
| `.githooks/pre-commit` | 已删除（它只为账本服务） |
| `.dsh-code/HANDOFF-2026-10-31-releases-and-ux-audit.md` | 已被本文件取代（内容并入这里） |
| 代码注释里的 `原规格 NN §M` / `RFC-NN §M` / `docs/NN §M` | 那些文档**不在仓库里**（随 `docs/` 树一起没了）。它们只是当年的**设计理由**，不是可查的现行规格；判读到它们时按注释本身的意思理解，不要去找原文 |
| `tools/probes/`、`assets/user/`、`assets/sample/local/` | 刻意不入库（`.gitignore`）；不是死代码，别因为「未跟踪」就删 |

---

本文件的更新时点：2026-10-31，`HEAD` 见 `git log -1`。static 8/8、contract 4/4、uitest 282 全过。
