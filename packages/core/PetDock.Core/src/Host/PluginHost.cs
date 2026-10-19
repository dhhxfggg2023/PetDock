// 插件宿主（docs/01 P-01 内核六能力表第 2 项；M1 契约 §5.2/§5.3 的实现）。
// 线程模型：单一后台调度线程串行执行「命令 → tick 全体 → 处理重载意图」——沙箱基座要求
// 单线程使用（t3 契约），而重载只可能发生在片与片之间：不存在绘制/执行中途换实例的窗口
// （docs/13 §3「绝不替换正在绘制的帧」）。外部调用（Load/Disable/Reload/Snapshot）全部
// 封送到调度线程执行。
// 本文件是 partial 的基础设施半边；生命周期核心（装载/禁用/热重载/恢复）在
// PluginHost.Lifecycle.cs。两半各 <400 行（docs/40 D-03c）。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

public sealed partial class PluginHost : IPetDockPluginHost, IDisposable
{
    public const string PluginsRootEnv = "PETDOCK_PLUGINS_ROOT";
    public const int ResidentTickMs = 1000;
    private const int SchedulerStallMs = 10000;

    private readonly string _pluginsRoot;
    private readonly int _tickIntervalMs;
    private readonly bool _autoLoad;
    private readonly Thread _thread;
    private readonly BlockingCollection<HostCommand> _commands = new(new ConcurrentQueue<HostCommand>());
    private readonly ConcurrentDictionary<string, string> _dirById = new(StringComparer.Ordinal);
    private readonly List<PluginHandle> _handles = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private volatile bool _stopping;

    /// <summary>调度线程命令（D-03b：系统自带泛型委托名含黑名单子串，自声明代替）。</summary>
    private delegate void HostCommand();

    /// <summary>生命周期事件（docs/13 §4 六事件：Discovered 由 Discover 隐式给出，其余五者由本宿主发）。
    /// 回调在宿主调度线程上触发：观察者必须快速返回并自行保证线程安全（docs/13 §4：事件不得阻塞宿主）。</summary>
    public event LifecycleEventDelegate? LifecycleEvent;

    /// <summary>构造即持有根目录与节奏；Start() 之前不占线程、不碰文件系统。
    /// autoLoad=false 供自检/测试：只起调度器，装载由调用方显式驱动。</summary>
    public PluginHost(string pluginsRoot, int tickIntervalMs = ResidentTickMs, bool autoLoad = true)
    {
        _pluginsRoot = pluginsRoot ?? string.Empty;
        _tickIntervalMs = Math.Max(10, tickIntervalMs);
        _autoLoad = autoLoad;
        _thread = new Thread(Loop)
        {
            Name = "petdock-plugin-host",
            IsBackground = true,
        };
    }

    /// <summary>自检用确定性驱动：true 时调度循环不再自行 tick / 应用重载，
    /// 每个切片由 TickNow() 显式同步触发 —— 预算判据（连续故障计数）需要逐片可读的驱动。
    /// 须在 Start() 之前设置。</summary>
    internal bool ManualTickOnly { get; set; }

    /// <summary>启动调度线程；autoLoad 时装载根目录下全部合法插件（校验不过 → 逐条日志拒载）。</summary>
    public void Start()
    {
        _thread.Start();
        if (_autoLoad)
        {
            PostCommand(AutoLoadAll);
        }
    }

    /// <summary>发现（契约 §5.3）：非递归扫描，只认目录与 manifest 文件的存在，不解析内容。</summary>
    public IReadOnlyList<string> Discover(string pluginsRoot)
    {
        var dirs = new List<string>();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(pluginsRoot))
            {
                if (File.Exists(Path.Combine(dir, ManifestValidator.ManifestFileName)))
                {
                    dirs.Add(Path.GetFullPath(dir));
                }
            }
        }
        catch (Exception ex)
        {
            KernelLog.Write($"discover-failed {ex.GetType().Name}: {ex.Message}");
        }
        dirs.Sort(StringComparer.OrdinalIgnoreCase);
        return dirs;
    }

    /// <summary>校验（契约 §5.2）：docs/21 全语义，错误逐条返回；成功即登记 id → 目录。</summary>
    public Result<PluginManifest, IReadOnlyList<ManifestError>> ValidateManifest(string pluginDir)
    {
        Result<PluginManifest, IReadOnlyList<ManifestError>> result = ManifestValidator.Validate(pluginDir);
        if (result.IsOk)
        {
            _dirById[result.Ok!.Id] = Path.GetFullPath(pluginDir);
        }
        return result;
    }

    private void AutoLoadAll()
    {
        if (string.IsNullOrWhiteSpace(_pluginsRoot))
        {
            KernelLog.Write("host auto-load skipped: no plugins root");
            return;
        }
        foreach (string dir in Discover(_pluginsRoot))
        {
            Result<PluginManifest, IReadOnlyList<ManifestError>> result = ValidateManifest(dir);
            if (!result.IsOk)
            {
                foreach (ManifestError error in result.Error!)
                {
                    KernelLog.Write($"manifest-error {error.Field}: {error.Message}");
                }
                continue;
            }
            PluginManifest manifest = result.Ok!;
            try
            {
                LoadCore(manifest, CancellationToken.None);
            }
            catch (PluginLoadException ex)
            {
                KernelLog.Write($"load-reject {manifest.Id}: {ex.Message}");
            }
        }
    }

    /// <summary>探测插件根目录：环境变量 PETDOCK_PLUGINS_ROOT 优先（须存在），
    /// 否则从 AppContext.BaseDirectory 逐级向上找 packages/plugins 或 plugins。
    /// 失败返回 null —— 宿主空转不致命（部署形态可能不带插件目录）。</summary>
    public static string? ProbePluginsRoot()
    {
        string? env = Environment.GetEnvironmentVariable(PluginsRootEnv);
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return env;
        }
        string? baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            return null;
        }
        try
        {
            for (DirectoryInfo? dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
            {
                string packagesPlugins = Path.Combine(dir.FullName, "packages", "plugins");
                if (Directory.Exists(packagesPlugins))
                {
                    return packagesPlugins;
                }
                string plain = Path.Combine(dir.FullName, "plugins");
                if (Directory.Exists(plain))
                {
                    return plain;
                }
            }
        }
        catch (ArgumentException)
        {
            // 路径形态异常（如已到 UNC 根）—— 放弃向上探测
        }
        return null;
    }

    /// <summary>对插件目录挂 manifest 文件监听：文件一被触碰就置重载意图，
    /// 真正的重载在调度线程片间执行（绝不会在执行/绘制中途换实例）。</summary>
    private void Watch(PluginHandle handle)
    {
        try
        {
            FileSystemWatcher watcher = new(handle.PluginDir, ManifestValidator.ManifestFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            PluginHandle target = handle;
            watcher.Changed += (_, _) => PostCommand(() => target.ReloadPending = true);
            watcher.Created += (_, _) => PostCommand(() => target.ReloadPending = true);
            watcher.Deleted += (_, _) => PostCommand(() => target.ReloadPending = true);
            watcher.Renamed += (_, _) => PostCommand(() => target.ReloadPending = true);
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            KernelLog.Write($"watch-failed plugin={handle.Manifest.Id} {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>停机：停监听、停调度线程（Join 3s）、随后按 docs/11 §9 预算释放全部沙箱。
    /// 重复 Dispose 为无害 no-op。</summary>
    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }
        _stopping = true;

        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        try
        {
            _commands.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }

        if (Thread.CurrentThread != _thread && _thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }
        DisposeAllHandles();
        _commands.Dispose();
    }

    private void DisposeAllHandles()
    {
        // 此时调度线程已停（或 Join 超时），当前线程是唯一写者
        foreach (PluginHandle handle in _handles.ToArray())
        {
            if (handle.State is not (PluginLifecycleState.Running or PluginLifecycleState.Suspended))
            {
                continue;
            }
            Publish("pluginUnloading", handle.Manifest.Id);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            handle.Sandbox?.Dispose();
            handle.Sandbox = null;
            handle.Tier2?.Dispose();
            handle.Tier2 = null;
            handle.State = PluginLifecycleState.Disposed;
            long ms = clock.ElapsedMilliseconds;
            if (ms > LifecycleDisposeBudgetMs)
            {
                KernelLog.Write($"dispose-over-budget plugin={handle.Manifest.Id} ms={ms}");
            }
        }
    }

    private void Publish(string name, string pluginId)
    {
        LifecycleEvent?.Invoke(new LifecycleEvent(name, pluginId, Environment.TickCount64));
        KernelLog.Write($"lifecycle {name} plugin={pluginId}");
    }

    /// <summary>入队一条调度线程命令（fire-and-forget；停机中静默丢弃）。</summary>
    private void PostCommand(HostCommand work)
    {
        try
        {
            _commands.Add(work);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding 之后：宿主正在停机
        }
    }

    /// <summary>同步封送：在调度线程上执行并等完成/等异常（Resume/自检用）。</summary>
    private void RunOnScheduler(HostCommand work)
    {
        if (Thread.CurrentThread == _thread)
        {
            work();
            return;
        }
        using var done = new ManualResetEventSlim(false);
        Exception? caught = null;
        PostCommand(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                done.Set();
            }
        });
        if (!done.Wait(SchedulerStallMs))
        {
            throw new TimeoutException("plugin host scheduler stalled");
        }
        if (caught is not null)
        {
            ExceptionDispatchInfo.Capture(caught).Throw();
        }
    }

    /// <summary>异步封送（接口方法用）：返回在调度线程上执行结果的 Task。</summary>
    private Task<T> RunOnSchedulerAsync<T>(Func<T> work)
    {
        if (Thread.CurrentThread == _thread)
        {
            return Task.FromResult(work());
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostCommand(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>异步封送（无返回值版本；Task&lt;object?&gt; 协变到 Task）。</summary>
    private Task RunOnSchedulerVoid(HostCommand work)
    {
        if (Thread.CurrentThread == _thread)
        {
            work();
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostCommand(() =>
        {
            try
            {
                work();
                tcs.SetResult(null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>一致性读取（自检用）：在调度线程上取值，避免对可变状态的撕裂读。</summary>
    internal T Snapshot<T>(Func<T> read)
    {
        if (Thread.CurrentThread == _thread)
        {
            return read();
        }
        using var done = new ManualResetEventSlim(false);
        T value = default!;
        PostCommand(() =>
        {
            try
            {
                value = read();
            }
            finally
            {
                done.Set();
            }
        });
        if (!done.Wait(SchedulerStallMs))
        {
            throw new TimeoutException("plugin host scheduler stalled (Snapshot)");
        }
        return value;
    }
}
