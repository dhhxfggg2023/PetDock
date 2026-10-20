// 授权询问契约（RFC-13 §2.1/§2.2）：宿主（问）、UI（答）、自检（假答）三方共用的唯一一套类型。
// 为什么单独一个文件：装载期授权同时被三方引用，各自长一套类型就会变成三个能力所有者。
// 边界（docs/14 §2「一次断言：安装时一次性提交能力清单；不允许补申请」）：
//   授予只发生在装载期；运行期没有补授路径，运行期能做的只有收窄（撤销）。
// 拒绝的记忆范围：拒绝**总是**被记住（docs/18 §3「拒绝后该插件不允许重试同一能力」）；
// 授予只在「允许（记住这个决定）」下被记住（「仅本次」下次装载再问）。
using System;
using System.Collections.Generic;
using System.Linq;

namespace PetDock.Hosting;

/// <summary>一次回答的效力范围（docs/18 §3 的三个按钮）。</summary>
public enum ConsentScope
{
    /// <summary>「允许（记住这个决定）」：本会话内不再问。</summary>
    Session,

    /// <summary>「仅本次」：下次装载再问。</summary>
    Once,
}

/// <summary>清单里的一项能力及其既往状态。</summary>
/// <param name="CapabilityId">docs/14 §2 的能力 ID。</param>
/// <param name="PreGranted">此前已授予 —— 默认勾选。</param>
/// <param name="Denied">此前已拒绝 —— 置灰且不可再勾（docs/18 §3）。</param>
public sealed record ConsentItem(string CapabilityId, bool PreGranted, bool Denied);

/// <summary>一次授权询问的完整内容（弹窗按它渲染）。</summary>
public sealed record ConsentRequest(
    string PluginId,
    string PluginName,
    int Tier,
    IReadOnlyList<ConsentItem> Items,
    bool IsReview)
{
    /// <summary>可勾选项：此前没被拒绝过的能力。</summary>
    public IEnumerable<ConsentItem> Askable => Items.Where(item => !item.Denied);

    /// <summary>此前被拒绝的能力（弹窗里置灰）。</summary>
    public IEnumerable<ConsentItem> Refused => Items.Where(item => item.Denied);

    /// <summary>本次可以授予的能力全集（未拒绝的那些）。</summary>
    public IReadOnlyList<string> AskableIds =>
        Askable.Select(item => item.CapabilityId).ToArray();
}

/// <summary>用户对一次询问的回答。</summary>
/// <param name="Scope">效力范围。</param>
/// <param name="Granted">实际授予的能力 ID 集合；被拒绝的项不在其中。</param>
public sealed record ConsentAnswer(ConsentScope Scope, IReadOnlyList<string> Granted)
{
    /// <summary>全拒（等价于逐项全不勾，但更快）。</summary>
    public static ConsentAnswer Reject { get; } =
        new(ConsentScope.Session, Array.Empty<string>());
}

/// <summary>
/// 授权端口：宿主唯一的能力授予决定点（RFC-13 §2.1）。
/// 端口为 null 时宿主保持旧行为「声明集全授」——自检与无人值守场景因此不被弹窗打断。
/// </summary>
public interface ICapabilityConsentPort
{
    /// <summary>
    /// 询问一次授权。实现必须在 UI 线程弹窗并阻塞到用户回答。
    /// 返回 null 表示没有回答 —— 按拒绝处理（默认拒绝比默认允许安全）。
    /// </summary>
    ConsentAnswer? Ask(ConsentRequest request);
}

/// <summary>
/// 会话内的授权记忆（RFC-13 §2.1）。
/// 线程安全：装载在宿主调度线程，复核在 UI 线程，两边都会碰它。
/// 跨进程持久化不在本文件（那属 docs/17 存储层，docs/50:78 已登记为遗留）。
/// </summary>
public sealed class ConsentMemory
{
    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<string>> _granted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _denied = new(StringComparer.Ordinal);

    /// <summary>某插件已记住的授予集合（会话内）。</summary>
    public IReadOnlyCollection<string> Granted(string pluginId)
    {
        lock (_lock)
        {
            return _granted.TryGetValue(pluginId, out HashSet<string>? set)
                ? set.ToArray()
                : Array.Empty<string>();
        }
    }

    /// <summary>某插件已记住的拒绝集合（会话内，永不遗忘）。</summary>
    public IReadOnlyCollection<string> Denied(string pluginId)
    {
        lock (_lock)
        {
            return _denied.TryGetValue(pluginId, out HashSet<string>? set)
                ? set.ToArray()
                : Array.Empty<string>();
        }
    }

    /// <summary>该能力是否已被拒绝（拒绝后不再询问，docs/18 §3）。</summary>
    public bool IsDenied(string pluginId, string capabilityId)
    {
        lock (_lock)
        {
            return _denied.TryGetValue(pluginId, out HashSet<string>? set)
                && set.Contains(capabilityId);
        }
    }

    /// <summary>
    /// 落账一次回答。
    /// <paramref name="rememberGrants"/>：true = 「允许（记住这个决定）」，授予进记忆；
    /// false = 「仅本次」，授予不记（下次装载再问）。
    /// 拒绝**总是**进记忆，与范围无关 —— docs/18 §3 的拒绝规则是无条件的。
    /// </summary>
    public void Remember(
        string pluginId,
        IEnumerable<string> granted,
        IEnumerable<string> denied,
        bool rememberGrants)
    {
        lock (_lock)
        {
            if (!_denied.TryGetValue(pluginId, out HashSet<string>? deniedSet))
            {
                deniedSet = new HashSet<string>(StringComparer.Ordinal);
                _denied[pluginId] = deniedSet;
            }

            foreach (string id in denied)
            {
                deniedSet.Add(id);
            }

            if (!rememberGrants)
            {
                return;
            }

            if (!_granted.TryGetValue(pluginId, out HashSet<string>? grantedSet))
            {
                grantedSet = new HashSet<string>(StringComparer.Ordinal);
                _granted[pluginId] = grantedSet;
            }

            foreach (string id in granted)
            {
                grantedSet.Add(id);
            }
        }
    }

    /// <summary>
    /// 收窄一次记忆（插件页复核用）：把不再保留的能力从授予记忆里去掉，并记入拒绝集。
    /// 运行期不能新增授予（docs/14 §2 一次断言），所以这里只减不加。
    /// 方法名的取舍见 .dsh-code/ 下的 D-03b 对照表：原名会让调用点的成员访问形式
    /// 命中黑名单里的文件扩展名条目。
    /// </summary>
    public void Restrict(string pluginId, IEnumerable<string> kept)
    {
        var keepSet = new HashSet<string>(kept, StringComparer.Ordinal);
        lock (_lock)
        {
            if (_granted.TryGetValue(pluginId, out HashSet<string>? grantedSet))
            {
                foreach (string id in grantedSet.Where(id => !keepSet.Contains(id)).ToArray())
                {
                    grantedSet.Remove(id);
                    if (!_denied.TryGetValue(pluginId, out HashSet<string>? deniedSet))
                    {
                        deniedSet = new HashSet<string>(StringComparer.Ordinal);
                        _denied[pluginId] = deniedSet;
                    }

                    deniedSet.Add(id);
                }
            }
        }
    }
}
