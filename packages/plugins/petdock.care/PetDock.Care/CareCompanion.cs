// 陪伴层：喂养 · 心情 · 成长（2026-10-29，P4 插件线第一块）。
//
// 玩法只有三条命令（喂食 / 玩耍 / 休息）与自然消耗；数字全部由本插件解释，宿主只负责
// 每帧给一次 tick、把 Report 摆到面板、把按钮转回来。这样以后接音频或 agent 状态时，
// 内核不需要再为玩法改一次。
//
// 线程：Tick / OnGesture 在帧循环线程，Invoke 在 UI 线程，Report 任意线程可读。
// 所以数值统一在 _gate 里改，Report 交出去的是新建的字符串快照（不是可变对象本身）。
using System;
using System.Globalization;
using System.Threading.Tasks;
using PetDock.Core.PetRuntime;

namespace PetDock.Care;

public sealed class CareCompanion : IPetCompanion
{
    private const int MinuteMs = 60_000;

    /// <summary>落盘节流：帧泵上每 10 分钟提交一次（命令与下线各提交一次，见 Invoke / DisposeAsync）。</summary>
    private const int CommitEveryMs = 10 * MinuteMs;

    /// <summary>离线最多补 12 小时的消耗：装完一周回来不该一睁眼就是 0。</summary>
    private const double MaxOfflineMinutes = 720;

    private readonly object _gate = new();
    private readonly CareValues _values = new();
    private CareTexts _texts = CareTexts.Zh;
    private IPetState? _state;
    private ICompanionHost? _host;
    private int _accruedMs;
    private int _commitMs;
    private int _pokes;
    private bool _lowFedSaid;
    private bool _lowVimSaid;

    public InterfaceMeta Meta => new("petdock.care", "1.0.0");

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Task InitAsync(CompanionContext context)
    {
        long now = Now;
        lock (_gate)
        {
            _state = context.State;
            _host = context.Host;
            _texts = CareTexts.For(context.Host.Language);
            _values.Load(context.State, now);

            // 离线折旧从上次落盘算起；没有落盘过（At == now）时补零。
            double minutes = Math.Min(MaxOfflineMinutes, Math.Max(0, (now - _values.At) / 60_000.0));
            _values.Decay(minutes);
            _values.Save(_state, now);
        }

        return Task.CompletedTask;
    }

    public CompanionTick Tick(int dtMs, WorldSnapshot world)
    {
        if (dtMs <= 0)
        {
            return CompanionTick.None;
        }

        string? say = null;
        bool commit = false;
        lock (_gate)
        {
            _accruedMs += dtMs;
            int minutes = 0;
            while (_accruedMs >= MinuteMs)
            {
                _accruedMs -= MinuteMs;
                minutes++;
            }

            _values.Decay(minutes);

            // 两种提醒各只说一次：反复喊饿会把气泡变成噪音。
            if (!_lowFedSaid && _values.Fed < 20)
            {
                _lowFedSaid = true;
                say = _texts.LowFedSay;
            }
            else if (!_lowVimSaid && _values.Vim < 15)
            {
                _lowVimSaid = true;
                say = _texts.LowVimSay;
            }

            _commitMs += dtMs;
            if (_commitMs >= CommitEveryMs)
            {
                _commitMs = 0;
                _values.Save(_state, Now);
                commit = true;
            }
        }

        if (commit)
        {
            _host?.CommitState();
        }

        return say is null ? CompanionTick.None : CompanionTick.Speak(say);
    }

    public void OnEvent(PetEvent evt)
    {
        // 本层暂不消费总线事件：陪伴数值只由时间、桌面动作与面板命令驱动。
    }

    public void OnGesture(CompanionGesture gesture)
    {
        string? say = null;
        lock (_gate)
        {
            switch (gesture)
            {
                case CompanionGesture.Poke:
                    _values.Mood = CareValues.Clamp(_values.Mood + 1);
                    _pokes++;
                    if (_pokes % 5 == 0)
                    {
                        say = _texts.PokeSay;
                    }

                    _values.Save(_state, Now);
                    break;
                case CompanionGesture.Land:
                    _values.Vim = CareValues.Clamp(_values.Vim - 1);
                    _values.Save(_state, Now);
                    break;
                default:
                    break;
            }
        }

        // 动作不落盘（每戳一下写一次盘太重）；它只改缓冲，等到命令或节流提交再写。
        if (say is not null)
        {
            _host?.Say(say);
        }
    }

    public string? Invoke(string commandId)
    {
        string? say;
        lock (_gate)
        {
            switch (commandId)
            {
                case "feed":
                    _values.Fed += 25;
                    _values.Vim -= 3;
                    _values.Mood += 4;
                    _values.Bond += 2;
                    say = _texts.FeedSay;
                    break;
                case "play":
                    _values.Mood += 15;
                    _values.Vim -= 10;
                    _values.Fed -= 4;
                    _values.Bond += 3;
                    say = _texts.PlaySay;
                    break;
                case "rest":
                    _values.Vim += 25;
                    _values.Mood += 2;
                    _values.Fed -= 3;
                    _values.Bond += 1;
                    say = _texts.RestSay;
                    break;
                default:
                    return null;
            }

            _values.Normalize();
            _values.Save(_state, Now);
        }

        _host?.CommitState();
        return say;
    }

    public CompanionReport Report
    {
        get
        {
            lock (_gate)
            {
                _texts = CareTexts.For(_host?.Language);
                long now = Now;
                return new CompanionReport(_texts.Title, StatusLine(), DetailLine(now))
                {
                    Commands = new[]
                    {
                        new CompanionCommand("feed", _texts.FeedLabel),
                        new CompanionCommand("play", _texts.PlayLabel),
                        new CompanionCommand("rest", _texts.RestLabel),
                    },
                };
            }
        }
    }

    public Task DisposeAsync()
    {
        lock (_gate)
        {
            _values.Save(_state, Now);
        }

        _host?.CommitState();
        return Task.CompletedTask;
    }

    private string StatusLine() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} · {2} {3} · {4} {5}",
            _texts.FedLabel,
            Round(_values.Fed),
            _texts.MoodLabel,
            Round(_values.Mood),
            _texts.VimLabel,
            Round(_values.Vim));

    private string DetailLine(long now) =>
        string.Format(CultureInfo.InvariantCulture, _texts.LevelFormat, _values.Level)
        + " · "
        + string.Format(CultureInfo.InvariantCulture, _texts.DaysFormat, _values.Days(now));

    private static string Round(double value) =>
        Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
}
