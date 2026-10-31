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
  - `node tools/verify.mjs --config release --only static,build,contract,window` → 9 + 1 + 4 + 1 步，末行 `VERIFY-OK layers=4/6 steps=15`（2026-10-31 实测；默认三层不含 window）
  - 静态层 2026-10-31 由八步变**九步**：新增 `i18n-reachability`（每条文案键要么被产品代码引用，要么带着理由在 `tools/i18n-keys-manifest.json` 里豁免；豁免不再需要本身就是失败）
  - `--uitest`：末行 `UI-OK skipped=0`，条数已到 289（判据体在 `test/SelfCheck/`）
  - **注意**：`--uitest` 经 pwsh **管道**（`| Select-Object …`）跑会得到 exit=1；stdio 重定向到文件时 exit=0（verify.mjs 用的是后者）。读末行 marker，别读管道的退出码
  - `--only dist` 九步里 `release-align` **预期为红**：`release-manifest.json` 在 zip 里且嵌着来源提交，任何提交都会让 zip 变 —— 只有 HEAD 正是已发布那一版时才绿。**不要为了消这个红点去发版**。
  - LOC：`node tools/lint-core.mjs` 末行 `STAT core-loc <实测>/<预算>`。2026-10-31 实测 **37708/37740 —— 只剩 32 行**；下次改内核 src 必须抬预算，并给 `tools/core-loc-baseline.json` 的 history 写一条归属精确到行的记录（各切片之和必须等于实测增量）。history[82] 是这一片的（+488 = 上一片漏申报的 101 + 本片 387）。
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
| （2026-10-31 下午，二） | **更新搬运的崩溃安全**：覆盖日志 + 启动判回滚 + 搬运工 `--rollback`（见 §2.3） |
| （2026-10-31 下午，一） | **可达性判据落地**（`tools/i18n-reachability.mjs` + `tools/i18n-keys-manifest.json` + 静态层第九步；i18n-lint 扫描面扩到随包页面插件）；**出网通道合一**（新增 `Host/GitHubHttp.cs`，两个出口改走它，仓库文件改走 contents API，市场索引补 id/version 形状校验与下载上限；新增 5 条 `ui-net-*` 判据）；详见 §2.2 |

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

### 2.2 自更新 / 市场下载在真实网络上是 100% 失败的（2026-10-31 查明，修好了一半）

一个从上线起就存在、**九道静态门与 282 条 UI 判据全绿**的致命缺陷：
`UpdateService` 与 `PluginMarket` 各自 new 了一个 `HttpClient` 并写着 `AllowAutoRedirect = false`
（理由是「只连一个写死的域名」）。而 GitHub 的 Release 资产**必然**先回一次 302 —— 本机实测：

```
GET .../releases/download/v0.4.1/PetDock-0.4.1-win-x64.zip        → 302 → release-assets.githubusercontent.com
GET .../releases/download/v0.4.1/PetDock-0.4.1-win-x64.zip.sha256 → 302 → release-assets.githubusercontent.com
```

非 200 即失败 → 用户点「下载新版本 / 从市场装插件」永远只会拿到 `download-status-302`。
门禁为什么看不见：离线判据只解析 JSON；打包自检自造 zip；**唯一碰真实网络的那一层（dist）默认不跑，
而它自己带着一个会跟随重定向的下载器**（`tools/verify-release.mjs`）——
产品代码从没被真实流量碰过。又一个「判据视角」的形状（§4.1）。

修法与判据：出网只留一条通道 `Host/GitHubHttp.cs` —— 主机写死、只走 https、
**重定向自己跟且每一跳先校验主机**、有体积上限、失败原因点名主机与种类（`…-loopback` / `…-timeout` / `…-unreachable-<异常>`）。
5 条 `ui-net-*` 判据（纯函数，不联网）把这条策略钉住。同一处顺手修掉两个同族缺陷：
索引里的 `id/version` 会被拼进下载文件名却只查了「非空」（`..\` 能写穿暂存区），以及两处下载都没有体积上限。

**仍未完**：所有者这台机器上 `release-assets.githubusercontent.com` **也被 hosts 指到本机**
（实测 20 秒无响应）。跟随重定向是「全世界都对」的那一半；本机要真能装，得把这两个域名从 hosts / 加速器里放出来，
或者做一个编译期写死的镜像前缀（§3.5 决策 5）。

### 2.3 更新搬运的崩溃安全：半覆盖之后必须能回来（2026-10-31 下午）

审计出来的第二条高危（比市场页面更该先做，因为它能把用户的程序搞没）：

- 搬运工是**逐文件覆盖**的，只对「捕获到的异常」回滚。断电 / 蓝屏 / 任务管理器结束留下的是
  「一半新、一半旧」的安装目录；
- 更糟的是下一次启动：旧逻辑把「已动过文件」与「只写了意图、一个文件都没动」混成一种，判 `Cancel`，
  而 `ClearPending(withFiles: true)` **连唯一的那份 `backup/` 一起删** ——
  用户既没有旧版本、也没有能跑的程序，界面还说「已取消」；
- 判据只断言「两个标记消失了」，从不造半覆盖现场，所以全绿。

现在的形状（三个概念各一个属主）：

| 概念 | 属主 | 判据 |
| --- | --- | --- |
| 覆盖日志 `_update/applying.txt`（动第一个文件**之前**写，装完/回滚完才删） | 搬运工写、宿主读（`UpdateStage.AppliedFile`） | `ui-update-clear-keeps-backup` + 真进程 `hs-11` 对账两边文件名 |
| 「没跑完」的两种分流 | `UpdateStartup.Decide`：日志不在 → `Cancel`；日志在 → `RollBack` | `ui-update-start-rollback-interrupted` |
| 回滚执行（用 `backup/` 换回旧版本、删掉新版本新增的文件、写 `result.txt`） | 搬运工 `--rollback`（同一个进程、同一条「等宿主退出」协议） | 真进程 `hs-8/9/10`（旧 core 被换回、新增文件被删、日志被清、没备份时说 `rolled-back 0`） |

另外两件事一起收了：`ClearPending` 在日志还在时**任何路径都不删 `backup/`**（那是唯一的退路）；
回滚结局在界面上与「装失败」分开说（`ui.update.rolledBack` / `ui.update.rollbackFailed`，两门语言），
因为「旧版本已经回来」与「装没装上」对用户的下一步完全不同。

**仍未证明**：真实的断电。判据造的是「日志在 + 盘上已是半新」的现场，不是真拔电 —— 这一条写进
`verify.mjs` dist 层的 notProven，不要当成已经证过。

## 3. 查明、但还没修的

### 3.1 插件商店根本找不到 —— 它**没有入口**

| 层 | 状态 |
| --- | --- |
| 文案 15 条 `cp.market.*` | 齐（`assets/i18n/zh-CN/controlpanel.json`） |
| 后端 | 齐（`PluginMarket.cs`、`UiHost.Market.cs`） |
| 判据 | 绿（`MarketChecks`） |
| **界面** | **不存在**：`packages/plugins` 下大小写不敏感搜 `market` 零命中；出厂 UI 只有 `PetsPage/PluginsPage/SettingsPage` 三页；15 条里只有 4 条**报错**文案被内核引用（`UiHost.Market.cs`），其余 11 条零引用 |

### 3.2 检查更新 / 商店拉索引 —— 已修（2026-10-31），只剩本机 hosts 这一半

**已修**：两个出口不再各写一份，走同一条通道 `Host/GitHubHttp.cs`，索引与更新源都改走
`api.github.com/repos/dhhxfggg2023/PetDock/contents/<path>?ref=main`（base64 解码；地址照样写死、只走 https）。
实测：contents API 90 ms 返回 200，解码出的 `update.json` 版本 0.4.1 正确。失败原因现在点名主机与种类。

**还没修**（本机事实，不是代码问题）：`release-assets.githubusercontent.com` 同样被 hosts 指到
127.0.0.1，本地加速器接了 TCP 不回话 → 20 秒无响应。所以在这台机器上「下载新版本」仍然会失败，
只是现在失败信息会说清是哪台主机、为什么不通用。**下一步是所有者侧的**：把这些域名从 hosts / 加速器里放出来，
或者按 §3.5 决策 5 加一个编译期写死的镜像前缀。

### 3.3 钩子（已移除）

`.githooks/pre-commit` 只为废弃的账本服务，2026-10-31 删除；`core.hooksPath` 仍指向
`.githooks` 但那里不再有钩子文件。原来的失败不是脚本错：`ledger-export.mjs` 找不到账本目录（源没了）。
**不要重新引入账本或它的钩子**（§0）。

### 3.4 缺口清单 —— 已交给判据自己维护（2026-10-31）

以前这张表是**散文**：一次性扫描的 18 条写在这里，然后它就开始漂。现在它有一个拥有者：
`node tools/i18n-reachability.mjs`（静态层第九步）+ `tools/i18n-keys-manifest.json`。
实测口径：409 条键、产品代码引用 393 条、**16 条零引用**、1 条动态拼键（`ui.bubble.poke`，已登记理由）。

16 条的三种性质（照抄判据的 STAT 就能复现）：

| 键 | manifest 里的 status | 性质 |
| --- | --- | --- |
| `cp.market.*`（11） | `pending-feature` | 市场界面缺失（§3.1）。**接上入口后必须从 manifest 删除**，否则判据报 `STALE-EXEMPT` |
| `cp.settings.update.downloaded/appliedNote/cancelledNote/handoffNote`（4） | `dead-copy` | 与 `ui.update.*` 是同一件事的第二份文案；运行期走的是 update-state 的 `ui.update.*` |
| `cp.pets.delete.failed`（1） | `diagnostic-gap` | 宿主只会回 `cp.pets.delete.missing`，真正写盘失败时用户看不到原因 |

那 3 条气泡键（`ui.bubble.poke1/2/3`）**不再是误报**：判据认得 `"ui.bubble.poke" + n` 这种拼接，
但要求这种动态拼键在 manifest 里带理由登记一次（`UNREGISTERED-DYNAMIC` 会拦住新的拼接）。

### 3.5 开放决策（都还没定）

1. `<IncludeSourceRevisionInInformationalVersion>`：SDK 默认把提交号塞进 `ProductVersion`，
   于是**任何提交都改变全部 39 个二进制**（`0.4.1+<40hex>`）。关掉能少一层噪音，但救不了 `release-align`（manifest 里同样嵌提交号）。
2. 不要为把 `release-align` 弄绿而发 0.4.2：它的二进制与 0.4.1 只差嵌进去的提交号。
3. 代码签名：微软自己的文档说「单纯为避开 SmartScreen 而买 EV 已不再划算」，且 Artifact Signing 的公共信任证书
   只发给美/加/欧/英的组织 —— 这里不符合资格。唯一能让用户**永远**看不到那个框的是上架 Microsoft Store。
   在此之前，正确做法是把说明写在用户会看的那一屏（发布正文开头 + 包内 INSTALL.md 的「怎么用」第 2 步）。
4. `%SystemDrive%` 字面目录为什么会出现（仓库根那个未展开的环境变量目录）—— 见 §5 第 12 条。
5. **镜像前缀**（2026-10-31 新增）：本机的 hosts/加速器把 `raw.githubusercontent.com` 与
   `release-assets.githubusercontent.com` 都指到 127.0.0.1，所以「下载新版本 / 从市场装插件」在本机必然失败。
   两条路：① 所有者把域名放出来（零代码）；② 在 `GitHubHttp` 里加一个**编译期写死的**镜像前缀
   （不能做成用户配置 —— 那等于把「只连一组写死的主机」这条纪律换掉，用户被骗一次就装了别人的东西）。
   决策前先问：这个镜像会不会看到我们的下载（隐私）、以及它挂了之后失败信息还能不能点名主机。

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

1. **可达性** —— ✅ 已落地（2026-10-31）：`tools/i18n-reachability.mjs` + 静态层第九步 +
   `tools/i18n-keys-manifest.json`。产品代码面 = 内核 src + 插件 + sdk 的 C#（**不含 tools/** —— 实测
   本仓自己的注释里写了一句 `"cp.market.title"`，扫 tools 会让一条死键被自己的文档判成「已引用」）。
   动态拼键（`"ui.bubble.poke" + n`）必须带理由登记；豁免过期本身判红。
2. **可逆性** —— 每个改变状态的命令必须有一张对应的「反命令」表，并断言反命令能从这个状态的**用户入口**走到。
3. **可诊断性** —— 每条用户可见的失败文案必须带「哪个操作、哪台主机/哪个文件、下一步做什么」，
   不允许一句孤零零的「失败」。

### 4.3 这一族判据的**第五个**症状（2026-10-31 新增）

§2.2 那个 100% 失败的下载，为什么九个门禁都没有一句话：唯一碰真实网络的是 dist 层，
而 dist 层**默认不跑**，且在这一层里失败**早已是预期状态**（`release-align` 那条红线人人都知道）。
一个「已知会红」的门禁，等于没有门禁 —— 它红着的时候没人读，红的原因也没人分。
教训：判据要么绿、要么被人读；把「预期为红」当成常态，就是把这一层从判据降级成装饰。

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
10. **`pwsh` 里的删除/收尾类操作也会挂**（删 `.dsh-code/` 挂了两次、每次都到程序超时）。
    这类一次性收尾改用 Node 直连：`import("node:fs")` 删目录、`import("node:child_process").execFileSync` 调 git —— 同一条命令反而稳。
11. `git push` 需要比 `git log` 更全的 env：少了 `USERDOMAIN`/`COMPUTERNAME`/`USERNAME`/`SystemDrive`/`ComSpec`/`PATHEXT` 时，
    GCM 会抛 `ArgumentException` 然后退成「could not read Username（terminal prompts disabled）」。补齐这些就过（本机凭据是 GCM 里存好的，不需要令牌）。
12. **hosts 劫持的不止 raw**：`release-assets.githubusercontent.com` 同样解析到 127.0.0.1，
    本地加速器**接了 TCP 不回话** → 20 秒无响应（不是连不上，是没回话）。
    所以「下载更新包 / 市场插件包」在这种机器上必然失败，且症状看起来像「网络不通」。
    查法：`Resolve-DnsName <host>` 看到 127.0.0.1 就是它。
13. **同一个 `run_code` 执行体可能会被跑两遍**（2026-10-31 实测：`core-loc-baseline.json` 的同一条 history 进了两次）。
    写 git 跟踪的文件时，执行体必须是**幂等**的（先查再写），别写「追加」。
14. `--uitest` 走 pwsh **管道**（`… | Select-Object -Last 5`）会得到 exit=1，即使末行是 `UI-OK`；
    stdio 重定向到文件时 exit=0（`verify.mjs` 用的是后者）。读末行 marker，别拿管道的 `$LASTEXITCODE` 当结论。

## 6. 下一步（按顺序）

1. ~~收起取不出来~~ —— 已完成（`27c4f00`）。
2. ~~可达性判据落地~~ —— 已完成（静态层第九步；见 §3.4）。
3. ~~两个出口的主机~~ —— **代码已改**（`GitHubHttp` + contents API）；本机还剩 hosts 那一半（§3.2 / §3.5 决策 5）。
4. **插件市场界面**（后端与文案都在，缺的就是入口，§3.1）。市场**并入插件页**，不新开导航页：
   `market-plugins/README.md` 第 38 行写的用户路径就是「插件页 →『浏览插件市场…』→ 列表 → 安装」，
   而且 15 条文案一条都不用新增。接上之后把那 11 条 `pending-feature` 从 manifest 删掉。
5. ~~更新搬运的崩溃安全~~ —— 已完成（§2.3；真进程判据在 `tools/update-helper-selftest.mjs`）。
6. **tier-2 插件是无沙箱裸进程**（同上审计，未修）：`PluginHost.Tier2.cs` 用
   `Path.Combine(pluginDir, manifest.Entry)` 解析入口，Entry 为绝对路径时前缀被吞掉，
   `Tier2Sandbox` 直接 `Process.Start`；包含性检查只在**安装**时做过一次
   （`PluginInstaller`），装载 / 热重载 / 按 id 启用三条路都不复查。tier-0 界面插件同理（`UiPluginCatalog` 接受绝对路径 entry）。
   修法方向：把「入口必须在插件目录内」做成一个解析函数，装载的三条路都走它。
7. §3.5 的五项开放决策各定一次（第 5 项是本机能不能更新/装插件的前提）。

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

本文件的更新时点：2026-10-31 下午，`HEAD` 见 `git log -1`。
`node tools/verify.mjs --config release --only static,build,contract,window` → `VERIFY-OK layers=4/6 steps=15`
（static 9/9、build、contract 4/4、window 289 条 `UI-OK skipped=0`）。
