// M1 契约 §5（.dsh-code/M1-contract-frozen.md）：能力句柄 = docs/14 §2 的白名单 ID 模型。
// 白名单而非黑名单；安装期一次性声明；每次授予/拒绝记审计。本文件只负责「ID 的合法性」，
// 不负责「是否授予」——授予判定在 CapabilityGate。
// 注意：docs/14 §2 中的 assets.write:cas 与 audio.play 是「明令禁止项」，
// 它们出现在白名单文档里只是为了说明永不发放，因此本枚举不含它们。
using System.Text.RegularExpressions;

namespace PetDock.Core.Sandbox;

/// <summary>
/// 一条能力授权。Id 必须与 docs/14 §2 白名单匹配（含两个带参数的模式）。
/// </summary>
public readonly record struct CapabilityGrant
{
    public string Id { get; }

    public CapabilityGrant(string id)
    {
        if (!CapabilityIds.IsWellKnown(id))
        {
            throw new ArgumentException(
                $"capability id '{id}' is not in the docs/14 §2 whitelist", nameof(id));
        }
        Id = id;
    }

    public override string ToString() => Id;
}

/// <summary>
/// docs/14 §2 能力 ID 全集（M1 冻结版）。两个带参数项用正则表达。
/// </summary>
public static class CapabilityIds
{
    public const string InputMousePosition = "input.mousePosition";
    public const string InputMoveWindow = "input.moveWindow";
    public const string InputClickThrough = "input.clickThrough";
    public const string InputKeyboardHook = "input.keyboardHook";
    public const string AssetsReadCas = "assets.read:cas";
    public const string PersistWriteSelf = "persist.write:self";
    public const string PersistReadSelf = "persist.read:self";
    public const string ScreenChangeDpi = "screen.changeDpi";

    /// <summary>network.fetch:&lt;domain&gt;，域名为小写字母/数字/点/横线。</summary>
    public const string NetworkFetchPrefix = "network.fetch:";

    /// <summary>ipc.tier2:&lt;shim&gt;，shim 为小写字母开头的小写字母/数字/横线。</summary>
    public const string IpcTier2Prefix = "ipc.tier2:";

    private static readonly Regex NetworkFetchRe =
        new(@"^network\.fetch:[a-z0-9.-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IpcTier2Re =
        new(@"^ipc\.tier2:[a-z][a-z0-9-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Fixed = new(StringComparer.Ordinal)
    {
        InputMousePosition,
        InputMoveWindow,
        InputClickThrough,
        InputKeyboardHook,
        AssetsReadCas,
        PersistWriteSelf,
        PersistReadSelf,
        ScreenChangeDpi,
    };

    /// <summary>该 ID 是否属于白名单（固定项或两个模式项之一）。</summary>
    public static bool IsWellKnown(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (Fixed.Contains(id)) return true;
        return NetworkFetchRe.IsMatch(id) || IpcTier2Re.IsMatch(id);
    }

    /// <summary>所有固定项（不含模式项的具体实例）——供审计枚举用。</summary>
    public static IReadOnlyCollection<string> FixedIds => Fixed;
}
