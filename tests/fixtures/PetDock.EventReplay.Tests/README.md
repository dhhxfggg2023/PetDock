# PetDock.EventReplay.Tests — M4 事件重放测试（t3）

本工程是 docs/50:51「事件重放测试」与「ABI 契约测试」里**重放**那一半的物证。
契约权威：`docs/handoff/M4-contract-frozen.md` §8（夹具格式 / 确定性 / 五条负例 / 谁不做什么）。

## 怎么跑（本机唯一可行通道）

```
dotnet build PetDock.sln -c Release --no-incremental
dotnet exec tests/fixtures/PetDock.EventReplay.Tests/bin/Release/net8.0-windows/PetDock.EventReplay.Tests.dll
```

**不要用 `dotnet test` / `dotnet vstest`**：本机 DSH 沙箱让 testhost 在
`SetParentProcessExitCallback → OpenProcess` 上必 Win32 5，整轮中止、一条断言都跑不到
（与 `PetDock.AbiContract.Tests` 同一条教训）。本工程照抄它的自写 `Main` + `[Fact]` 反射形态。

### 开关

| 开关 | 作用 |
|---|---|
| （无） | 跑全部 `[Fact]`；`SKIP` 如实打印但**不计入**失败数 |
| `--strict` | `SKIP` 计入失败数 —— t1 §2.6「SKIP 不是 PASS」的机器形态。**给 t5 用** |
| `--list` | 列出全部判据名 |
| `--coverage` | 只打覆盖对账（13 事件 × 2 格 + 5 模式 + 13×5 交叉 + core 面诊断），不跑测试 |

输出形态：`EVENT-CHECK <class>.<method> PASS|FAIL <msg>` / `SKIP <reason>`，
末行 `EVENT-OK` 或 `EVENT-FAIL n=<k>`（退出码 = 失败数；`--strict` 下含 SKIP）。

## 夹具纪律（docs/41 D-13a 的延续）

`.replay.jsonl` 是**构造器产物，不入库**：`FixtureConstructor.cs` 在运行时造出 19 个夹具，
写进 `%TEMP%\petdock-eventreplay-<guid>\`，进程退出时删除。仓库里不得有任何 `.replay.jsonl`
（`tests/fixtures/.gitignore` 已加通配，`GeneratedFixturesNeverLandInTheRepository` 判据在运行时守它）。

## 夹具格式（t1 §8.2）

一行一帧，UTF-8 无 BOM。**一处测试侧扩展**（契约样例没有它，故在此写明）：每帧多一个
`brain.directives` 块 = 本次 tick 由脚本化 brain 交出的**指令原序**（输入，不是期望）。
期望仍是 `expected` 三块。由此 `expected.directives` 是**仲裁后**的序，与输入的乱序**刻意不同** ——
否则那条判据就是同义反复。

```json
{"seq":0,"dtMs":33,"tod":600,"world":{"cursor":[12,40],"windowRect":[0,0,256,256],"focusedApp":"notepad","occluded":false},
 "events":[{"type":"core.clock.tick","payload":{"dt":33}}],
 "brain":{"directives":[{"kind":"requestCapability","capability":"input.mousePosition","reason":"need cursor"},
                       {"kind":"setAnimation","clip":"idle"}]},
 "session":null,
 "expected":{"directives":[{"kind":"setAnimation","clip":"idle"},
                           {"kind":"requestCapability","capability":"input.mousePosition","reason":"need cursor"}],
             "events":[{"type":"core.clock.tick","payload":{"dt":33}}],
             "refusals":[]}}
```

六条规则（t1 §8.2 的五条 + 夹具侧墙钟禁令），每条都有判据与反向自证：

| # | 规则 | 判据 | 红的样子 |
|---|---|---|---|
| R1 | `seq` 从 0 连续递增 | `FixtureSequencesAreContiguous` | `fixture-seq-gap at={n}` |
| R2 | `dtMs`/`tod`/`world` 全部来自夹具，禁读墙钟 | `FixturesCarryNoWallClockFields` + `DriverDoesNotReadWallClock` | 出现 `wall`/`now`/`ms` 非 null 键、或驱动里出现 `DateTime.Now` |
| R3 | `expected.directives` 按 kind 降序 | `ExpectedDirectivesAreInArbitrationOrder` | `fixture-directive-order` |
| R4 | `expected.events` 按投递序（FIFO） | `DeliveryOrderMatchesCoreLogFifo` + `ExpectedEventsHaveNoPhantoms` | 期望序与 core 日志序分叉、或出现输入里没有的事件 |
| R5 | 事件 `type` ∈ §2.1 的 13 个（负例夹具除外） | `FixturesStayInsideTheCatalog` | `fixture-unknown-type` |
| R6 | 夹具正文无本机时刻键 | 同 R2 | `fixture-wall-clock` |

## 确定性（t1 §8.3）

同一夹具连跑两次，**规范行** sha256 相等。两件事刻意分开实现，合并就会变成同义反复：

1. **抹除**（`Canonicalizer.StrippedFieldNames`，被抹掉的字段名逐个列出）：
   `elapsed=`（本机耗时）、`run=`（重放序号，确定性相异来源）、`wall-`、`hwnd=`、`ticks=`、`0x…` 句柄。
2. **相异**：抹除前的**原始**行必须真的不同（`elapsed=` 会抖、`run=` 必变）——
   `VolatileFieldsAreReallyStripped` 断言 raw 不同而 canonical 相同。
   **没有这一步，「两次相等」可能只是「两次都没记录任何会变的东西」。**

反向自证：`ReplayOutputIsNotEmpty`（`lines >= 8`，防「两次都空」的假绿）；
`StrippedFieldListIsExplicitAndBounded`（抹除清单不许含 `clip=`/`position=`/`verdict=`/`count=`）。

## 五条负例（t1 §8.4）

| # | 负例 | 判据 | 期望 |
|---|---|---|---|
| N1 | 表外事件与合法事件同批 | `N1_OutsideCatalogIsRejectedButLegalEventSurvives` | 表外不入 Log、`OutsideCatalog+=1`、合法事件仍在、不崩 |
| N2 | bail 监听者抛异常 → 阻止 suspend | `SuspendIsConsultedThroughBailFirst` | 状态仍 running（fail-closed） |
| N3 | 注入时钟推 201ms | `N3_RequestCapabilityTimesOutAt201ms` | `refused reason=timeout latency=201`、审计一行 |
| N4 | `Brain.Tick` 抛异常 | `N4_BrainFaultVoidsTheWholeTick` | 本 tick 指令全部作废 + `BrainFaults++` + degraded |
| N5 | **同一夹具两次故意不同（tod 差 1）** | `N5_MetaCriterionDeterminismJudgeCanFail` | `Replay.Deterministic` 必须 **FAIL** —— **元判据** |

N5 特别重要：没有它，`Deterministic` 那一行可能是「什么都没比较」的假绿。

## 两层结构（诚实性：SKIP 不是 PASS）

t3 **只消费 core 公开面**（t1 §8.5），全程**反射晚绑定**（`CoreSurface.cs`）—— 不是替身，
调的是 core 的真实现，只是晚绑定。这样做的三条理由写在 `CoreSurface.cs` 文件头：
t2 的签名在落地期会变（本会话实测 EventBus 的返回元组几分钟内换过），编译期引用会让每次中间态
把 tests/ 构建打断；而 `SKIP` 必须是一个**可判定的事实**，不是编译错误。

- **层 1（今天真跑真红）**：`HostArbiter.Sorted`（15 个无序对的全序 + 同 kind 稳定性）、
  `HostEventDispatch.Log`（FIFO 投递序、审计面、`OutsideCatalog`）、
  `PetInstance.TickBrain/NextDeltaMs/EnforceSessionBudget`、夹具六规则、确定性、N5。
- **层 2（需要 t2 的面）**：五种 dispatch、目录守门、`CapabilityBroker`、`WorldProbe`。
  判据已全部写好；面不存在时 **SKIP + 具名原因**（写清哪一节、欠谁）—— `SKIP` 不是 `PASS`。

`--strict` 下每一处 SKIP 都变成必须回答的问题：t5 请带 `--strict` 跑，让「未落地」不可静默。

## 覆盖对账（验收④）

`--coverage` 逐格打出 13 事件 × 2 格 + 5 模式 + 13×5 交叉格 + core 面诊断。
`CoverageLedgerIsFullyAttributed` 判据断言每一格都有状态与**具名**原因，
且不许出现「待定 / 稍后 / TBD」这类没有内容的词。

## 新增判据的义务

core 的面一旦落地，**层 2 的对应判据必须从 SKIP 改成真跑**（`Assert.Fail("注入面已出现：…")`
就是给那一刻准备的哨兵）。这是 t3 的补测义务，不是可选项。
