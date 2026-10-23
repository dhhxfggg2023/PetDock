// RFC-24（docs/17）：把 State 接口的缓冲区接到磁盘上。
// 层次：插件实现（IPetState，可以是 NullState）管「载荷语义」；本类管「缓冲区 / 上限 / 迁移 / 落盘时机」。
// 崩溃恢复语义（docs/11:211-214）：write 只进缓冲区，进程崩了缓冲全丢、磁盘不动；
// 只有 Commit 会落盘，且落盘是原子替换（旧文件要么完整可见、要么被新文件完整替换）。
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>
/// 一个实例的状态层落盘实现（RFC-24 §6）。包装插件给的 <see cref="IPetState"/>，
/// 对它而言是透明的：schema 版本、读、写语义都来自内层实现，本层不重解释载荷。
/// </summary>
internal sealed class PersistentPetState : IPetState
{
    /// <summary>宿主自己写的键（位置由窗口宿主写，插件写它会被拒绝，见 <see cref="Write"/>）。</summary>
    public const string PositionKey = "position";

    private static readonly IReadOnlySet<string> HostOwnedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        PositionKey,
    };

    private readonly IPetState _core;
    private readonly StateStore _store;
    private readonly string _instanceId;
    private readonly EventNotifyDelegate? _sink;

    private Dictionary<string, JsonNode?> _buffer = new(StringComparer.Ordinal);
    private Dictionary<string, JsonNode?> _committed = new(StringComparer.Ordinal);
    private int _version;
    private bool _dirty;
    private string _blocked = string.Empty;

    private PersistentPetState(string instanceId, IPetState core, StateStore store, EventNotifyDelegate? sink)
    {
        _instanceId = instanceId;
        _core = core;
        _store = store;
        _sink = sink;
    }

    /// <summary>成功落盘的提交次数（判据面）。</summary>
    public int Commits { get; private set; }

    /// <summary>被拒绝的提交次数（判据面）。</summary>
    public int Refusals { get; private set; }

    /// <summary>本实例做过的迁移次数（每次装载最多一次链式迁移）。</summary>
    public int Migrations { get; private set; }

    /// <summary>最近一次拒绝的原因码（见 <see cref="StateRefusal"/>）；空串 = 没被拒过。</summary>
    public string LastRefusal { get; private set; } = string.Empty;

    /// <summary>true = 本实例被拦住、不会再落盘（原因见 <see cref="BlockedReason"/>）。</summary>
    public bool IsBlocked => _blocked.Length > 0;

    /// <summary>被拦原因（<see cref="StateRefusal.SchemaNewer"/> 等）；空 = 未被拦。</summary>
    public string BlockedReason => _blocked;

    /// <summary>磁盘上的状态文件路径（诊断用）。</summary>
    public string StorePath => _store.FilePath;

    /// <summary>有没有还没落盘的改动（崩溃恢复自检的判据面）。</summary>
    public bool HasUnsaved => _dirty;

    /// <summary>
    /// 绑定一个实例：解析状态文件路径 → 读盘 → 必要时迁移。
    /// 装载期的失败一律降级为「内存态 + 一行日志」：状态读不出来不该让宠物起不来。
    /// </summary>
    public static PersistentPetState Attach(
        string instanceId,
        IPetState core,
        string stateDir,
        IReadOnlyList<string>? assetRoots,
        EventNotifyDelegate? sink)
    {
        var store = new StateStore(stateDir, StateStore.KeyFor(instanceId, assetRoots));
        var layer = new PersistentPetState(instanceId, core, store, sink);
        layer.LoadFromDisk();
        return layer;
    }

    /// <summary>docs/11:198：内层实现的版本号（本层不自己升版本）。</summary>
    public int SchemaVersion => _version;

    public IReadOnlyDictionary<string, JsonNode?> ReadAll() => new Dictionary<string, JsonNode?>(_buffer, StringComparer.Ordinal);

    public JsonNode? Read(string key) =>
        !string.IsNullOrEmpty(key) && _buffer.TryGetValue(key, out JsonNode? value) ? value : null;

    /// <summary>docs/11:202：只进缓冲区。宿主拥有的键由宿主写，插件写它被拒绝（RFC-24 §5 第 3 条）。</summary>
    public void Write(IReadOnlyDictionary<string, JsonNode?> patch)
    {
        System.ArgumentNullException.ThrowIfNull(patch);
        foreach (KeyValuePair<string, JsonNode?> pair in patch)
        {
            if (string.IsNullOrEmpty(pair.Key))
            {
                throw new System.ArgumentException("state key must not be empty", nameof(patch));
            }

            if (HostOwnedKeys.Contains(pair.Key))
            {
                throw new System.ArgumentException($"state key '{pair.Key}' is owned by the host", nameof(patch));
            }
        }

        foreach (KeyValuePair<string, JsonNode?> pair in patch)
        {
            _buffer[pair.Key] = pair.Value?.DeepClone();
        }

        _dirty = true;
    }

    /// <summary>宿主写自己的键（位置）：与插件补丁走同一个缓冲区，但不受脚手架写保护。</summary>
    public void WriteHost(string key, JsonNode? value)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new System.ArgumentException("state key must not be empty", nameof(key));
        }

        _buffer[key] = value?.DeepClone();
        _dirty = true;
    }

    /// <summary>docs/11:203：唯一持久化入口。</summary>
    public System.Threading.Tasks.Task CommitAsync()
    {
        if (TryCommit(out StateCommitException? refusal))
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }

        if (refusal!.InnerException is StateLimitException limit)
        {
            throw limit;
        }

        throw new System.InvalidOperationException(
            $"state commit refused ({refusal.Reason}): {refusal.Message}", refusal);
    }

    /// <summary>不抛异常的提交（宿主生命周期边界用：这里抛异常会变成「关窗卡住」）。返回 false = 被拒。</summary>
    public bool TryCommit(out StateCommitException? refusal)
    {
        refusal = null;
        if (_blocked.Length > 0)
        {
            refusal = new StateCommitException(_blocked, $"state layer blocked ({_blocked}) for instance {_instanceId}");
            NoteRefusal(refusal);
            return false;
        }

        if (!_dirty)
        {
            return true;
        }

        try
        {
            _store.Save(_version, _instanceId, _buffer);
        }
        catch (StateLimitException error)
        {
            refusal = new StateCommitException(StateRefusal.OverLimit, error.Message, error);
            NoteRefusal(refusal);
            return false;
        }
        catch (StateCommitException error)
        {
            refusal = error;
            NoteRefusal(error);
            return false;
        }

        _committed = new Dictionary<string, JsonNode?>(_buffer, StringComparer.Ordinal);
        _dirty = false;
        Commits++;
        Publish(EventCatalog.PetStateCommit, new JsonObject { ["petId"] = _instanceId });
        return true;
    }

    /// <summary>docs/11:204：丢弃自上次提交以来的全部缓冲。</summary>
    public void Rollback()
    {
        _buffer = new Dictionary<string, JsonNode?>(_committed, StringComparer.Ordinal);
        _dirty = false;
    }

    /// <summary>docs/11:207：跨版本迁移（1 ≤ to ≤ current，纯函数，返回新载荷）。</summary>
    public IReadOnlyDictionary<string, JsonNode?> Migrate(int from, int to, IReadOnlyDictionary<string, JsonNode?> snapshot)
    {
        System.ArgumentNullException.ThrowIfNull(snapshot);
        if (from < 1 || to < 1 || to > _version || to < from)
        {
            throw new System.ArgumentOutOfRangeException(nameof(to), $"migrate {from}->{to} outside 1..{_version}");
        }

        IReadOnlyDictionary<string, JsonNode?> result = _core.Migrate(from, to, snapshot);
        var copy = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> pair in result)
        {
            copy[pair.Key] = pair.Value?.DeepClone();
        }

        return copy;
    }

    /// <summary>一行诊断（--drivetest 与日志共用）。</summary>
    public string Describe() =>
        $"state-store file={_store.FilePath} version={_version} keys={_buffer.Count} dirty={_dirty}"
        + $" commits={Commits} refusals={Refusals} migrations={Migrations}"
        + $" blocked={(IsBlocked ? _blocked : "no")} last={(LastRefusal.Length == 0 ? "no" : LastRefusal)}";

    /// <summary>
    /// 装载：读盘 → 版本比对 → 迁移。三档结局：
    ///   · 文件不存在 / 坏文件 → 空缓冲区（首次启动是正常路径）；
    ///   · 版本更高 → 被拦（<see cref="StateRefusal.SchemaNewer"/>）：只读装载，一次都不写；
    ///   · 版本更低 → 逐级迁移，成功后立刻落盘并发布 pet.state.migrated（docs/16 让插件发现它的数据被升级了）。
    /// </summary>
    private void LoadFromDisk()
    {
        _version = _core.SchemaVersion;
        StateFile? file = _store.Load();
        if (file is null)
        {
            return;
        }

        if (file.InstanceId.Length > 0 && !string.Equals(file.InstanceId, _instanceId, StringComparison.Ordinal))
        {
            _blocked = StateRefusal.ForeignInstance;
            _buffer = new Dictionary<string, JsonNode?>(file.Values, StringComparer.Ordinal);
            KernelLog.Write($"state-blocked instance={_instanceId} reason={_blocked} file-instance={file.InstanceId}");
            return;
        }

        if (file.SchemaVersion > _version)
        {
            _blocked = StateRefusal.SchemaNewer;
            _buffer = new Dictionary<string, JsonNode?>(file.Values, StringComparer.Ordinal);
            KernelLog.Write($"state-blocked instance={_instanceId} reason={_blocked} file-version={file.SchemaVersion} core-version={_version}");
            return;
        }

        if (file.SchemaVersion == _version)
        {
            _buffer = new Dictionary<string, JsonNode?>(file.Values, StringComparer.Ordinal);
            _committed = new Dictionary<string, JsonNode?>(file.Values, StringComparer.Ordinal);
            return;
        }

        IReadOnlyDictionary<string, JsonNode?> current = file.Values;
        try
        {
            for (int from = file.SchemaVersion; from < _version; from++)
            {
                current = _core.Migrate(from, from + 1, current);
            }
        }
        catch (System.Exception error)
        {
            _blocked = StateRefusal.MigrationFailed;
            _buffer = new Dictionary<string, JsonNode?>(file.Values, StringComparer.Ordinal);
            _committed = new Dictionary<string, JsonNode?>(file.Values, StringComparer.Ordinal);
            KernelLog.Write($"state-migrate-failed instance={_instanceId} from={file.SchemaVersion} to={_version} {error.GetType().Name}: {error.Message}");
            return;
        }

        _buffer = new Dictionary<string, JsonNode?>(current, StringComparer.Ordinal);
        _committed = new Dictionary<string, JsonNode?>(file.Values, StringComparer.Ordinal);
        _dirty = true;
        Migrations++;
        KernelLog.Write($"state-migrated instance={_instanceId} from={file.SchemaVersion} to={_version} keys={_buffer.Count}");

        // 迁移结果立刻落盘：不落的话下次启动会对着同一份旧文件再迁一次。
        if (!TryCommit(out StateCommitException? refusal))
        {
            KernelLog.Write($"state-migrate-commit-failed instance={_instanceId} reason={refusal!.Reason}");
        }

        Publish(EventCatalog.PetStateMigrated, new JsonObject { ["from"] = file.SchemaVersion, ["to"] = _version });
    }

    private void NoteRefusal(StateCommitException refusal)
    {
        Refusals++;
        LastRefusal = refusal.Reason;
        KernelLog.Write($"state-commit-refused instance={_instanceId} reason={refusal.Reason} detail={refusal.Message}");
    }

    private void Publish(string type, JsonObject payload)
    {
        if (_sink is null)
        {
            return;
        }

        try
        {
            _sink(new PetEvent(type, System.Environment.TickCount64, payload, _instanceId));
        }
        catch (System.Exception error)
        {
            KernelLog.Write($"state-publish-failed type={type} {error.GetType().Name}: {error.Message}");
        }
    }
}
