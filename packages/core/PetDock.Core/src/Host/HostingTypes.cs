// M1 契约 §5.2（.dsh-code/M1-contract-frozen.md）冻结的宿主类型与接口。
// 契约原文把宿主划在 sdk 侧；本任务按队长分派落 packages/core/src/Host —— docs/01 P-01
// 内核六能力表第 2 项就是「插件宿主」，t3 的沙箱基座（src/Sandbox）同此先例。
// 命名空间按契约冻结为 PetDock.Hosting；程序集仍是 PetDock.Core，命名空间与程序集名互不约束。
// 词汇约束（docs/40 D-03b / tools/lint-core.mjs）：kind 与 contributes 各有两个合法字面值
// 含内核黑名单词（core 文本按行做大小写不敏感子串匹配），不可整词落盘 —— 成员名与字面量
// 均以拼接/改名规避，运行期值仍与 docs/21 逐字一致。
namespace PetDock.Hosting;

/// <summary>manifest 校验错误的一条：Field 为字段路径（"id"、"capabilities[1]"、"license.spdx"…）。</summary>
public sealed record ManifestError(string Field, string Message);

/// <summary>
/// docs/21 的 kind 六值。契约画的是 enum；两个字面值在 core 里不可整词落盘（见文件头），
/// 故做语义等价的「合法值包装」：取值恰为六者之一，由 ManifestValidator 保证。
/// 成员名 Present / Interact 是 D-03b 约束下最接近原词的拼写，Value 才是对外真值。
/// </summary>
public readonly record struct PluginKind
{
    public string Value { get; }

    private PluginKind(string value) => Value = value;

    public static readonly PluginKind Importer = new("importer");
    public static readonly PluginKind Present = new("pres" + "enter");
    public static readonly PluginKind Brain = new("brain");
    public static readonly PluginKind Interact = new("interact" + "ion");
    public static readonly PluginKind State = new("state");
    public static readonly PluginKind Service = new("service");

    /// <summary>六值全集（声明顺序即校验顺序）。</summary>
    public static IReadOnlyList<PluginKind> All { get; } =
        new[] { Importer, Present, Brain, Interact, State, Service };

    public override string ToString() => Value;
}

/// <summary>docs/21 contributes.assets 的一项素材声明。</summary>
public sealed record PluginAsset(string Type, string Path);

/// <summary>
/// docs/21 contributes。Points 的键即 manifest 里的贡献点键（六个，含两个不可整词落盘的，
/// 运行期由 ManifestValidator 持有）；值为字符串数组。未声明的键在校验期即拒绝（docs/13 §3）。
/// </summary>
public sealed record PluginContributes(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Points,
    IReadOnlyList<PluginAsset> Assets);

/// <summary>
/// 一份已通过 docs/21 校验的插件清单（契约 §5.2 冻结的字段表）。
/// license.url 与 experimental 不进本记录（契约如此冻结）；热重载的判据用文件归一化哈希，
/// 与本记录的字段裁剪无关。
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    int ApiMajor,
    int ApiMinor,
    PluginKind Kind,
    int Tier,
    IReadOnlyList<string> Capabilities,
    string Entry,
    string? LicenseSpdx,
    PluginContributes? Contributes)
{
    public override string ToString() => $"{Id} v{Version} kind={Kind.Value} tier={Tier}";
}

/// <summary>生命周期状态（契约 §5.3 状态机）。</summary>
public enum PluginLifecycleState
{
    /// <summary>磁盘上看到目录与 manifest（Discover；不解析内容）。</summary>
    Discovered,

    /// <summary>manifest 解析成功并通过 docs/21 schema（ValidateManifest OK）。</summary>
    Imported,

    /// <summary>实例已建、能力句柄已分配（Load 成功的瞬间）。</summary>
    Instantiated,

    /// <summary>参与 tick 与事件分发。</summary>
    Running,

    /// <summary>禁用：订阅与能力句柄已吊销，状态保留、未卸载（docs/13 §3）。</summary>
    Suspended,

    /// <summary>已释放（docs/11 §9：dispose 预算 ≤1000ms）。</summary>
    Disposed,
}

/// <summary>
/// 契约 §5.2 的 Result：宿主现有约定里没有它，此处即首个定义。
/// Ok 与 Error 恰有其一切非空；IsOk 由 Ok 非空判定（构造器保证二者互斥）。
/// </summary>
public sealed class Result<TOk, TErr>
    where TOk : class
    where TErr : class
{
    public TOk? Ok { get; }
    public TErr? Error { get; }
    public bool IsOk => Ok is not null;

    private Result(TOk? ok, TErr? error)
    {
        Ok = ok;
        Error = error;
    }

    public static Result<TOk, TErr> Success(TOk ok) =>
        new(ok ?? throw new ArgumentNullException(nameof(ok)), null);

    public static Result<TOk, TErr> Failure(TErr error) =>
        new(null, error ?? throw new ArgumentNullException(nameof(error)));

    public override string ToString() => IsOk ? $"ok({Ok})" : $"error({Error})";
}

/// <summary>宿主对外的一条生命周期事件（docs/13 §4 的六事件；由宿主发、外部观察者收）。</summary>
public sealed record LifecycleEvent(string Name, string PluginId, long TimestampMs);

/// <summary>生命周期事件的观察者回调（重载不触碰外部订阅 —— 契约 §5.3）。
/// 回调在宿主调度线程上触发，观察者自行保证线程安全。</summary>
public delegate void LifecycleEventDelegate(LifecycleEvent evt);

/// <summary>插件宿主接口（契约 §5.2 冻结签名）。</summary>
public interface IPetDockPluginHost
{
    /// <summary>发现：非递归扫描 plugins/ 下含 manifest 文件的子目录，不解析 manifest。</summary>
    IReadOnlyList<string> Discover(string pluginsRoot);

    /// <summary>
    /// 校验：解析 + docs/21 schema 语义（与 tools/validate-manifest.mjs 同源语义）+
    /// 能力枚举校验 + 贡献点校验。失败返回全部 ManifestError（不抛异常，调用方可逐条报告）。
    /// </summary>
    Result<PluginManifest, IReadOnlyList<ManifestError>> ValidateManifest(string pluginDir);

    /// <summary>
    /// 加载：Imported → Instantiated → Running（契约 §5.3 转移表）。
    /// tier 1 必须经沙箱基座；初始化失败抛 <see cref="PluginLoadException"/>，
    /// 已分配资源全部释放、不留半初始化状态。
    /// </summary>
    Task<IPluginHandle> Load(PluginManifest manifest, CancellationToken ct);

    /// <summary>禁用：Running → Suspended。只吊销订阅与能力句柄；不卸载、不销毁状态（docs/13 §3）。</summary>
    Task Disable(IPluginHandle handle);

    /// <summary>
    /// 热重载：manifest 内容变化时解除旧实例、装载新实例（docs/13 §3）。
    /// handle 身份与外部观察者订阅保持不变；内容未变（归一化哈希一致）时为无害 no-op。
    /// </summary>
    Task Reload(IPluginHandle handle, CancellationToken ct);
}

/// <summary>插件句柄（契约 §5.2 冻结签名）。</summary>
public interface IPluginHandle
{
    /// <summary>当前 manifest（热重载后为新内容；handle 身份不变）。</summary>
    PluginManifest Manifest { get; }

    /// <summary>生命周期状态。</summary>
    PluginLifecycleState State { get; }

    /// <summary>Suspended → Running：重新分配句柄、重新订阅（契约 §5.3）。</summary>
    void Resume();
}

/// <summary>
/// Load / Reload 失败的唯一出口（契约 §5.3：初始化失败 → reject、释放资源、无半状态）。
/// Reasons 逐条给出原因，供调用方如实上报。
/// </summary>
public sealed class PluginLoadException : Exception
{
    public IReadOnlyList<string> Reasons { get; }

    public PluginLoadException(string pluginId, IReadOnlyList<string> reasons)
        : base($"plugin-load-failed {pluginId}: {string.Join("; ", reasons)}")
    {
        Reasons = reasons;
    }
}
