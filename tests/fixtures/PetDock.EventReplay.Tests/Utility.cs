// 重放测试的公共小工具：规范行（canonical lines）、sha256、易变字段的抹除口径、Seq。
//
// §8.3 的确定性命据要求「同一夹具连跑两次，规范行 sha256 相等」。这里把两件事**分开**实现，
// 因为它们是两个不同的断言，合并就会变成同义反复：
//   1. **抹除**（<see cref="Canonicalizer"/>）：把易变字段删掉。被抹掉的字段名必须**逐个列出**
//      （§8.3「逐个夹具显式列出被抹掉的字段名」），不许含糊说「去掉了耗时」；
//   2. **相异**（<see cref="ReplayRun.RawText"/>）：抹除前的原始行**必须真的不同**，否则
//      「两次相等」是同义反复。Replay.VolatileStripped 这条判据断言的就是 raw 不同、canonical 相同。
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace PetDock.EventReplay.Tests;

/// <summary>序列小工具（避免在静态初始化器里写 LINQ 链）。</summary>
internal static class Seq
{
    public static TOut[] Map<TIn, TOut>(IReadOnlyList<TIn> source, Func<TIn, TOut> pick)
    {
        var mapped = new TOut[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            mapped[i] = pick(source[i]);
        }

        return mapped;
    }

    public static string[] Strings(IReadOnlyList<string> source) => Map(source, s => s);

    public static string Join(IReadOnlyList<string> source, string separator) =>
        string.Join(separator, Strings(source));
}

/// <summary>一次重放的全部输出：原始行（含本机耗时）+ 规范行（抹除后）。</summary>
public sealed class ReplayRun
{
    public ReplayRun(
        string fixtureName,
        IReadOnlyList<string> rawLines,
        IReadOnlyList<string> canonicalLines,
        IReadOnlyList<string> strippedFields)
    {
        FixtureName = fixtureName;
        RawLines = rawLines;
        CanonicalLines = canonicalLines;
        StrippedFields = strippedFields;
        RawText = string.Join("\n", rawLines);
        CanonicalText = string.Join("\n", canonicalLines);
        RawSha = Sha256Text.Of(RawText);
        CanonicalSha = Sha256Text.Of(CanonicalText);
    }

    public string FixtureName { get; }

    /// <summary>抹除前的行（含 elapsed= 这类本机耗时）。</summary>
    public IReadOnlyList<string> RawLines { get; }

    /// <summary>抹除后的行（判据比较的对象）。</summary>
    public IReadOnlyList<string> CanonicalLines { get; }

    /// <summary>本次实际被抹掉的字段名（逐个列出，供 §8.3 的显式清单）。</summary>
    public IReadOnlyList<string> StrippedFields { get; }

    public string RawText { get; }

    public string CanonicalText { get; }

    public string RawSha { get; }

    public string CanonicalSha { get; }

    /// <summary>规范行数（Replay.NonEmpty 判它 ≥ 8，防「两次都空」的假绿）。</summary>
    public int CanonicalLineCount => CanonicalLines.Count;

    public override string ToString() =>
        $"fixture={FixtureName} lines={CanonicalLineCount} sha={CanonicalSha}";
}

/// <summary>两次重放的判决。</summary>
public sealed record DeterminismVerdict(
    string FixtureName,
    string Run1Sha,
    string Run2Sha,
    bool BytesEqual,
    bool RawDiffers,
    int LineCount,
    bool NonEmpty)
{
    /// <summary>§8.3 的完整判据：规范行相等 **且** 非空。
    /// <see cref="RawDiffers"/> 不参与 Pass —— 它是**反向自证**（证明抹除器在干活、
    /// 原始文本真的会变），由 Replay.VolatileStripped 单独判。参见 N5 元判据。</summary>
    public bool Pass => BytesEqual && NonEmpty;

    public string Detail =>
        $"fixture={FixtureName} run1=sha256:{Run1Sha} run2=sha256:{Run2Sha}"
        + $" bytes-equal={Lower(BytesEqual)} raw-differs={Lower(RawDiffers)}"
        + $" lines={LineCount} nonempty={Lower(NonEmpty)}";

    /// <summary>判据自己的那一行（与 t1 §8.1 的输出形态逐字同构）。
    /// 验收③要求「贴红的那一次原文」—— 这条属性就是那个原文，负例把它照原样打出来。</summary>
    public string RenderedLine =>
        $"EVENT-CHECK Replay.Deterministic {(Pass ? "PASS" : "FAIL")} {Detail}";

    private static string Lower(bool value) => value ? "true" : "false";
}

/// <summary>易变字段的抹除器：只删「名字列在这里」的那些，别的一个不动。</summary>
public static class Canonicalizer
{
    /// <summary>
    /// 被抹掉的字段名（§8.3：必须显式列出）。抹除按 **`名字=` 前缀** 做，不是正则扫全文 ——
    /// 前缀形态保证不会误删 payload 里的同名子串。
    ///
    /// 两条必须区分清楚（本会话实测踩过）：
    ///   * `elapsed=` —— 本机 Stopwatch 耗时，唯一**真的会抖**的东西；
    ///   * `run=`     —— 本进程内的重放序号，**确定性**地每次不同（不靠计时抖动）。B）
    /// 两条一起才是「原始文本必不同」的完整保证。而 `ticks=`（PetInstance.Ticks）**不**在这里：
    /// 它是确定性的可观测计数，抹掉它等于少了一条判据（踩过一次：把它当 Stopwatch 计数抹了）。
    /// </summary>
    public static readonly string[] StrippedFieldNames =
    {
        "elapsed=",  // 本机 Stopwatch 耗时（会抖）
        "run=",      // 重放序号（确定性相异来源）
        "wall-",     // 墙钟读数
        "hwnd=",     // Win32 窗口句柄（每次运行都不同）
    };

    /// <summary>§8.3 逐字要求保留的字段（clip / position / verdict / count → 抹过度会变同义反复）。</summary>
    public static readonly string[] RetainedFieldNames =
    {
        "clip=", "position=", "verdict=", "count=", "type=", "kinds=", "seq=", "tod=",
        "state=", "reason=", "capability=", "eventType=", "order=", "world=", "dispatched=",
        "ticks=", "logged=", "directives=", "frames=", "forced-cancel=",
    };

    public static string[] Canonicalize(IEnumerable<string> lines)
    {
        var canonical = new List<string>();
        foreach (string line in lines)
        {
            canonical.Add(StripHexPointers(Strip(line)));
        }

        return canonical.ToArray();
    }

    /// <summary>抹掉一个 token 之后到下一个空格（或行尾）的全部内容。</summary>
    public static string Strip(string line)
    {
        string result = line;
        foreach (string field in StrippedFieldNames)
        {
            int index = result.IndexOf(field, StringComparison.Ordinal);
            while (index >= 0)
            {
                int end = result.IndexOf(' ', index);
                if (end < 0)
                {
                    end = result.Length;
                }

                result = result.Remove(index, end - index);
                index = result.IndexOf(field, StringComparison.Ordinal);
            }
        }

        return result.TrimEnd();
    }

    /// <summary>0x… 十六进制指针（§8.3「任何指针/句柄」）。单独一步，因为它没有 `名字=` 前缀。</summary>
    public static string StripHexPointers(string line)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            if (i + 2 < line.Length && line[i] == '0' && (line[i + 1] == 'x' || line[i + 1] == 'X'))
            {
                int end = i + 2;
                while (end < line.Length && Uri.IsHexDigit(line[end]))
                {
                    end++;
                }

                builder.Append("0xHANDLE");
                i = end - 1;
                continue;
            }

            builder.Append(line[i]);
        }

        return builder.ToString();
    }
}

/// <summary>sha256 的十六进制小写（判据输出里逐字给出，便于两次比对与人工复核）。</summary>
public static class Sha256Text
{
    /// <summary>取前 16 个十六进制字符 —— 契约 §8.1 的 `sha256:{h}` 只要可判定，不需要 64 字符噪声。</summary>
    public static string Of(string text)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var builder = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
        {
            builder.Append(digest[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

/// <summary>本机耗时的**唯一**读法（一个地方，方便 t5 grep 出「驱动里只有这一处读墙钟」，
/// 且它只用于证明抹除器在干活，绝不进判据的比较对象）。</summary>
public static class Volatile
{
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>本进程已跑的毫秒数。</summary>
    public static double ElapsedMs => Clock.Elapsed.TotalMilliseconds;
}
