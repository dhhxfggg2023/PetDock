// RFC-37 §3.2（片 1b）：桌面上不止一只。
//
// 片 1a 让清单承认了「有两只」，但桌上仍然只有一只 —— 因为窗口、内容源、帧泵各只有一份。
// 本类把清单里其余的每一只装成一条**槽位**：自己的窗口 + 自己的 PetFrameSource + 自己的帧循环。
//
// 主窗不占槽位：它的生命周期归 UiHost（RFC-14 的可见性↔泵、RFC-22 的位置落盘都挂在它上面），
// 本类只借它的位置来摆其余几只，并在「显示/隐藏全部」时把它一并算进去（否则托盘的
// 「切换全部」只切一半，用户看到的那只没反应）。
//
// 片 1b 只做窗口层：导入仍然是替换语义（RFC-15，解除它要动 原规格 18 §2.1，见 RFC-37 §9 片 2），
// 所以这里只把**清单里已经有的**那几只摆出来 —— 用户导入两只之后就看到两只，不需要再点任何东西。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class PetWindowSet : IDisposable
{
    /// <summary>给每只宠物窗接点击/拖动反馈（宿主侧实现；集合不反向依赖 UiHost）。</summary>
    internal delegate void PetWindowAttach(LayeredFrameWindow window, System.Func<string?> packId);

    /// <summary>新槽位登记时调用；已存在的槽位由 <see cref="UiHost.AttachPetSet"/> 补接。</summary>
    internal PetWindowAttach? WindowAttached { get; set; }
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
        ResidentFrameLoop Loop,
        string? PetId = null,
        string? Name = null);

    private readonly LayeredFrameWindow _primary;
    private readonly ResidentFrameLoop? _primaryLoop;
    private readonly PetInstance? _primaryInstance;
    private readonly List<Slot> _slots = new();

    /// <summary>清单（位置按清单 id 存；槽位没带 id 时用它反查）。可空 = 自检形态。</summary>
    private PetDataStore? _store;

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
            string name = string.IsNullOrWhiteSpace(pack.Name) ? Path.GetFileName(pack.Path) : pack.Name;
            instance = roster.BuildInstance(
                "resident-set", new[] { pack.Path }, freshView: true, imported: true, displayName: name);
            if (instance is null)
            {
                KernelLog.Write($"pet-set-skip pack={pack.InstanceId} reason=no-view");
                return null;
            }

            // 画板尺寸的唯一权威是窗口的物理尺寸（150% DPI 下是 192）：不在这里预挂接 128，
            // 由 PetFrameSource 的首次取帧按实测尺寸做 init/describe/attach。
            var source = new PetFrameSource(instance);
            var window = new LayeredFrameWindow(source);
            source.WindowHandle = window.WindowHandle;

            // 鼠标消息进交互会话 + moveTo 真移窗：与主窗同一条接线（RFC-35 §3.2）。
            // 不接就是「第二只点了没反应」「第二只不会走」。
            FrameComposition.WireWindow(window, instance);
            return new Slot(instance, pack.Path, window, source, new ResidentFrameLoop(window), pack.InstanceId, name);
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
        ResidentFrameLoop? primaryLoop = null,
        UserSettingsStore? settings = null)
    {
        var set = new PetWindowSet(primary, primaryLoop, shown);
        set.Settings = settings;
        set._store = store;
        IReadOnlyList<PetDataStore.ActivePack> catalog = store.ReadPets();
        IReadOnlyList<string> hidden = settings?.Load().HiddenPets ?? Array.Empty<string>();
        IReadOnlyList<PetDataStore.ActivePack> rest = Plan(shown is null ? null : shownPackId, catalog, hidden);
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

    /// <summary>
    /// 登记一条槽位（自检也用它；生产路径见 <see cref="FromCatalog"/>）。
    /// 「拖拽落地 → 按宠物记位置」的订阅在这里做一次、只做一次：无论槽位是装配期建出来的
    /// 还是运行期追加进来的，落盘那条链都是同一条（两个入口各接一次就会漏掉一边）。
    /// </summary>
    public void Add(Slot slot)
    {
        slot.Window.WindowEvent += (type, payload) => OnSlotMoved(slot, type, payload);
        WireContextMenu(slot.Window);
        WireDoubleClick(slot.Window);
        _slots.Add(slot);
        WindowAttached?.Invoke(slot.Window, () => IdOf(slot));
    }

    /// <summary>
    /// 把一只**已经活着的**实例摆上桌（RFC-37 §3.5 片 2：导入即追加）。
    /// 与 <see cref="Create"/> 的区别只有一处：实例由调用方建好并初始化过（导入编排里那一步
    /// 决定了失败能否回滚），这里只补窗口、帧源句柄、鼠标接线与帧循环。
    /// 失败返回 false 且不留残骸 —— 调用方据此退回替换语义，桌面上原来那几只不受影响。
    /// </summary>
    public bool TryAdd(PetInstance instance, string packPath, string? catalogId = null)
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

            // 第二只点了必须有反应、moveTo 必须真挪窗：与主窗同一条接线（RFC-35 §3.2）。
            FrameComposition.WireWindow(window, instance);

            var slot = new Slot(
                instance, packPath, window, source, new ResidentFrameLoop(window),
                catalogId ?? CatalogIdOf(packPath), instance.DisplayName);
            Add(slot);

            if (_started)
            {
                PlaceAtStart(slot, _slots.Count);
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
    /// 把其余几只摆上桌：每只**先回到自己存过的位置**，没存过就按主窗当前位置向左挨着排
    /// （右数第二只、第三只……），再起各自的泵。
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
        for (int i = 0; i < _slots.Count; i++)
        {
            PlaceAtStart(_slots[i], i + 1);
            _slots[i].Loop.Start();
        }

        _primaryLoop?.Start();
    }

    /// <summary>
    /// 把清单里的一只重新摆上桌（2026-10-30：宠物页的「在桌面上显示」）。
    /// 与装配期的区别只是时机：同一套 Create + Add，位置取它存过的那一处。
    /// 已经摆着的那一只返回 true（幂等）—— 页面按钮点两下不该出现两只。
    /// </summary>
    public bool ShowPack(PetSurfaceRoster? roster, PetDataStore.ActivePack pack)
    {
        if (_disposed || pack is null)
        {
            return false;
        }

        if (Find(pack.InstanceId) is not null)
        {
            return true;
        }

        Slot? slot = Create(roster, pack);
        if (slot is null)
        {
            return false;
        }

        Add(slot);
        if (_started)
        {
            // PlaceAtStart 会先套上这一只自己的尺寸（settings.petSizes）。
            PlaceAtStart(slot, _slots.Count);
            slot.Loop.Start();
        }

        KernelLog.Write($"pet-set-show pack={pack.InstanceId} {Describe()}");
        return true;
    }

    /// <summary>把其余几只的窗口一起改成同一逻辑边长（旧入口：现在只在装配期做兜底）。</summary>
    public void SetLogicalSize(int edge)
    {
        foreach (Slot slot in _slots)
        {
            slot.Window.SetLogicalSize(edge);
        }
    }

    /// <summary>
    /// 只改**这一只**的逻辑边长（2026-10-31；用户：「调整大小应该是每个宠物单独调整」）。
    /// 返回 false = 桌上没有这一只（收起的 / 已删的），调用方据此决定是不是只记设置。
    /// </summary>
    public bool SetLogicalSize(string id, int edge)
    {
        Slot? slot = Find(id);
        if (slot is null)
        {
            return false;
        }

        slot.Window.SetLogicalSize(edge);
        KernelLog.Write($"pet-set-size id={id} size={edge}");
        return true;
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
