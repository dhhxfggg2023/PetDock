// RFC-10 §2：Tier-2 进程外沙箱（docs/14 §1：独立进程 + stdio JSON-RPC + 会话 quota/timeout）。
// 与 PluginSandbox 同形：new → Start → 任意次 Tick → Dispose。
//
// 范围：只把**进程边界**做对 —— spawn、行分隔 JSON-RPC、每请求超时、崩溃检测、有序关闭。
// docs/11 的四个 ABI 接口要跨进程实现还需要帧/命中图/事件包的序列化契约，那是独立里程碑的量，
// 这里不假装已经做到。判据见 tests/m7 的 Tier-2 段。
//
// P-05（docs/01 §5）：公共方法一律不外抛，失败只落成沙箱事件。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PetDock.Core.Sandbox;

/// <summary>一次请求结果。失败时 <see cref="Error"/> 非空。</summary>
public readonly record struct Tier2Reply(bool Ok, JsonNode? Result, string? Error);

/// <summary>单客体进程外沙箱。单线程同步：一帧一次往返，不并发。</summary>
public sealed class Tier2Sandbox : IDisposable
{
    /// <summary>握手方法名；不回它就装载失败。</summary>
    public const string HandshakeMethod = "ping";

    private readonly ISandboxEventSink _sink;
    private readonly string _command;
    private readonly string _arguments;
    private readonly string _workingDir;
    private readonly int _requestTimeoutMs;
    private readonly int _frameBudgetMs;
    private readonly StringBuilder _stderrTail = new();

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
        int frameBudgetMs = 8)
    {
        Gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _arguments = arguments;
        _workingDir = workingDir;
        _requestTimeoutMs = requestTimeoutMs > 0 ? requestTimeoutMs : 2000;
        _frameBudgetMs = frameBudgetMs > 0 ? frameBudgetMs : 8;
    }

    public CapabilityGate Gate { get; }

    public bool IsLoaded => !_disposed && _process is { HasExited: false };

    public long Requests { get; private set; }

    public long Timeouts { get; private set; }

    public long Crashes { get; private set; }

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

            Emit(SandboxEventKind.Audit, ("tier2", "started"), ("pid", process.Id), ("rpc", HandshakeMethod));
            return true;
        }
        catch (Exception ex)
        {
            Emit(SandboxEventKind.GuestFault, ("stage", "start"), ("err", ex.GetType().Name), ("msg", ex.Message));
            StopProcess();
            return false;
        }
    }

    /// <summary>驱动一帧；返回子进程是否仍在服务。</summary>
    public bool Tick(int dtMs)
    {
        if (!IsLoaded)
        {
            return false;
        }

        Tier2Reply reply = Call("tick", new JsonObject { ["dt"] = dtMs }, _frameBudgetMs);
        if (!reply.Ok)
        {
            Emit(SandboxEventKind.BudgetExceeded, ("stage", "tick"), ("err", reply.Error ?? "<none>"));
            return false;
        }

        return IsLoaded;
    }

    /// <summary>一次 JSON-RPC 往返：写一行 JSON，读一行 JSON，带超时。</summary>
    public Tier2Reply Call(string method, JsonObject parameters, int? timeoutMs = null)
    {
        if (_stdin is null || _stdout is null || _process is null || _process.HasExited)
        {
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
            _stdin.WriteLine(request.ToJsonString());
            _stdin.Flush();
        }
        catch (Exception ex)
        {
            Crashes++;
            Emit(SandboxEventKind.GuestFault, ("stage", "write"), ("method", method), ("err", ex.GetType().Name));
            return new Tier2Reply(false, null, $"write-failed: {ex.GetType().Name}");
        }

        // ReadLine 是阻塞的，所以异步读 + 有界等待；超时就杀掉 —— 不回应 tick 的子进程
        // 不能留着继续占资源（docs/14 §5 的越限后果是卸载）。
        try
        {
            Task<string?> pending = _stdout.ReadLineAsync();
            if (!pending.Wait(budget))
            {
                Timeouts++;
                Emit(SandboxEventKind.BudgetExceeded, ("stage", "rpc-timeout"), ("method", method), ("budgetMs", budget), ("stderr", Tail()));
                StopProcess();
                return new Tier2Reply(false, null, $"timeout after {budget}ms");
            }

            string? line = pending.Result;
            if (line is null)
            {
                Crashes++;
                Emit(SandboxEventKind.GuestFault, ("stage", "eof"), ("method", method), ("stderr", Tail()));
                StopProcess();
                return new Tier2Reply(false, null, "stdout-closed");
            }

            return ParseReply(line, requestId);
        }
        catch (Exception ex)
        {
            Crashes++;
            Emit(SandboxEventKind.GuestFault, ("stage", "read"), ("method", method), ("err", ex.GetType().Name));
            StopProcess();
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

        // id 必须回显：串行的请求/应答一旦错位，之后每一帧都读到上一帧的结果，
        // 而症状（画面滞后一帧）远比根因难查。这里显式拒绝。
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
        Emit(SandboxEventKind.Audit, ("tier2", "disposed"), ("requests", Requests), ("timeouts", Timeouts), ("crashes", Crashes));
    }
}
