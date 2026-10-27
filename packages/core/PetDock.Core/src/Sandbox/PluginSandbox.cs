// M1 契约 §4.6 / §5（交接档案）：Tier-1 沙箱基座主类型。
// 冻结映射（契约 §4.6）：
//   - 每插件独立 Engine + Store（本类型每实例各持一份）；
//   - 内存预算 → Store.SetLimits；每片 CPU → Fuel；墙钟 → EpochDeadline（阈值已设，纪元未推进）；
//   - WASI 用 preview1 子集（Store.SetWasiConfiguration + Linker.DefineWasi），
//     WasiConfiguration 默认全关（默认拒绝），不声明 WASI 1.0；
//   - 热重载 = 换 Store（SwapStoreForReload），引擎/模块保留；
//   - 任何越限：受控中断（trap，不崩溃）+ BudgetExceeded 事件 + 撤销一项能力；
//   - P-05：本类型任何公共方法都不向外抛异常 —— 一切落成事件。
using Wasmtime;

namespace PetDock.Core.Sandbox;

/// <summary>
/// 单个 Tier-1 客体的沙箱。宿主（t4）每加载一个插件构造一个实例。
/// 生命周期：new → Load → 任意次 Tick → （SwapStoreForReload | RevokeOneForViolation）→ Dispose。
/// 线程模型：单线程 —— 宿主须在同一线程调用本实例的全部方法（与 Store 约束一致）。
/// </summary>
public sealed class PluginSandbox : IDisposable
{
    /// <summary>宿主可调用的客体导出函数名（每帧入口）。</summary>
    public const string TickExportName = "tick";

    private readonly ISandboxEventSink _sink;
    private readonly CapabilityGate _gate;

    private SandboxBudgets _budgets;
    private Engine? _engine;
    private Module? _module;
    private Store? _store;
    private Instance? _instance;
    private Function? _tick;
    private bool _disposed;

    /// <summary>构造期不接触引擎 —— 引擎/存储在 Load 才建（发现/校验失败不该留下半个引擎）。</summary>
    public PluginSandbox(CapabilityGate gate, ISandboxEventSink sink, SandboxBudgets? budgets = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _budgets = budgets ?? SandboxBudgets.Default;
    }

    /// <summary>当前预算（宿主标定燃料后回写）。</summary>
    public SandboxBudgets Budgets
    {
        get => _budgets;
        set => _budgets = value;
    }

    /// <summary>能力闸门（宿主回调用它做运行期判定）。</summary>
    public CapabilityGate Gate => _gate;

    /// <summary>是否已装载客体（Load 成功且未 Dispose）。</summary>
    public bool IsLoaded => !_disposed && _instance is not null;

    /// <summary>
    /// 校验并装载模块。失败只发事件，不抛（P-05）。返回是否成功。
    /// </summary>
    public bool Load(ReadOnlySpan<byte> moduleBytes)
    {
        if (_disposed) return false;
        try
        {
            var config = new Config()
                .WithEpochInterruption(true)
                .WithFuelConsumption(true);
            _engine = new Engine(config);

            var invalid = Module.Validate(_engine, moduleBytes);
            if (invalid is not null)
            {
                Emit(SandboxEventKind.GuestFault, ("stage", "validate"), ("err", invalid));
                return false;
            }

            _module = Module.FromBytes(_engine, TickExportName, moduleBytes);
            return BuildStore();
        }
        catch (Exception ex)
        {
            Emit(SandboxEventKind.GuestFault, ("stage", "load"), ("err", ex.Message));
            return false;
        }
    }

    /// <summary>
    /// 热重载 = 换 Store（契约 §4.6）：引擎与模块保留，存储/实例/入口全部重建。
    /// 失败时旧实例已弃、新实例未立 —— 发事件并返回 false，由宿主决定是否回退。
    /// </summary>
    public bool SwapStoreForReload()
    {
        if (_disposed || _engine is null || _module is null) return false;
        try
        {
            DisposeStore();
            return BuildStore();
        }
        catch (Exception ex)
        {
            Emit(SandboxEventKind.GuestFault, ("stage", "reload"), ("err", ex.Message));
            return false;
        }
    }

    /// <summary>
    /// 执行一次客体入口调用。任何 trap/异常都被吞掉并落成事件（P-05：绝不穿透进宿主循环）。
    /// 越限（内存增长失败、燃料耗尽、纪元到期）→ BudgetExceeded + 撤销一项能力。
    /// </summary>
    public void Tick()
    {
        if (_disposed || _tick is null || _store is null) return;

        // 每片重置：燃料与纪元截止（契约 §4.6「epoch_deadline 在每次客体 tick 前重置」）。
        try
        {
            if (_budgets.FuelPerSlice > 0) _store.Fuel = _budgets.FuelPerSlice;
            _store.SetEpochDeadline(_budgets.EpochTicksPerSlice);
        }
        catch (Exception ex)
        {
            Emit(SandboxEventKind.GuestFault, ("stage", "tick-reset"), ("err", ex.Message));
            return;
        }

        try
        {
            _tick.Invoke();
        }
        catch (TrapException ex)
        {
            // trap = 受控中断（越限或客体自身 trap 都走这里）。不区分细节、统一按越限处置：
            // 撤销一项能力 + 拒绝下一次同类请求由闸门承载（denied 事件在下次 Check 时发）。
            var revoked = _gate.RevokeOne(null);
            Emit(SandboxEventKind.BudgetExceeded,
                ("trap", ex.Message), ("revoked", revoked ?? "<none>"));
        }
        catch (WasmtimeException ex)
        {
            Emit(SandboxEventKind.GuestFault, ("stage", "tick"), ("err", ex.Message));
        }
        catch (Exception ex)
        {
            // 宿主回调内的托管异常已被 wasmtime-dotnet 转成 trap（上游保证）；
            // 走到这里的是绑定层意外，仍不许出栈。
            Emit(SandboxEventKind.GuestFault, ("stage", "tick-unexpected"), ("err", ex.Message));
        }
    }

    /// <summary>
    /// 显式越限处置入口（宿主在内存分配失败等场景主动调用）：
    /// 撤销一项能力 + 发 BudgetExceeded（原规格 14 §5）。
    /// </summary>
    public void RevokeOneForViolation(string reason)
    {
        if (_disposed) return;
        var revoked = _gate.RevokeOne(null);
        Emit(SandboxEventKind.BudgetExceeded, ("why", reason), ("revoked", revoked ?? "<none>"));
    }

    /// <summary>
    /// 宿主回调专用：在 Linker 上登记一个宿主函数（契约 §5：未声明能力 → 拒绝 + 审计）。
    /// 该函数被客体调用时先过闸门。回调体内绝不抛异常（异常会成 trap，进而变 BudgetExceeded）。
    /// </summary>
    public void DefineHostFunction(
        Linker linker,
        string module,
        string name,
        string capabilityId,
        Function.UntypedCallbackDelegate body,
        IReadOnlyList<ValueKind> parameterKinds,
        IReadOnlyList<ValueKind> resultKinds)
    {
        ArgumentNullException.ThrowIfNull(linker);
        ArgumentNullException.ThrowIfNull(body);

        linker.DefineFunction(module, name,
            (caller, args, results) =>
            {
                var decision = _gate.Check(capabilityId);
                if (!decision.Allowed) return; // 拒绝：不执行主体，结果保持默认零值
                try
                {
                    body(caller, args, results);
                }
                catch
                {
                    // 主体异常吞掉 —— 事件由主体自行上报，这里只保证不转成 trap。
                }
            },
            parameterKinds, resultKinds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            DisposeStore();
            _module?.Dispose();
            _engine?.Dispose();
        }
        catch
        {
            // 释放路径不抛（P-05 的边界形态）。
        }
        finally
        {
            _module = null;
            _engine = null;
        }
    }

    private bool BuildStore()
    {
        // 调用方已捕获异常；此处只在成功路径工作。
        _store = new Store(_engine!);
        _store.SetLimits(memorySize: _budgets.MemoryBytes);
        _store.SetWasiConfiguration(new WasiConfiguration()); // 默认全关 = 默认拒绝

        using var linker = new Linker(_engine!);
        linker.DefineWasi(); // preview1 子集（契约 §4.6：不声明 WASI 1.0）
        _instance = linker.Instantiate(_store, _module!);
        _tick = _instance.GetFunction(TickExportName);
        return true;
    }

    private void DisposeStore()
    {
        _tick = null;
        _instance = null;
        _store?.Dispose();
        _store = null;
    }

    private void Emit(SandboxEventKind kind, params (string Key, object? Value)[] pairs)
    {
        try
        {
            _sink.Emit(new SandboxEvent(kind, SandboxEventPayload.Build(pairs)));
        }
        catch
        {
            // 落点故障不许反噬沙箱（P-05 的兜底形态）。
        }
    }
}
