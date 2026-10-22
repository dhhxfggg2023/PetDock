// 插件日志面（RFC-23 / docs/20 §3）。插件写 `PluginLog.Info("...")`，归属由宿主给。
//
// 为什么是一个**静态门面 + 宿主开作用域**，而不是给插件注入一个 logger 对象：
// 插件实现类由名册用无参构造造出来（PetSurfaceLoader.cs:279 的 Activator.CreateInstance），
// 构造之后插件手里只有 PetViewContext / BrainContext 两个上下文，而指针面与状态层**没有**这两个
// 上下文 —— 注入式方案覆盖不到四个 ABI 角色里的两个，等于规格只兑现一半。
//
// 作用域用 AsyncLocal 而不是 [ThreadStatic]：InitAsync 是 Task 形态，实现方在 await 之后续跑时
// 线程可能已经换了，[ThreadStatic] 会让归属随调度漂移（RFC-23 §6 否决的那条）。
using System;
using System.Threading;

namespace PetDock.Core.PetRuntime;

/// <summary>文档 docs/20 §3 的四个级别。顺序即严重度（数值越大越严重）。</summary>
public enum PluginLogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 日志收口（宿主实现）。**实现不得把异常抛给调用点**：一次日志失败不该带走一帧
/// （RFC-23 §3.4；门面自己也会兜一层，见 <see cref="PluginLog.Emit"/>）。
/// </summary>
public interface IPluginLogSink
{
    /// <summary>写一条。pluginId 为空的表示作用域外调用（门面传 <see cref="PluginLog.Unattributed"/>）。</summary>
    void Write(string pluginId, PluginLogLevel level, string message);
}

/// <summary>
/// 插件可调的日志门面。四个方法与 docs/20 §3 一一对应。
///
/// 没装收口（自检、旧装配）时静默丢弃但**计数**：让「没装」与「插件没调」在读数上分得开，
/// 这正是 RFC-21 那一轮学到的（空函数体会让两种沉默长得一样）。
/// </summary>
public static class PluginLog
{
    /// <summary>作用域外调用的归属名（不是空串：空串会被读成「没归属」而丢掉行）。</summary>
    public const string Unattributed = "(unattributed)";

    private static readonly AsyncLocal<string?> Current = new();

    private static long _debugDropped;
    private static long _sinkFailed;
    private static long _unattributed;

    /// <summary>日志收口。null = 没装（丢弃但计数）。</summary>
    public static IPluginLogSink? Sink { get; set; }

    /// <summary>当前作用域的插件 id（null = 作用域外）。</summary>
    public static string? CurrentPlugin => Current.Value;

    /// <summary>debug 被丢弃的条数（docs/20:50 的「仅开发模式落盘」在无 --dev 开关时的落点）。</summary>
    public static long DebugDropped => Interlocked.Read(ref _debugDropped);

    /// <summary>收口自己抛异常的次数（写盘失败等）。业务侧永远看不到这些异常。</summary>
    public static long SinkFailures => Interlocked.Read(ref _sinkFailed);

    /// <summary>作用域外调用被记成 <see cref="Unattributed"/> 的条数。</summary>
    public static long UnattributedCount => Interlocked.Read(ref _unattributed);

    /// <summary>计数归零（自检每轮从零读）。只影响计数，不动作用域与收口。</summary>
    public static void ResetCounters()
    {
        Interlocked.Exchange(ref _debugDropped, 0);
        Interlocked.Exchange(ref _sinkFailed, 0);
        Interlocked.Exchange(ref _unattributed, 0);
    }

    /// <summary>
    /// 把「接下来的插件调用属于谁」钉住。宿主在**每一次**调用进插件代码之前开它（RFC-23 §3.3）。
    /// 返回的可释放对象恢复进入前的作用域（可嵌套）。
    /// </summary>
    public static IDisposable Scope(string? pluginId)
    {
        string? previous = Current.Value;
        Current.Value = string.IsNullOrEmpty(pluginId) ? null : pluginId;
        return new ScopeToken(previous);
    }

    public static void Debug(string message) => Emit(PluginLogLevel.Debug, message);

    public static void Info(string message) => Emit(PluginLogLevel.Info, message);

    public static void Warn(string message) => Emit(PluginLogLevel.Warn, message);

    public static void Error(string message) => Emit(PluginLogLevel.Error, message);

    /// <summary>带异常对象的重载：行里带上异常类型与消息（不解析文本，只是拼在行尾）。</summary>
    public static void Warn(string message, Exception? error) => Emit(PluginLogLevel.Warn, Compose(message, error));

    public static void Error(string message, Exception? error) => Emit(PluginLogLevel.Error, Compose(message, error));

    private static void Emit(PluginLogLevel level, string message)
    {
        // debug 不落盘：docs/20:50 说「仅开发模式（--dev）落盘」，而本项目没有 --dev 开关
        // （RFC-23 §8 第 1 条）。宁可「接受调用、如实计数」，也不假装写下去。
        if (level == PluginLogLevel.Debug)
        {
            Interlocked.Increment(ref _debugDropped);
            return;
        }

        string pluginId = Current.Value ?? Unattributed;
        if (Current.Value is null)
        {
            Interlocked.Increment(ref _unattributed);
        }

        IPluginLogSink? sink = Sink;
        if (sink is null)
        {
            // 没装收口：丢弃但计数（不是静默无痕）。
            Interlocked.Increment(ref _sinkFailed);
            return;
        }

        try
        {
            sink.Write(pluginId, level, message ?? string.Empty);
        }
        catch (Exception)
        {
            // 日志失败不传染业务：帧路径、tick 路径都不许因为写不下去而中断。
            Interlocked.Increment(ref _sinkFailed);
        }
    }

    private static string Compose(string message, Exception? error)
    {
        if (error is null)
        {
            return message ?? string.Empty;
        }

        return (message ?? string.Empty) + " | " + error.GetType().Name + ": " + error.Message;
    }

    /// <summary>作用域令牌：释放即恢复进入前的作用域。</summary>
    private sealed class ScopeToken : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public ScopeToken(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current.Value = _previous;
        }
    }
}
