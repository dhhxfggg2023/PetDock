// 一只宠物的陪伴数值（0..100）与状态层读写（2026-10-29，P4）。
//
// 数值全部在内存里算，落盘只是把当前值写成键值对：状态层是 per-instance 的缓冲区，
// 提交点由宿主决定（<see cref="ICompanionHost.CommitState"/>）。没有状态层时照常工作，
// 只是重启后回到默认值 —— 这是「没接落盘层」的老实形态，不是假装存住了。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;

namespace PetDock.Care;

internal sealed class CareValues
{
    private const string KeyFed = "care.fed";
    private const string KeyMood = "care.mood";
    private const string KeyVim = "care.vim";
    private const string KeyBond = "care.bond";
    private const string KeySince = "care.since";
    private const string KeyAt = "care.at";

    /// <summary>每分钟的自然消耗（不喂不玩就会慢慢掉）。</summary>
    private const double FedPerMinute = 0.4;
    private const double VimPerMinute = 0.5;

    public double Fed { get; set; } = 70;

    public double Mood { get; set; } = 70;

    /// <summary>体力（睡觉回，玩掉）。</summary>
    public double Vim { get; set; } = 70;

    /// <summary>亲密点（命令累加，按点算等级）。</summary>
    public double Bond { get; set; }

    /// <summary>第一次见到这只宠物的时间（毫秒）。</summary>
    public long Since { get; private set; }

    /// <summary>上次写入状态层的时间（毫秒）；离线折旧从它算起。</summary>
    public long At { get; private set; }

    public static double Clamp(double value) => value < 0 ? 0 : value > 100 ? 100 : value;

    /// <summary>等级只由亲密点决定，1 级起步。</summary>
    public int Level => 1 + (int)(Bond / 50);

    /// <summary>陪伴天数：按首次见到的时间算，当天算第 1 天。</summary>
    public int Days(long now) => Math.Max(1, (int)((now - Since) / 86_400_000L) + 1);

    /// <summary>在线折旧：心情往饱腹与体力的均值靠（饿着又累，心情自然不好）。</summary>
    public void Decay(double minutes)
    {
        if (minutes <= 0)
        {
            return;
        }

        Fed = Clamp(Fed - (FedPerMinute * minutes));
        Vim = Clamp(Vim - (VimPerMinute * minutes));
        double target = (Fed + Vim) / 2;
        Mood = Clamp(Mood + ((target - Mood) * Math.Min(1, 0.3 * minutes)));
    }

    /// <summary>把所有数值收进 0..100（命令加减之后调用）。</summary>
    public void Normalize()
    {
        Fed = Clamp(Fed);
        Mood = Clamp(Mood);
        Vim = Clamp(Vim);
        Bond = Math.Max(0, Bond);
    }

    /// <summary>从状态层取回上次的值；没有就保持默认。</summary>
    public void Load(IPetState? state, long now)
    {
        Since = now;
        At = now;
        if (state is null)
        {
            return;
        }

        Fed = ReadNumber(state, KeyFed, Fed);
        Mood = ReadNumber(state, KeyMood, Mood);
        Vim = ReadNumber(state, KeyVim, Vim);
        Bond = ReadNumber(state, KeyBond, Bond);
        Since = (long)ReadNumber(state, KeySince, now);
        At = (long)ReadNumber(state, KeyAt, now);
        Normalize();
    }

    /// <summary>把当前值写进状态层缓冲（不落盘；落盘由 CommitState 决定）。</summary>
    public void Save(IPetState? state, long now)
    {
        At = now;
        if (state is null)
        {
            return;
        }

        state.Write(new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [KeyFed] = JsonValue.Create(Fed),
            [KeyMood] = JsonValue.Create(Mood),
            [KeyVim] = JsonValue.Create(Vim),
            [KeyBond] = JsonValue.Create(Bond),
            [KeySince] = JsonValue.Create(Since),
            [KeyAt] = JsonValue.Create(At),
        });
    }

    private static double ReadNumber(IPetState state, string key, double fallback)
    {
        JsonNode? node = state.Read(key);
        if (node is not JsonValue)
        {
            return fallback;
        }

        // 不能用 TryGetValue<double>：JsonValue<long> 与 JsonValue<double> 是两种节点，
        // 而状态层是「写 JSON 再读回」—— 整数会被读成 long（"70"），整份状态就静默回到默认值。
        // 这里按 JSON 文本解析，两种节点都认（CultureInfo 固定，不受系统区域影响）。
        string text = node.ToJsonString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? number
            : fallback;
    }
}
