// M2 契约 §1（.dsh-code/M2-contract-frozen.md）：Pet Runtime ABI 的核心数据类型在 core 声明。
// docs/11-pet-runtime-abi.md 是 FROZEN-v1，本文件是它的 C# 绑定；sdk/csharp 只对这里做
// TypeForwardedTo（M1 t6 教训：同名同命名空间重声明 = 两个 CLR 类型 = IsAssignableFrom 恒假）。
//
// 标识符与 docs/11 名字的逐条对照，以及「为什么四个接口里有两个换了名」，
// 写在 .dsh-code/M2-name-map.md（core 之外）—— 本文件不展开它，只给出这几条例外：
//   Point / Rect / Size / Color → PetPoint / PetRect / PetSize / PetColor
//     （System.Drawing 有同名类型，宿主侧的帧缓冲是 GDI+，两份会在同一文件并见）
//   FrameIndex / Ms / ScaleFactor / PetInstanceId → int / int / double / string
//     （docs/11 §2 的记号表已写明它们是 i32 / f64 / string 的字面别名，不另立包装类型）
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace PetDock.Core.PetRuntime;

/// <summary>
/// Pet Runtime ABI 的版本标记（docs/11 §11：版本 = header 中 interface / version）。
/// 破坏性修改 = major，兼容增量 = minor；这条即 D-11b 比对的对象。
/// </summary>
public static class PetRuntimeAbi
{
    /// <summary>docs/11 §11 的 interface 标记值。</summary>
    public const string InterfaceName = "petdock.runtime";

    /// <summary>FROZEN-v1 的 major。</summary>
    public const int Major = 1;

    /// <summary>FROZEN-v1 的 minor。</summary>
    public const int Minor = 0;

    /// <summary>1.0 形式。</summary>
    public static string Version => $"{Major}.{Minor}";
}

/// <summary>docs/11 §2 Point：宠物局部坐标，原点在窗口左上。</summary>
public readonly record struct PetPoint(double X, double Y)
{
    public override string ToString() => $"({Fmt(X)},{Fmt(Y)})";

    /// <summary>定长小数格式化（自检输出用；不依赖本机区域设置）。</summary>
    public static string Fmt(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>docs/11 §2 Rect。</summary>
public readonly record struct PetRect(double X, double Y, double W, double H)
{
    public override string ToString() =>
        $"({PetPoint.Fmt(X)},{PetPoint.Fmt(Y)},{PetPoint.Fmt(W)},{PetPoint.Fmt(H)})";
}

/// <summary>docs/11 §2 Size：逻辑像素（DPI 缩放前）。</summary>
public readonly record struct PetSize(double W, double H)
{
    public override string ToString() => $"{PetPoint.Fmt(W)}x{PetPoint.Fmt(H)}";
}

/// <summary>
/// docs/11 §2 的颜色（r/g/b/a 各 0-255，a=0 即穿透区）。类型名带 Pet 前缀的理由同 PetPoint：
/// System.Drawing 有同名类型，宿主侧两份会在同一文件并见。
/// **M2 无消费者**：帧的像素走位图（不逐像素传色），命中判定走 alpha；本类型在此是为把 docs/11 §2
/// 的词汇表补全 —— ABI 声明过它，插件作者按 docs 写代码时应当能找到它。M3 的托盘/气泡着色会用上。
/// </summary>
public readonly record struct PetColor(int R, int G, int B, int A)
{
    /// <summary>不透明色（a=255）。</summary>
    public static PetColor Opaque(int r, int g, int b) => new(r, g, b, 255);

    public override string ToString() => $"rgba={R},{G},{B},{A}";
}

/// <summary>
/// 接口元数据（docs/11 §3/§4 的 `readonly meta: { id, version }`）。
/// Id 是运行期真值，必须与 docs/11:310-312 逐字一致（见 .dsh-code/M2-name-map.md §2）。
/// </summary>
public readonly record struct InterfaceMeta(string Id, string Version)
{
    public override string ToString() => $"{Id} v{Version}";
}

/// <summary>
/// docs/11 §2.4 PetEvent：总线与状态机共用的最小单元（type / at / payload / trace）。
/// payload 按 docs/11 是 Record&lt;string, Json&gt; —— 这里用 JsonObject（null = 空载荷）。
/// </summary>
public sealed record PetEvent(string Type, long At, JsonObject? Payload = null, string? Trace = null)
{
    /// <summary>只有类型的最小事件（宿主通知类事件用）。</summary>
    public static PetEvent Named(string type) =>
        new(type, System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public override string ToString() => $"{Type}@{At}";
}

/// <summary>docs/11 §2.3 AnimationClip.loop 的三个合法值。</summary>
public enum ClipLoop
{
    /// <summary>once。</summary>
    Once,

    /// <summary>loop。</summary>
    Loop,

    /// <summary>pingpong。</summary>
    PingPong,
}

/// <summary>docs/11 §2.3 命名锚点：名字 + 帧号。</summary>
public readonly record struct ClipAnchor(string Name, int At);

/// <summary>
/// docs/11 §2.3 AnimationClip：目录动画的声明。
/// fps 是名义帧率，实际由 docs/11 §7 的帧预算裁决（宿主从不为了「凑满」而重复持有旧帧）。
/// </summary>
public sealed record AnimationClip(
    string Id,
    int Frames,
    double Fps,
    ClipLoop Loop,
    IReadOnlyList<ClipAnchor>? Anchor = null)
{
    /// <summary>一个周期时长（毫秒）= frames × 1000 / fps；fps ≤ 0 时视为 0。</summary>
    public double PeriodMs => Fps > 0 ? Frames * 1000.0 / Fps : 0.0;

    public override string ToString() =>
        $"{Id} frames={Frames} fps={PetPoint.Fmt(Fps)} loop={Loop}";
}
