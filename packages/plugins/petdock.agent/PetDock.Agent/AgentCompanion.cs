// 陪伴层：AI / agent 状态（2026-10-29，P4 第三块）。
//
// 数据来源只有本机文件：写状态的一方（IDE 插件、脚本、CI）落一行 JSON，这里读它、展示它。
// 这样不需要新授权面（没有 HTTP、没有凭据），而「桌宠知道 agent 在干什么」这个体验照样成立。
// 文件路径由环境变量 PETDOCK_AGENT_STATE 决定；没设就用 %LOCALAPPDATA%\PetDock\agent-state.json。
//
// 三条诚实规则：
//   1. 文件不在 =「未发现状态文件」，不是「空闲」；
//   2. 内容读不懂 =「状态文件读不出来」，不是说它空闲；
//   3. 跑着的任务超过 2 分钟没更新 = 加一句「可能已停」—— 状态是写状态那一方给的，我们只负责别让它显得比实际更新。
using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using PetDock.Core.PetRuntime;

namespace PetDock.Agent;

public sealed class AgentCompanion : IPetCompanion
{
    /// <summary>轮询间隔：查一次 mtime 而已，1 秒足够；每分钟读一次会让「刚跑完」晚十几秒才出现。</summary>
    private const int PollMs = 1000;

    /// <summary>running 超过这么久没更新，就在状态后面加「可能已停」。</summary>
    private const int StaleAfterMs = 120_000;

    private readonly object _gate = new();
    private AgentTexts _texts = AgentTexts.Zh;
    private ICompanionHost? _host;
    private string? _path;
    private AgentSnapshot _snapshot = AgentSnapshot.MissingOne;
    private bool _hasRead;
    private DateTime _lastWrite = DateTime.MinValue;
    private int _pollMs;
    private string? _pending;

    public InterfaceMeta Meta => new("petdock.agent", "1.0.0");

    public Task InitAsync(CompanionContext context)
    {
        lock (_gate)
        {
            _host = context.Host;
            _texts = AgentTexts.For(context.Host.Language);
            _path = ResolvePath();
            Reload(announce: false);
        }

        return Task.CompletedTask;
    }

    public CompanionTick Tick(int dtMs, WorldSnapshot world)
    {
        if (dtMs <= 0)
        {
            return CompanionTick.None;
        }

        lock (_gate)
        {
            _pollMs += dtMs;
            if (_pollMs < PollMs)
            {
                return CompanionTick.None;
            }

            _pollMs = 0;
            Reload(announce: true);
            string? say = _pending;
            _pending = null;
            return say is null ? CompanionTick.None : CompanionTick.Speak(say);
        }
    }

    public void OnEvent(PetEvent evt)
    {
        // 本层只认文件，不消费总线事件。
    }

    public void OnGesture(CompanionGesture gesture)
    {
        // 点按的通用台词由宿主说；这里没有额外动作。
    }

    public string? Invoke(string commandId)
    {
        if (!string.Equals(commandId, "refresh", StringComparison.Ordinal))
        {
            return null;
        }

        lock (_gate)
        {
            _hasRead = false;
            Reload(announce: false);
            return StatusLine(_snapshot, DateTime.UtcNow);
        }
    }

    public CompanionReport Report
    {
        get
        {
            lock (_gate)
            {
                _texts = AgentTexts.For(_host?.Language);
                DateTime now = DateTime.UtcNow;
                return new CompanionReport(_texts.Title, StatusLine(_snapshot, now), DetailLine(_snapshot, now))
                {
                    Commands = new[] { new CompanionCommand("refresh", _texts.Refresh) },
                };
            }
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// 读一次文件。只有 mtime 变了才真的解析（每秒解析一个小 JSON 也不贵，但没必要）。
    /// 状态从「跑着」变成 done/failed 时留一句话给 Tick 说出去 —— 用户不一定盯着面板。
    /// </summary>
    private void Reload(bool announce)
    {
        DateTime write = DateTime.MinValue;
        string? path = _path;
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                if (File.Exists(path))
                {
                    write = File.GetLastWriteTimeUtc(path);
                }
            }
            catch (Exception)
            {
                write = DateTime.MinValue;
            }
        }

        if (_hasRead && write == _lastWrite)
        {
            return;
        }

        _hasRead = true;
        _lastWrite = write;
        AgentSnapshot next = AgentSnapshot.Read(path);
        if (announce && next.Terminal && !_snapshot.Terminal
            && !string.Equals(next.State, _snapshot.State, StringComparison.Ordinal))
        {
            _pending = string.Equals(next.State, "failed", StringComparison.OrdinalIgnoreCase)
                ? _texts.FailedSay
                : _texts.DoneSay;
        }

        _snapshot = next;
    }

    private string StatusLine(AgentSnapshot snapshot, DateTime now)
    {
        if (snapshot.Missing)
        {
            return _texts.Missing;
        }

        if (snapshot.Unreadable)
        {
            return _texts.Unreadable;
        }

        string label = StateLabel(snapshot);
        if (!string.IsNullOrWhiteSpace(snapshot.Task))
        {
            label = label + " · " + snapshot.Task!.Trim();
        }

        if (snapshot.Running && IsStale(snapshot, now))
        {
            label = label + _texts.Stale;
        }

        return label;
    }

    private string DetailLine(AgentSnapshot snapshot, DateTime now)
    {
        if (snapshot.Missing || snapshot.Unreadable)
        {
            return string.IsNullOrWhiteSpace(_path) ? _texts.Hint : _path!;
        }

        var parts = new List<string>(3);
        if (snapshot.Progress is double progress)
        {
            double percent = progress <= 1 ? progress * 100 : progress;
            parts.Add(string.Format(
                CultureInfo.InvariantCulture, _texts.Progress, Math.Round(percent, 0, MidpointRounding.AwayFromZero)));
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Detail))
        {
            parts.Add(snapshot.Detail!.Trim());
        }

        if (snapshot.At is DateTime at)
        {
            parts.Add(Age(at, now));
        }

        if (parts.Count == 0)
        {
            parts.Add(_texts.Hint);
        }

        return string.Join(" · ", parts);
    }

    private string StateLabel(AgentSnapshot snapshot)
    {
        string state = snapshot.State;
        if (string.Equals(state, "idle", StringComparison.OrdinalIgnoreCase))
        {
            return _texts.StateIdle;
        }

        if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
        {
            return _texts.StateRunning;
        }

        if (string.Equals(state, "done", StringComparison.OrdinalIgnoreCase))
        {
            return _texts.StateDone;
        }

        if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return _texts.StateFailed;
        }

        // 不认识的 state 原样显示：替它翻译成「空闲」就是在编造含义。
        return snapshot.RawState ?? state;
    }

    private static bool IsStale(AgentSnapshot snapshot, DateTime now) =>
        snapshot.At is DateTime at && (now - at).TotalMilliseconds > StaleAfterMs;

    private string Age(DateTime at, DateTime now)
    {
        double seconds = Math.Max(0, (now - at).TotalSeconds);
        if (seconds < 60)
        {
            return string.Format(CultureInfo.InvariantCulture, _texts.AgeSeconds, (int)seconds);
        }

        if (seconds < 3600)
        {
            return string.Format(CultureInfo.InvariantCulture, _texts.AgeMinutes, (int)(seconds / 60));
        }

        return string.Format(CultureInfo.InvariantCulture, _texts.AgeHours, (int)(seconds / 3600));
    }

    private static string? ResolvePath()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("PETDOCK_AGENT_STATE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PetDock");
        return Path.Combine(dir, "agent-state.json");
    }
}
