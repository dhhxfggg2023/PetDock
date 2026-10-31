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
  - 静态层 2026-10-31 由八步变**九步**：新增 `i18n-reachability`（每条文案键要么被产品代码引用，要么带着理由在 `tools/i18n-keys-manifest.json` 里豁免；豁免不再需要本身就是失败）。**当天下午 `exempt` 已清空**：409 条键全部有产品出口，判据现在跑在 `exempt=0` 上
  - `--uitest`：末行 `UI-OK skipped=0`，条数已到 295（判据体在 `test/SelfCheck/`）
  - **注意**：`--uitest` 经 pwsh **管道**（`| Select-Object …`）跑会得到 exit=1；stdio 重定向到文件时 exit=0（verify.mjs 用的是后者）。读末行 marker，别读管道的退出码
  - `--only dist` 九步里 `release-align` **预期为红**：`release-manifest.json` 在 zip 里且嵌着来源提交，任何提交都会让 zip 变 —— 只有 HEAD 正是已发布那一版时才绿。**不要为了消这个红点去发版**。
  - LOC：`node tools/lint-core.mjs` 末行 `STAT core-loc <实测>/<预算>`。2026-10-31 傍晚实测 **38236/38300 —— 余量 64**（history[91] +101「没开回来的出口」、[92] +34「意图读不出来」、[93] +78「安装残留不变成插件」、[94] +7「失败时只收解包树」）；下次改内核 src 必须抬预算，并给 `tools/core-loc-baseline.json` 的 history 写一条归属精确到行的记录（各切片之和必须等于实测增量）。history[87] 记的 +203 是 tier-2 进程边界；history[85] +3、history[86] +3 分别是「删除宠物如实报部分失败」与「远程数字字段不抛」。插件市场界面 170 行 + XAML 90 行在 `packages/plugins` 下，**不计入本预算**（口径只算 `packages/core`）。
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
| v0.4.2 | 2026-10-31 16:40 | — | 2e2b9b4a0d56（6693963 B） |

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
| （2026-10-31 傍晚，九） | **覆盖安装失败的残留不许变成一只插件**：`.bak-` 命名与识别各一个属主、发现逻辑跳过、搬不回去的旧目录进回收站（见 §2.10） |
| （2026-10-31 傍晚，八） | **「上次的更新意图读不出来」不再等于「没有意图」**：三种状态显式分开、判成一次取消、文案单独一句（见 §2.9） |
| （2026-10-31 傍晚，十） | **装好但没开起来时，旧版本的备份不许一起删**：只收 `unpacked/`，`backup/` 等到确认新版本起来了才收；真进程判据 `hs-12/13/14`（见 §2.8） |
| （2026-10-31 傍晚，七） | **「装好了但没能自己开回来」有了出口**：搬运工留一行原因，宿主并进那一次结局（见 §2.8）；`UpdateResult` 从 `UpdateStage.cs` 拆出 |
| （2026-10-31 下午，六） | **tier-2 进程边界**：作业对象（宿主死→客体死、单进程、内存上限），判据 `tier2-orphan-killed-by-job`（见 §2.7） |
| （2026-10-31 下午，五） | **网络来的数字字段不许把宿主抛穿**：更新源与市场索引的 `schema` 改 `TryGetInt32`（见 §2.6） |
| （2026-10-31 下午，四） | **插件市场接进插件页**（见 §2.5）；4 条死文案删除、`cp.pets.delete.failed` 接上；豁免表清零 |
| （2026-10-31 下午，三） | **插件入口只有一个属主**：`PluginEntryPath` + 四个装载点与安装器全走它（见 §2.4） |
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

### 2.4 插件入口只有一个属主（2026-10-31 下午，安全）

审计的「严重-2 / 高」：清单里的 `entry` 是**远端可控字符串**，会被拼成路径去 `Assembly.LoadFrom`
与 `Process.Start`。两件事同时成立才让它变成漏洞：

1. 安装器（`PluginInstaller`）只在**安装那一刻**查过一次「入口必须在插件目录内」，
   而装载、热重载、按 id 启用三条路都不复查；
2. `Path.Combine(pluginDir, entry)` 遇到绝对路径的第二段会**直接返回它** ——
   于是「入口在目录内」这个前提被悄悄换掉：能写插件目录的人（发行 zip 解出来的目录用户可写）
   可以让宿主启动任意 exe（tier-2，无沙箱裸进程），或把任意 dll 载进宿主进程（tier-0）。

现在只有一个解析者 `Host/PluginEntryPath.cs`：空 / 绝对路径 / 跳出目录 / 不存在，四类拒绝；
四个装载点（tier-2、tier-1 读入口模块、tier-0 界面插件、tier-0 宠物表面）与安装器全部走它。
判据：`ui-plugin-entry-contained`（四类拒绝的行为）与 `ui-plugin-entry-single-owner`
（源码级：内核 src 里不许再出现把 `manifest.Entry` 拼成路径的行 —— 挡的是将来长出来的第五个装载点）。

**仍未修**（审计里同一条的下一半）：tier-2 依然是一个**无沙箱的裸进程**（没有 Job Object、没有能力判定，
`Tier2Sandbox` 里的 `Gate` 只记账不执行）。入口收敛堵住了「指向任意 exe」，但没给 tier-2 建边界 ——
一个写得很糟的 tier-2 插件照样能用完整用户权限干任何事。这条需要单独一片（列在 §6）。

### 2.5 插件市场接进插件页（2026-10-31）

§3.1 的洞是「后端齐、文案齐、判据绿，就是没有入口」。现在入口在插件页顶部：
**插件页 →「浏览插件市场…」→ 列表 → 安装**（`market-plugins/README.md` 写的用户路径原样落地），
与本地列表是同一块区域的两种态，不弹新窗口、不新开导航页。

- 界面全在 `packages/plugins/petdock.ui.default`：`PluginsPage.xaml`（第二态）、
  `PluginsPage.Market.cs`（拉取 / 安装 / 失败文案）、`RowViewModels.cs` 的 `MarketRow`。
- 页面**不下载、不解压、不校验**：`InstallFromMarketAsync` 走宿主同一条安装管线；
  装完只改那一行的动作位（安装 →「已装 x.y.z」，不可点），**不重新联网**。
- 失败分两种说法：宿主给的 `cp.market.*` 直接翻；`index-not-json` / `bad-download-url` / `entry-bad-*`
  这类机器码塞进新增的 `cp.market.indexFailed`。把机器码原样丢给用户等于没说，把索引坏了说成「离线」是撒谎。
- 联网纪律没变：**只有点这一下才联网** —— 构造、刷新、切回都不碰网络（与更新检查同一条）。
- 顺带清掉的残留：`cp.settings.update.downloaded/appliedNote/cancelledNote/handoffNote`（4 条与 `ui.update.*`
  重复的死文案）删除；`cp.pets.delete.failed` 接上 —— 清单删掉了、素材删不掉时不再说「已删除」，
  而是返回「已从列表删除，但素材文件没能删掉（可能被占用）」，页面**先刷新再报**（否则会留下一行不存在的宠物）。
- 结果：`tools/i18n-keys-manifest.json` 的 `exempt` **清空**（409 条键全部有产品出口；`dynamic=1` 仍是气泡拼键）。
  机制留着，等下一次「功能没接」时再登记。

诚实的边界：**没有自动化判据证明这页画得对**。窗口层的 295 条用的是假页面（壳本身），
不会实例化插件页，所以这一片能被证明的只有「编译过 + 每条文案都有出处」。要看真样子只能跑起来点一下。

### 2.6 「返回原因码」的函数不许抛（2026-10-31）

`UpdateFeed.TryParse` 与 `PluginMarketIndex.TryParse` 都长这样：
「结构不对就返回 `why` 短码」，调用方据此翻成人话。但两者都用 `GetInt32()` 读 `schema` ——
这个 API 在 `1.5` 这种**不是整数**的 JSON 数字上抛 `FormatException`，于是「返回原因码」变成了「抛异常」：

- 更新页：异常从 `CheckUpdateAsync` 穿出去，落在 async void 的 UI 路径上；
- 市场页：拉索引时抛出去，被页面当成「没拉到」显示成「离线」—— 理由说错了，用户会去查网络。

两处都改成 `TryGetInt32`（对 1.5 返回 `false`），各自加一条负例：
`feed-schema` / `index-schema` 而不是异常。同类里 `ManifestValidator.Fields.ReadInt` 早就是这么写的
（清单的 `api.major` / `tier` 走校验器，`Build` 只在全绿后跑），所以清单侧没有这个洞。

一般化的规矩：**接受外部字节的函数只有两种出口 —— 返回值或契约里写明的异常**；
「解析失败」永远走返回值。写这条不是因为 1.5 特别，而是因为下一个远程字段还会这么写。

### 2.7 tier-2 的进程边界（2026-10-31）

审计的最后一条是「tier-2 至今是无 OS 限制的裸进程」。这一片**不假装**能做成沙箱：
拦网络、拦文件、拦注册表是另一个量级的事，而且前提是「tier-2 允许做什么」得先有个决定。
这里只把三条已经承诺过的事变成内核里的事实（`Sandbox/Tier2Job.cs`）：

| 保证 | 做法 | 为什么必须补 |
| --- | --- | --- |
| 宿主死 → 客体死 | `KILL_ON_JOB_CLOSE` | 宿主终止客体的所有路径都以「宿主还活着」为前提；被任务管理器结束、被更新搬运工接管时清理代码一行都不跑，客体变孤儿（还握着 IPC 管道） |
| 客体只许一个进程 | `ACTIVE_PROCESS = 1` | 防止它悄悄繁殖出一堆没人看得见的进程 |
| 内存上限 1.5 GiB | `PROCESS_MEMORY` | 跑飞的分配由分配失败体现，而不是把整机拖进交换 |

接线在 `Sandbox/Tier2Sandbox.Job.cs`（partial，主文件已在 400 行边上）。作业建不起来时**照常启动**但落一条审计行
`job-unbound` —— 「以为有边界」比「没有边界」更危险。

判据 `tier2-orphan-killed-by-job`（hosttest 层）：正常启动一个真客体 → 关掉作业句柄（等价于宿主进程死掉、
内核回收句柄）→ 客体必须在 5 秒内从进程表消失。实测 `up=True pid=26468 job=True closed=True gone=True`。

**这是一次行为收紧**：tier-2 客体从此不能再启动子进程（`ACTIVE_PROCESS = 1`）。
要放开就改 `Tier2Job.Create` 的那一行标志位 —— 但那时「只许一个进程」这条保证也就没了，得一起改文档。
**仍然不是沙箱**：它照样能读任意文件、连任意网络。界面与文档都不许把它说成「隔离」。

### 2.8 「装好了，但没能自己开回来」要有出口（2026-10-31 傍晚）

审计里「失败说得不够清楚」这一组的第一条，也是唯一一条真正的**盲区**：
搬运工把新版本装好之后 `Relaunch` 失败，只往 `helper.log` 写一行就去睡了。
`result.txt` 仍然写 `ok`（这是对的 —— 它回答的是「装没装上」），于是下一次启动界面上只有
「已更新到 0.4.2」：用户问的却是「刚才桌宠为什么消失了」，而这个问题没有任何地方回答。
他只能自己再点一次程序，那时连提示都没有。

现在的形状是两份文件、两个问题：

| 文件 | 回答的问题 | 谁写 / 谁读 |
| --- | --- | --- |
| `_update/result.txt` | 装上了吗 | 搬运工写，宿主读一次就删 |
| `_update/relaunch-failed.txt` | 装好之后它自己回来了吗（没回来时为什么） | 搬运工 `WriteRelaunchReport` 写，宿主 `ReadRelaunchReport` 读一次就删 |

宿主把那一行并进**那一次**结局：`UpdateResult.AppliedNotRestarted`（`ok-not-restarted`），
界面说 `ui.update.appliedNoRelaunch`（两种语言）。`UpdateResult` 因此有了两个问题：
`Ok` = 装成功了吗，`Restarted` = 那个新进程是自己起来的吗 —— 拆开之后「已更新」不再顺带暗示「它回来过」。

同一处顺手修掉的两件事（都在这条路径上，都是「只有在出事时才看得见」的那一类）：

- `Relaunch` 失败时提前 `return`，`unpacked/` 与 `backup/` 都没收 —— 一次更新在用户数据区留几百兆，
  下一次更新再解一份。**切法要分清楚**：`unpacked/` 一定收（解出来的那份永远没用了，zip 还在），
  `backup/` 只在「确认新版本真的起来了」之后才收 —— 没起来时它是用户手里唯一那份旧版本，
  而「新版本根本起不来」时宿主永远不会来清暂存区。第一版修法把两样一起收了，真进程判据 `hs-14` 当场拦住。
- `Applier.Rollback` 在 `Relaunch` 已经记下真实异常名之后，又补一句笼统的 `rollback-relaunch-failed`，
  把有用的那一行挤到上面。多余的这一句删掉。

判据两处：

- `--uitest`：`ui-update-relaunch-reported`（有那行 → `ok=True restarted=False`，读完即删）、
  `ui-update-relaunch-absent-is-normal`（没那行 → 仍是普普通通的「已更新」，不许把正常结局说成异常）；
- dist 层真进程 `tools/update-helper-selftest.mjs`（夹具里的「新版本」是一份文本文件，`Relaunch` 必然失败，
  正好造出这条现场）：`hs-12` 原因行必须落盘、`hs-13` `unpacked/` 已收、`hs-14` `backup/` 还在。
  实测 `report="Win32Exception" unpacked=false backup-kept=true`，14 条全过（`HELPER-OK`）。

### 2.9 「上一次的更新意图读不出来」不再等于「没有意图」（2026-10-31 傍晚）

`restart.txt` 回答的是「本进程退出去是要装东西」。它其实有三种状态，而早先只有两种：

- 不在 → 没有意图；
- 在且读得出 → 上一趟没跑完（取消或回滚）；
- 在、但读不出来（被占用、权限异常、磁盘错误）→ **早先也被读成「没有意图」**。

第三种最危险：启动判定于是走 `HandOff` —— 在可能已经有一个搬运工正等着本进程退出的情况下，
**再起第二个搬运工**，两个进程去覆盖同一批文件。日志里有一行 `update-request-unreadable`，
用户侧一个字都没有。

现在 `ReadRequest(dataRoot, out bool unreadable)` 把三种状态显式交给调用方，
`UpdateStartPlan.IntentUnreadable` 记下「这不是一次普通的取消」：

- **处置**与「上一趟没跑完」一致：这次不装。覆盖日志还在时仍然判回滚 —— 盘上是不是新旧混杂，
  只看日志，与那份意图写的什么无关；
- **说法**不同：`ui.update.intentUnreadable`（两种语言）。普通取消可以说「上次更新（x.y.z）没有装完」，
  这一句在这里不能说：那份文件写的哪个版本我们并不知道，拿待装版本去冒充它，等于替用户编一句话。

判据 `ui-update-start-intent-unreadable`：独占打开 `restart.txt` 造出「文件在、读不出来」的现场，
断言 `Cancel` + `IntentUnreadable` + `detail=request-unreadable`。

### 2.10 覆盖安装失败的残留不许变成一只插件（2026-10-31 傍晚）

审计里「安装/删除失败可能把数据根留一半」那一条，具体形态在这里：

- `PluginInstaller.TryCommit` 覆盖安装时先把旧目录搬成 `<id>.bak-<时间戳>`，再把新的搬进来；
  搬不回去时，这份备份就留在**插件根下**；
- 发现逻辑（`PluginHost.Discover`）只认「目录里有清单」，于是它被当成**同 id 的第二只插件**；
  同 id 去重按目录名排序 —— 真身留下、备份永远躺在那里：用户看不到它、删不掉它，
  也永远不会再被更新到。

三件事各一个属主：

| 事 | 属主 |
| --- | --- |
| 备份叫什么名字 | `PluginDirBackup.PathFor`（唯一，安装器只调它） |
| 怎么认出这是备份 | `PluginDirBackup.IsLeftover`（发现逻辑只问它） |
| 搬不回去时它去哪 | `PluginInventory.Park`（回收站；删除入口与安装器共用这一份实现，跨卷退回复制+删源） |

判据 `plugin-install-backup-not-a-plugin`：插件根下放一只 `<id>` 与一份 `<id>.bak-2026-10-31-120000`，
断言 `Discover` 看不见备份、`IsLeftover` 对备份为 true、对真身为 false。

**仍然的情形**：跨卷与权限同时出问题时 `Park` 也会失败，那时只留一行 `plugin-install-park-failed`，
备份仍在原地 —— 但至少不会变成一只幽灵插件。

## 3. 查明、但还没修的

### 3.1 插件商店根本找不到 —— 已修（2026-10-31）

原来的洞：文案 15 条 `cp.market.*` 齐、后端 `PluginMarket.cs` / `UiHost.Market.cs` 齐、判据 `MarketChecks` 绿，
**界面不存在**（15 条里只有 4 条报错文案被内核引用，其余 11 条零引用）。
现在入口在插件页顶部「浏览插件市场…」，接法见 §2.5；那 11 条 `pending-feature` 豁免随界面一起从 manifest 删除。


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
实测口径（2026-10-31 下午）：409 条键、**409 条全部被产品代码引用**、**0 条豁免**、1 条动态拼键（`ui.bubble.poke`，已登记理由）。

曾经登记过的 16 条现在全部有了归宿（现在的判据一句 `exempt=0` 就能复现）：

| 键 | 原 status | 归宿 |
| --- | --- | --- |
| `cp.market.*`（11） | `pending-feature` | 插件市场界面接上（§2.5） |
| `cp.settings.update.downloaded/appliedNote/cancelledNote/handoffNote`（4） | `dead-copy` | 删除（与 `ui.update.*` 重复） |
| `cp.pets.delete.failed`（1） | `diagnostic-gap` | 宿主在「素材没删掉」时如实返回（§2.5） |


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
15. **本地重跑 dist 层会改写 `update.json`**：`gen-update-feed` 把**刚才那一次**构建的 zip 摘要写进去，
    而那个 zip 不是在发 Release 的那个（commit/时间不同 ⇒ 摘要必然不同）。只跑门禁时请 `git checkout -- update.json`，
    否则推上去等于让所有客户端去下载一个「声明指纹与资产不符」的包。发布顺序见 §5 第 12 条与 §6 第 9 条。

## 6. 下一步（按顺序）

1. ~~收起取不出来~~ —— 已完成（`27c4f00`）。
2. ~~可达性判据落地~~ —— 已完成（静态层第九步；见 §3.4）。
3. ~~两个出口的主机~~ —— **代码已改**（`GitHubHttp` + contents API）；本机还剩 hosts 那一半（§3.2 / §3.5 决策 5）。
4. ~~插件市场界面~~ —— 已完成（§2.5）：入口进插件页，11 条 `pending-feature` 豁免已删，豁免表清零。
   **诚实的边界**：没有自动化判据证明这页画得对（窗口层的 295 条用的是假页面），要看外观只能跑起来点一下。
5. ~~更新搬运的崩溃安全~~ —— 已完成（§2.3；真进程判据在 `tools/update-helper-selftest.mjs`）。
6. ~~tier-2 插件入口包含性~~ —— 入口已收敛（§2.4）；~~进程边界~~ —— 作业对象已接（§2.7）。
   **剩下的**是真正的**沙箱**：网络/文件/注册表仍然不拦，`Gate` 对能力仍是「记账 + 拒绝未授予」而不是 OS 强制。
   那要单独一片，而且前提是「tier-2 允许做什么」有个决定 —— 没有那个决定，加限制只会把功能关掉。
7. §3.5 的五项开放决策各定一次（第 5 项是本机能不能更新/装插件的前提）。
8. **审计剩下的「失败说得不够清楚」**（都不致命，但都违反本仓的纪律 —— 失败要么说清、要么根本不算完成）：
   - ~~`UpdateStartup` 读 `restart.txt` 抛异常时被当成「没有更新意图」~~ —— **已有痕迹**（2026-10-31）：
     `UpdateStage.ReadRequest` / `ReadResult` 的 catch 各留一条 `update-request-unreadable` /
     `update-result-unreadable`。返回语义没动（调用方不需要改），先让静音变成有痕迹；
     ~~还差界面出口~~ —— **已补**（2026-10-31 傍晚，§2.9）：判成一次取消（`IntentUnreadable`）并给一句自己的话；
   - ~~`Applier` 清理阶段的 `TryDelete` 失败只进 log~~ —— **已留痕**（2026-10-31）：私有 delegate 的静态兜底 sink，日志行 `cleanup-failed <文件名> <异常类型>`。`RunRollBack` 也接了同一个 sink（2026-10-31 补），回滚路径的清理失败同样有痕；
   - ~~搬运工「装好了但没把程序开回来」只写 helper.log~~ —— **已修**（2026-10-31 傍晚，§2.8）：
     `_update/relaunch-failed.txt` + `ui.update.appliedNoRelaunch`，判据 `ui-update-relaunch-reported`；
   - ~~内置插件的安装/删除失败可能把数据根留一半~~ —— **已收口**（2026-10-31 傍晚，§2.10）：
     覆盖安装的备份不再被当成一只插件，搬不回去的旧目录进回收站。
9. **发布**：7 个本地提交在 `main` 上，等所有者一句「推送 / 发版」。发版按 `release-notes/<版本>.md`
   + 指纹 + 七条正文判据走（见 `tools/verify-release.mjs` 头部注释）。

## 7. 已废弃的东西与失效引用（不要复活它们）

| 东西 | 状态 |
| --- | --- |
| `.dsh-code/`、`docs/` 树、`docs/handoff/`、`ledger/` | **已删除**（2026-10-31）。仓库里的 `ledger/` 目录（本机未入库的账本）与本机历史快照 `%USERPROFILE%\petdock-ledger-backup\` 都不再是工作输入，**不要恢复它们来重建这套机制** |
| `tools/ledger-export.mjs` | **已删除**（2026-10-31）：它是那套账本的唯一读取者，随账本一起下线；历史在 git 与快照里 |
| `.githooks/pre-commit` | 已删除（它只为账本服务） |
| `.dsh-code/HANDOFF-2026-10-31-releases-and-ux-audit.md` | 已被本文件取代（内容并入这里） |
| 代码注释里的 `原规格 NN §M` / `RFC-NN §M` / `docs/NN §M` | 那些文档**不在仓库里**（随 `docs/` 树一起没了）。它们只是当年的**设计理由**，不是可查的现行规格；判读到它们时按注释本身的意思理解，不要去找原文 |
| `tools/probes/`、`assets/user/`、`assets/sample/local/` | 刻意不入库（`.gitignore`）；不是死代码，别因为「未跟踪」就删 |

---

本文件的更新时点：2026-10-31 傍晚（收口于第 13 轮），`HEAD` 见 `git log -1`。
远端 `main` 停在 `d430014`（v0.4.2 已发布、两个资产已回读核对）；它之后有 **4 个待推提交**（§2.8 与它的修正 / §2.9 / §2.10）。
⑦ 只剩「获得所有者同意后发布」，其余 ①–⑥ 已完成并有判据。
仍未做（见 §6 第 8 条）：内置插件安装/删除失败可能把数据根留一半。
`node tools/verify.mjs --config release --only static,build,contract,window` → `VERIFY-OK layers=4/6 steps=15`
（static 9/9、build、contract 4/4、window 295 条 `UI-OK skipped=0`；2026-10-31 傍晚实测）。
dist 层的两步单独跑过：`node tools/release.mjs` → `RELEASE-OK`（108 文件 / 17.3 MB，`dirty=false`），
`node tools/update-helper-selftest.mjs` → `HELPER-OK`（14 条）。
