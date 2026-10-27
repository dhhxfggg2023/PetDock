// RFC-10 §2：Tier-2 进程外沙箱（原规格 14 §1：独立进程 + stdio JSON-RPC + 会话 quota/等待上限）。
// 范围 = 进程边界（spawn、行分隔 JSON-RPC、每请求等待上限、崩溃检测、有序关闭）；包形状与会话
// quota 在 Tier2Wire.cs / Tier2Sandbox.Wire.cs，会话状态在 Tier2Sandbox.Session.cs。
// 本文件只留边界骨架（spawn / 往返 / 关闭）与记账。P-05（原规格 01 §5）：公共方法一律不外抛。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PetDock.Core.Sandbox;

/// <summary>一次请求结果。失败时 <see cref="Error"/> 非空。</summary>
public readonly record struct Tier2Reply(bool Ok, JsonNode? Result, string? Error);

/// <summary>单客体进程外沙箱。单线程同步：一帧一次往返，不并发。</summary>
public sealed partial class Tier2Sandbox : IDisposable
{
    /// <summary>握手方法名；不回它就装载失败。</summary>
    public const string HandshakeMethod = "ping";

    /// <summary>原规格 14 §5 的会话 wire 字节预算默认值（RFC-27 §3.5）。</summary>
    public const long DefaultSessionBytes = 32L * 1024 * 1024;

    /// <summary>连续多少次「等到上限也不回」才算卡死（原规格 14 §5.1）：慢是慢，连续不回才是死。</summary>
    public const int MaxConsecutiveTimeouts = 3;

    private readonly ISandboxEventSink _sink;
    private readonly string _command;
    private readonly string _arguments;
    private readonly string _workingDir;
    private readonly int _requestTimeoutMs;
    private readonly int _frameBudgetMs;
    private readonly long _sessionByteBudget;
    private readonly int _maxFrameBytes;
    private readonly StringBuilder _stderrTail = new();
    private int _consecutiveTimeouts;

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private bool _disposed;

    /// <summary>构造期不 spawn：发现/校验失败不该留下半个进程。</summary>
    public Tier2Sandbox(
        CapabilityGate gate,
        ISandboxEventSink sink,
        string command,
        string arguments = "",
        string workingDir = "",
        int requestTimeoutMs = 2000,
        int frameBudgetMs = 8,
        long sessionByteBudget = DefaultSessionBytes,
        int maxFrameBytes = Tier2Wire.MaxFrameBytes)
    {
        Gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _arguments = arguments;
        _workingDir = workingDir;
        _requestTimeoutMs = requestTimeoutMs > 0 ? requestTimeoutMs : 2000;
        _frameBudgetMs = frameBudgetMs > 0 ? frameBudgetMs : 8;
        _sessionByteBudget = sessionByteBudget > 0 ? sessionByteBudget : DefaultSessionBytes;
        _maxFrameBytes = maxFrameBytes > 0 ? maxFrameBytes : Tier2Wire.MaxFrameBytes;
    }

    public CapabilityGate Gate { get; }

    public bool IsLoaded => !_disposed && _process is { HasExited: false };

    public long Requests { get; private set; }

    public long Timeouts { get; private set; }

    public long Crashes { get; private set; }

    /// <summary>宿主→客体的请求行字节（RFC-27 §3.5）。</summary>
    public long WireInBytes { get; private set; }

    /// <summary>客体→宿主的应答行字节（RFC-27 §3.5）。</summary>
    public long WireOutBytes { get; private set; }

    /// <summary>通知条数（`abi.event`，不应答所以不占 <see cref="Requests"/>）。</summary>
    public long Notifications { get; private set; }

    /// <summary>会话预算被用尽的次数（原规格 14 §5 的越限）。</summary>
    public long QuotaExceeded { get; private set; }

    /// <summary>最近一次成功解码的帧号（RFC-27 §3.2 的严格递增要求）。</summary>
    public long LastFrameId { get; private set; }

    /// <summary>`abi.hello` 的结果；未握手成功时为 null。</summary>
    public Tier2Hello? Hello { get; private set; }

    public int Pid => _process?.Id ?? 0;

    /// <summary>启动子进程并握手。失败只发事件、不抛。</summary>
    public bool Start()
    {
        if (_disposed)
        {
            return false;
        }
        if (_process is not null)
        {
            return IsLoaded;
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = _command,
                Arguments = _arguments,
                WorkingDirectory = _workingDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            Process process = new() { StartInfo = info };
            process.ErrorDataReceived += (_, e) => AppendStderr(e.Data);

            if (!process.Start())
            {
                Emit(SandboxEventKind.GuestFault, ("stage", "start"), ("why", "start-returned-false"));
                process.Dispose();
                return false;
            }

            _process = process;
            _stdin = process.StandardInput;
            _stdout = process.StandardOutput;
            process.BeginErrorReadLine();

            Tier2Reply reply = Call(HandshakeMethod, new JsonObject());
            if (!reply.Ok)
            {
                Emit(SandboxEventKind.GuestFault, ("stage", "handshake"), ("err", reply.Error ?? "<none>"));
                StopProcess();
                return false;
            }

            // RFC-27 §3.1：握手后立刻取一次 meta。拿不到 meta 的 tier-2 客体不该装载——
            // 宿主连它要不要命中图都不知道，之后每一帧都只能靠猜。
            if (!NegotiateHello())
            {
                StopProcess();
                return false;
            }

            Emit(
                SandboxEventKind.Audit,
                ("tier2", "started"),
                ("pid", process.Id),
                ("rpc", HandshakeMethod),
                ("hello", Hello?.Id ?? "<none>"),
                // 渲染预算从这里起只是**宣告**（原规格 14 §5.1）：读数是给帧泵与日志看的，不裁决等待。
                ("frameBudgetMs", _frameBudgetMs));
            _session = Tier2SessionState.Running;
            _restartWindowStartMs = Environment.TickCount64;
            return true;
        }
        catch (Exception ex)
        {
            Emit(SandboxEventKind.GuestFault, ("stage", "start"), ("err", ex.GetType().Name), ("msg", ex.Message));
            StopProcess();
            return false;
        }
    }

    // Tick 在 Tier2Sandbox.Session.cs（RFC-33）：它是「会话还活着吗、还要不要驱动」的一部分。

    /// <summary>一次 JSON-RPC 往返：写一行 JSON，读一行 JSON，带等待上限。</summary>
    public Tier2Reply Call(string method, JsonObject parameters, int? timeoutMs = null)
    {
        if (_stdin is null || _stdout is null || _process is null || _process.HasExited)
        {
            // 「进程没了」的第四条路（write/eof/read 之外）：会话必须落地（RFC-33），
            // 否则 Session 停在 Running 而 IsLoaded 已 false，TryRecover 以为没啥可救。
            if (_session == Tier2SessionState.Running)
            {
                Crashes++;
                NoteCrash("exit");
            }

            return new Tier2Reply(false, null, "process-not-running");
        }

        int budget = timeoutMs ?? _requestTimeoutMs;
        Requests++;
        string requestId = Requests.ToString(CultureInfo.InvariantCulture);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["method"] = method,
            ["params"] = parameters.DeepClone(),
        };

        try
        {
            string line = request.ToJsonString();
            _stdin.WriteLine(line);
            _stdin.Flush();
            if (!ChargeWire(Encoding.UTF8.GetByteCount(line) + 1, "in"))
            {
                return new Tier2Reply(false, null, "quota-session-bytes");
            }
        }
        catch (Exception ex)
        {
            Crashes++;
            Emit(SandboxEventKind.GuestFault, ("stage", "write"), ("method", method), ("err", ex.GetType().Name));
            NoteCrash("write");
            return new Tier2Reply(false, null, $"write-failed: {ex.GetType().Name}");
        }

        // ReadLine 是阻塞的，所以异步读 + 有界等待。等到上限不回**不等于**进程死了（原规格 14 §5.1）：
        // 计一次超时、交回「这次没有」，让帧泵自己决定下一帧；连续 MaxConsecutiveTimeouts 次都不回才当卡死。
        try
        {
            Task<string?> pending = _stdout.ReadLineAsync();
            if (!pending.Wait(budget))
            {
                Timeouts++;
                Emit(SandboxEventKind.BudgetExceeded, ("stage", "rpc-timeout"), ("method", method), ("budgetMs", budget), ("stderr", Tail()));
                if (++_consecutiveTimeouts >= MaxConsecutiveTimeouts)
                {
                    StopProcess();
                    NoteCrash("timeout-stuck");
                }

                return new Tier2Reply(false, null, $"timeout after {budget}ms");
            }

            _consecutiveTimeouts = 0;

            string? line = pending.Result;
            if (line is null)
            {
                Crashes++;
                Emit(SandboxEventKind.GuestFault, ("stage", "eof"), ("method", method), ("stderr", Tail()));
                StopProcess();
                NoteCrash("eof");
                return new Tier2Reply(false, null, "stdout-closed");
            }

            if (!ChargeWire(Encoding.UTF8.GetByteCount(line) + 1, "out"))
            {
                return new Tier2Reply(false, null, "quota-session-bytes");
            }

            return ParseReply(line, requestId);
        }
        catch (Exception ex)
        {
            Crashes++;
            Emit(SandboxEventKind.GuestFault, ("stage", "read"), ("method", method), ("err", ex.GetType().Name));
            StopProcess();
            NoteCrash("read");
            return new Tier2Reply(false, null, $"read-failed: {ex.GetType().Name}");
        }
    }

    private Tier2Reply ParseReply(string line, string expectedId)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            Crashes++;
            Emit(SandboxEventKind.GuestFault, ("stage", "parse"), ("err", ex.Message), ("line", Clip(line)));
            return new Tier2Reply(false, null, $"bad-json: {ex.Message}");
        }

        if (node is not JsonObject obj)
        {
            return new Tier2Reply(false, null, "reply-not-object");
        }

        // id 必须回显：串行往返一旦错位，之后每一帧都读到上一帧的结果，而症状（画面滞后一帧）远比根因难查。
        string? id = obj["id"]?.GetValue<string>();
        if (!string.Equals(id, expectedId, StringComparison.Ordinal))
        {
            Emit(SandboxEventKind.GuestFault, ("stage", "id-mismatch"), ("expected", expectedId), ("got", id ?? "<null>"));
            return new Tier2Reply(false, null, $"id-mismatch: expected {expectedId}");
        }

        if (obj["error"] is JsonNode error)
        {
            return new Tier2Reply(false, null, error.ToJsonString());
        }

        return new Tier2Reply(true, obj["result"], null);
    }

    private void AppendStderr(string? data)
    {
        if (data is null)
        {
            return;
        }
        // 只留尾部：崩溃时最有用的往往是最后几行，整段 stderr 会撑爆内存。
        _stderrTail.AppendLine(data);
        if (_stderrTail.Length > 4096)
        {
            _stderrTail.Remove(0, _stderrTail.Length - 4096);
        }
    }

    private string Tail()
    {
        string text = _stderrTail.ToString().Trim();
        return text.Length == 0 ? "<empty>" : Clip(text.Replace('\n', ' ').Replace('\r', ' '));
    }

    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "…";

    private void StopProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1000);
            }
        }
        catch
        {
            // 关闭路径上的异常无处可报：进程已经不在了，再说也没有接收方。
        }
        finally
        {
            _stdin = null;
            _stdout = null;
        }
    }

    private void Emit(SandboxEventKind kind, params (string Key, object? Value)[] pairs)
    {
        _sink.Emit(new SandboxEvent(kind, SandboxEventPayload.Build(pairs)));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            if (_stdin is not null && _process is { HasExited: false })
            {
                // 有序关闭：先道别再杀，给子进程一次 flush 的机会。
                var bye = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "shutdown", ["method"] = "shutdown" };
                _stdin.WriteLine(bye.ToJsonString());
                _stdin.Flush();
            }
        }
        catch
        {
            // 同上：关闭路径不报错。
        }

        StopProcess();
        try
        {
            _process?.Dispose();
        }
        catch
        {
            // 句柄可能已被 Kill 收走，Dispose 再抛一次；吞掉。
        }
        _process = null;
        Emit(
            SandboxEventKind.Audit,
            ("tier2", "disposed"),
            ("requests", Requests),
            ("timeouts", Timeouts),
            ("crashes", Crashes),
            ("wireIn", WireInBytes),
            ("wireOut", WireOutBytes),
            ("quota", QuotaExceeded));
    }
}
