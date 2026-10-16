# tests/fixtures/abi-contract — D-11 契约基线夹具（t4）

## 什么在这

| 文件 | 角色 |
|---|---|
| `abi-baseline.json` | 「最小旧版调用序列」基线：六角色全部公开句柄（反射渲染格式，两侧同一函数）+ 枚举名与序号 + docs/14 §2 能力句柄白名单 + 旧插件调用序列。**Append-only**：删除/改名/收窄任一条目 = D-11b 违例，须走 docs/43 RFC。 |
| `min-old-plugin/petdock-plugin.json` | 「最小旧版插件清单」：api 1.0、kind=brain、tier=1，全部 9 必填键。id 过 docs/21 §1 pattern（无连字符——ledger pit 2026-10-16）。 |
| `min-old-plugin/main.js` | 旧清单 entry 指向的真实文件（形态同 my-first-brain，装载路径不是空引用）。 |
| `negative/broken-manifest/` | 诚实性负向夹具：一份每种字段都坏的清单，校验器**必须**拒它并报到具体字段；拒不掉 = 校验判据失效的报警线，被 `OldPluginLoadTests.NegativeFixtureIsRejectedNotEmpty` 钉死。 |
| `../PetDock.AbiContract.Tests/` | 判定工程：`AbiVerifier.CheckAll` 对着 **PetDock.Core.dll 反射**逐条核验（不读注释——M1 PetColor 教训），`AbiBaselineTests` 正向 + 5 条负向自证（删句柄/改签名/重排序号/幽灵类型/能力收窄，每种都必须被抓住），`OldPluginLoadTests` 用今天的 PluginHost/PetSurfaceRoster 真装载旧清单。 |

D-11a「每次宿主改动后 CI 用基线夹具做一次旧插件加载模拟」= 本工程的三个 xunit 文件进 `PetDock.sln` 后由 `dotnet test PetDock.sln` 每次执行；任一基线句柄缺失或签名变更 → `WholeBaselineResolvesAgainstCoreDll` 红。

## D-10a 覆盖下限的可复算测定（core 与 sdk：行 ≥80%，分支 ≥70%）

本机与 CI 同一条命令（docs/41:58；CI 无沙箱，testhost 正常；**本机 DSH 沙箱会让 testhost 必 Win32 5——账本 pit 2026-10-16，故本机留证走 `dotnet <工程>\bin\Release\net8.0-windows\PetDock.AbiContract.Tests.exe` 自检入口（11 条 ABI-CHECK 行 + ABI-OK/ABI-FAIL n=），覆盖数字由 CI/无沙箱终端产出**）：

```powershell
# 行覆盖（阈值 80 直接变成退出码，coverlet.msbuild 已在工程里引用）
dotnet test PetDock.sln -c Release /p:CollectCoverage=true `
  /p:Include="[PetDock.Core]*,[PetDock.Sdk]*" `
  /p:Threshold=80 /p:ThresholdType=line /p:ThresholdStat=min

# 分支覆盖（阈值 70）
dotnet test PetDock.sln -c Release /p:CollectCoverage=true `
  /p:Include="[PetDock.Core]*,[PetDock.Sdk]*" `
  /p:Threshold=70 /p:ThresholdType=branch /p:ThresholdStat=min
```

- 判定方式：coverlet 的 `Threshold` 不达标时 MSBuild 任务失败 → `dotnet test` 退出码非 0。这就是「低于即失败」的落点，不需要人工读表。
- 分母是 `[PetDock.Core]*,[PetDock.Sdk]*` 两个程序集（D-10a 逐字「packages/core 与 packages/sdk」）；cobertura 落在各工程 `TestResults/`。
- 诚实性说明（未验证项）：**这两条命令的数值结果本会话未亲跑取得**——本机沙箱 testhost 限制如上，跑不到断言执行。命令语义（Threshold→退出码）来自 coverlet.msbuild 6.0.2 的公开行为。首个可信数字由 t5 无沙箱终端或 CI 产出；M2 现状是 core 侧覆盖主力在 t9 工程，sdk 侧尚无测试引用 `[PetDock.Sdk]`，80% 是否已过要以实测为准，不许口头宣布。
- D-10b 纯逻辑隔离：本工程的临时插件根建在**输出目录内**（不是 %TEMP%，本机子进程写 %TEMP% 被拒——实测 Win32 5），不触真实窗口/GPU；仓库写入为零（夹具源树只读，Dispose 清 staging）。

## 更新规则

新增 ABI 面 → 同步 `abi-baseline.json`（只许加条目）。要动已发布句柄 → docs/43 RFC + docs/43 D-32a 废弃窗口，先于代码合并。
