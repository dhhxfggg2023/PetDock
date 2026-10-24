// RFC-37 §3.2（片 1b）：桌面上不止一只。
//
// 片 1a 让清单承认了「有两只」，但桌上仍然只有一只 —— 因为窗口、内容源、帧泵各只有一份。
// 本类把清单里其余的每一只装成一条**槽位**：自己的窗口 + 自己的 PetFrameSource + 自己的帧循环。
//
// 主窗不占槽位：它的生命周期归 UiHost（RFC-14 的可见性↔泵、RFC-22 的位置落盘都挂在它上面），
// 本类只借它的位置来摆其余几只，并在「显示/隐藏全部」时把它一并算进去（否则托盘的
// 「切换全部」只切一半，用户看到的那只没反应）。
//
// 片 1b 只做窗口层：导入仍然是替换语义（RFC-15，解除它要动 docs/18 §2.1，见 RFC-37 §9 片 2），
// 所以这里只把**清单里已经有的**那几只摆出来 —— 用户导入两只之后就看到两只，不需要再点任何东西。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class PetWindowSet : IDisposable
{
    /// <summary>相邻两只的水平间距（物理像素）：128 的帧宽 + 12 的空隙，看得出是两只。</summary>
    private const int SpacingX = 140;

    /// <summary>
    /// 一条槽位。实例可空只给自检用：判据要验的是「窗口 + 泵」这套机制，
    /// 而名册在自检里不一定有可承载的视图实现 —— 那时仍应有窗口与泵，只是没有宠物。
    /// 生产路径（<see cref="Create"/>）永远给实例。
    /// </summary>
    internal sealed record Slot(
        PetInstance? Instance,
        string PackPath,
        LayeredFrameWindow Window,
        PetFrameSource? Source,
        ResidentFrameLoop Loop);

    private readonly LayeredFrameWindow _primary;
    private readonly ResidentFrameLoop? _primaryLoop;
    private readonly PetInstance? _primaryInstance;
    private readonly List<Slot> _slots = new();
    private bool _started;
    private bool _disposed;

    public PetWindowSet(
        LayeredFrameWindow primary,
        ResidentFrameLoop? primaryLoop = null,
        PetInstance? primaryInstance = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _primaryLoop = primaryLoop;
        _primaryInstance = primaryInstance;
    }

    /// <summary>桌上的只数（含主窗）。主窗恒算一只：它可能没有实例（内核纯色兜底）。</summary>
    public int Count => _slots.Count + 1;

    /// <summary>主窗之外的那几只。</summary>
    public IReadOnlyList<Slot> Slots => _slots;

    /// <summary>是否已经摆上桌（自检与诊断读它；<see cref="StartAll"/> 幂等）。</summary>
    public bool Started => _started;

    /// <summary>
    /// 纯函数：清单里除了「已经显示的那只」以外，还要摆哪几只。
    /// 单独拿出来是因为它是本片唯一有分支的判断，判决不必造窗口就能验。
    /// </summary>
    public static IReadOnlyList<PetDataStore.ActivePack> Plan(
        string? shownPackId,
        IReadOnlyList<PetDataStore.ActivePack> catalog)
    {
        var rest = new List<PetDataStore.ActivePack>();
        foreach (PetDataStore.ActivePack pack in catalog)
        {
            if (string.Equals(pack.InstanceId, shownPackId, StringComparison.Ordinal))
            {
                continue; // 这一只就在主窗里
            }

            rest.Add(pack);
        }

        return rest;
    }

    /// <summary>
    /// 把一个素材包装成一条槽位：建实例 → 初始化 → 窗口 → 帧源句柄回填 → 鼠标接线 → 帧循环。
    /// 任何一步失败都只**跳过这一只**（日志留痕），不许连累已经在桌上的那几只。
    /// </summary>
    public static Slot? Create(PetSurfaceRoster? roster, PetDataStore.ActivePack pack)
    {
        if (roster is null)
        {
            return null;
        }

        PetInstance? instance = null;
        try
        {
            instance = roster.BuildInstance("resident-set", new[] { pack.Path }, freshView: true);
            if (instance is null)
            {
                KernelLog.Write($"pet-set-skip pack={pack.InstanceId} reason=no-view");
                return null;
            }

            instance.AttachAndStart(128, 128, 1.0);
            instance.Start();

            var source = new PetFrameSource(instance);
            var window = new LayeredFrameWindow(source);
            source.WindowHandle = window.WindowHandle;

            // 鼠标消息进交互会话：与主窗同一条接线（RFC-35 §3.2）。不接就是「第二只点了没反应」。
            FrameComposition.WirePointer(window, instance);
            return new Slot(instance, pack.Path, window, source, new ResidentFrameLoop(window));
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-set-skip pack={pack.InstanceId} reason={error.GetType().Name}: {error.Message}");
            instance?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// 按清单装配集合：主窗之外，把清单里其余仍存在的素材包各摆一只。
    /// <paramref name="shown"/> 非空时跳过清单里「当前选中」的那个包 —— 它已经在主窗里了；
    /// 主窗显示的是默认宠物（清单恢复失败）时一个都不跳过，因为清单里那几只都不在桌上。
    /// </summary>
    public static PetWindowSet FromCatalog(
        PetSurfaceRoster? roster,
        PetInstance? shown,
        string? shownPackId,
        PetDataStore store,
        LayeredFrameWindow primary,
        ResidentFrameLoop? primaryLoop = null)
    {
        var set = new PetWindowSet(primary, primaryLoop, shown);
        IReadOnlyList<PetDataStore.ActivePack> catalog = store.ReadPets();
        IReadOnlyList<PetDataStore.ActivePack> rest = Plan(shown is null ? null : shownPackId, catalog);
        foreach (PetDataStore.ActivePack pack in rest)
        {
            Slot? slot = Create(roster, pack);
            if (slot is not null)
            {
                set.Add(slot);
            }
        }

        KernelLog.Write($"pet-set {set.Describe()} catalog={catalog.Count}");
        return set;
    }

    /// <summary>登记一条槽位（自检也用它；生产路径见 <see cref="FromCatalog"/>）。</summary>
    public void Add(Slot slot)
    {
        _slots.Add(slot);
    }

    /// <summary>
    /// 桌上每只的即时读数（RFC-37 片 4）：主窗一行 + 每条槽位一行，按**归因键**（实例 id）索引。
    /// 这是性能页逐宠物那一列的唯一来源 —— 每只都有自己的泵（片 1b），所以帧耗时/超限计数
    /// 可以归属到它自己，而不是像单泵形态那样只能写「(进程共享)」。
    /// 主窗没有循环（或没有实例）时照样给一行，Frames 为 null，页面写「—」。
    /// </summary>
    public IReadOnlyList<PetReading> Readings()
    {
        var rows = new List<PetReading>(_slots.Count + 1)
        {
            new(
                Id: _primaryInstance?.Id ?? PrimaryId,
                Visible: _primary.Visible,
                Pumping: _primaryLoop?.Pumping ?? false,
                Frames: FramesOf(_primaryLoop)),
        };

        foreach (Slot slot in _slots)
        {
            rows.Add(new PetReading(
                Id: slot.Instance?.Id ?? "(slot-" + (rows.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
                Visible: slot.Window.Visible,
                Pumping: slot.Loop.Pumping,
                Frames: FramesOf(slot.Loop)));
        }

        return rows;
    }

    /// <summary>没有实例可归因时用的键（主窗兜底纯色、或自检造的空槽位）。</summary>
    private string PrimaryId => _primaryInstance?.Id ?? "(primary)";

    private static PetFrameReadings? FramesOf(ResidentFrameLoop? loop) =>
        loop is null
            ? null
            : new PetFrameReadings(loop.AvgFrameMs, loop.MaxFrameMs, loop.TargetFrameMs, loop.OverBudgetFrames);

    /// <summary>
    /// 把一只**已经活着的**实例摆上桌（RFC-37 §3.5 片 2：导入即追加）。
    /// 与 <see cref="Create"/> 的区别只有一处：实例由调用方建好并初始化过（导入编排里那一步
    /// 决定了失败能否回滚），这里只补窗口、帧源句柄、鼠标接线与帧循环。
    /// 失败返回 false 且不留残骸 —— 调用方据此退回替换语义，桌面上原来那几只不受影响。
    /// </summary>
    public bool TryAdd(PetInstance instance, string packPath)
    {
        if (_disposed || instance is null)
        {
            return false;
        }

        LayeredFrameWindow? window = null;
        PetFrameSource? source = null;
        try
        {
            source = new PetFrameSource(instance);
            window = new LayeredFrameWindow(source);
            source.WindowHandle = window.WindowHandle;

            // 第二只点了必须有反应：与主窗同一条指针接线（RFC-35 §3.2）。
            FrameComposition.WirePointer(window, instance);

            var slot = new Slot(instance, packPath, window, source, new ResidentFrameLoop(window));
            _slots.Add(slot);

            if (_started)
            {
                (int x, int y) = _primary.CurrentPosition;
                window.MoveTo(x - (SpacingX * _slots.Count), y);
                slot.Loop.Start();
            }

            KernelLog.Write($"pet-set-add pack={packPath} {Describe()}");
            return true;
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-set-add-failed pack={packPath} {error.GetType().Name}: {error.Message}");
            try
            {
                window?.Dispose();
            }
            catch (Exception disposeError)
            {
                KernelLog.Write($"pet-set-add-window-dispose-failed {disposeError.GetType().Name}");
            }

            return false;
        }
    }

    /// <summary>
    /// 把其余几只摆上桌：按主窗**当前**位置向左挨着排（右数第二只、第三只……），再起各自的泵。
    /// 位置取主窗的实时位置而不是写死的坐标 —— 用户拖过主窗之后，隐藏再显示仍然挨着它。
    /// 幂等：已经在跑的那几只 <see cref="ResidentFrameLoop.Start"/> 会直接返回。
    /// </summary>
    public void StartAll()
    {
        if (_disposed)
        {
            return;
        }

        _started = true;
        (int x, int y) = _primary.CurrentPosition;
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot slot = _slots[i];
            slot.Window.MoveTo(x - (SpacingX * (i + 1)), y);
            slot.Loop.Start();
        }

        _primaryLoop?.Start();
    }

    /// <summary>隐藏所有槽位并停泵（托盘的「隐藏宠物」：隐藏的宠物不该继续烧 CPU）。</summary>
    public void HideAll()
    {
        foreach (Slot slot in _slots)
        {
            slot.Loop.Hide();
        }
    }

    /// <summary>
    /// 显示/隐藏全部：以**主窗**的可见性为准（托盘的「切换全部」只有一条命令，
    /// 两套可见性状态会让用户点一次只切一半）。
    /// </summary>
    public void ToggleAll()
    {
        if (_primary.Visible)
        {
            _primaryLoop?.Hide();
            HideAll();
            return;
        }

        StartAll();
    }

    /// <summary>一行读数（日志与判据读它）。</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append("pets=").Append(Count.ToString(CultureInfo.InvariantCulture));
        text.Append(" extras=").Append(_slots.Count.ToString(CultureInfo.InvariantCulture));
        text.Append(" started=").Append(_started ? "true" : "false");
        text.Append(" ids=");
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(_slots[i].Instance?.Id ?? "(none)");
        }

        return text.ToString();
    }

    /// <summary>释放其余几只（窗口、实例、泵）；主窗归 UiHost，不在这里释放。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Slot slot in _slots)
        {
            try
            {
                slot.Loop.Dispose();
            }
            catch (Exception error)
            {
                KernelLog.Write($"pet-set-loop-dispose-failed {error.GetType().Name}: {error.Message}");
            }

            try
            {
                slot.Window.Dispose();
            }
            catch (Exception error)
            {
                KernelLog.Write($"pet-set-window-dispose-failed {error.GetType().Name}: {error.Message}");
            }

            try
            {
                slot.Instance?.Dispose();
            }
            catch (Exception error)
            {
                KernelLog.Write($"pet-set-instance-dispose-failed {error.GetType().Name}: {error.Message}");
            }
        }

        _slots.Clear();
    }
}
