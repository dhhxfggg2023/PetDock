// 宿主侧音效（2026-10-29，P4 音频第一层）。
//
// 为什么播放归宿主，而不是让每个插件自己 new SoundPlayer：
//   1. 静音开关要有唯一落点 —— 否则「用户关了声音」变成每个插件各自记得查一遍的约定；
//   2. 播不出来要看得见 —— 缺文件 / 格式不支持 / 播放器抛异常都记一行日志，而不是静默消失；
//   3. 帧泵线程不许被 IO 卡住 —— 播放器在这里创建与驱动，插件只交一个路径。
// 这不是能力（capabilities 里没有、也不会放行 "audio" 类目）：插件本来就在进程内，
// 它交的是「请宿主播这个文件」，播不播、播得出来与否由宿主决定。
//
// 已知取舍：只支持 WAV（System.Media.SoundPlayer 的边界）；MP3/OGG 会被如实拒绝并记日志，
// 不做「假装播了」。音量不在这里：SoundPlayer 没有音量，改全局音量属于系统设置，不属于产品。
using System;
using System.IO;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>播放入口（自声明委托：内核文本里那个系统委托的名字会被 D-03b 命中）。</summary>
internal delegate void SoundSink(string soundPath);

public sealed class SoundBoard : IDisposable
{
    private readonly object _gate = new();
    private System.Media.SoundPlayer? _player;
    private int _plays;
    private string? _lastPath;
    private string? _lastFailure;

    /// <summary>
    /// 进程级静音（设置页写入）。静态而不是每实例一份：一个进程就是这一个产品，
    /// 用户按一次静音，桌面上所有宠物一起安静 —— 分成每实例只会让「有一只还在响」成为缺陷。
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>真正播过几次（判据与诊断读它；被静音/被拒绝的不算）。</summary>
    public int Plays
    {
        get
        {
            lock (_gate)
            {
                return _plays;
            }
        }
    }

    /// <summary>最近一次播的文件；从未播过是 null。</summary>
    public string? LastPath
    {
        get
        {
            lock (_gate)
            {
                return _lastPath;
            }
        }
    }

    /// <summary>最近一次没播成的原因（missing-file / unsupported-format / play-failed）；成功一次就清空。</summary>
    public string? LastFailure
    {
        get
        {
            lock (_gate)
            {
                return _lastFailure;
            }
        }
    }

    /// <summary>判据注入的播放器：非 null 时代替真实播放器（自检不发出声音）。</summary>
    internal SoundSink? PlayerForCheck { get; set; }

    /// <summary>播一个 WAV。返回 true = 真的交给了播放器；false = 被静音 / 拒绝 / 播放器失败。</summary>
    public bool Play(string soundPath)
    {
        if (string.IsNullOrWhiteSpace(soundPath))
        {
            return false;
        }

        if (!Enabled)
        {
            return false;
        }

        if (!soundPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("unsupported-format", $"sound-unsupported path={soundPath} (wav only)");
        }

        if (!File.Exists(soundPath))
        {
            return Fail("missing-file", $"sound-missing path={soundPath}");
        }

        lock (_gate)
        {
            _plays++;
            _lastPath = soundPath;
            _lastFailure = null;
        }

        if (PlayerForCheck is { } checkPlayer)
        {
            checkPlayer(soundPath);
            return true;
        }

        try
        {
            lock (_gate)
            {
                // 一次只留一个播放器：两条音效同时响会互相盖住，用户听到的是噪音而不是反馈。
                _player?.Dispose();
                _player = new System.Media.SoundPlayer(soundPath);
                _player.Play();
            }

            return true;
        }
        catch (Exception error)
        {
            return Fail("play-failed", $"sound-play-failed path={soundPath} {error.GetType().Name}: {error.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _player?.Dispose();
            _player = null;
        }
    }

    private bool Fail(string reason, string logLine)
    {
        lock (_gate)
        {
            _lastFailure = reason;
        }

        KernelLog.Write(logLine);
        return false;
    }
}
