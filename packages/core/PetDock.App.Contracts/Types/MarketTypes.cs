using System.Collections.Generic;

namespace PetDock.App;

/// <summary>
/// 市场加载结果：<see cref="Entries"/> 为空且 <see cref="Error"/> 为 null = 市场里确实没有插件；
/// Error 非 null = 没拉到（离线、被墙、索引坏了）。两者必须分开 —— 都显示成「没有插件」是撒谎。
/// </summary>
public sealed record MarketResult(IReadOnlyList<MarketItem> Entries, string? Error)
{
    public static MarketResult Failed(string error) => new(Array.Empty<MarketItem>(), error);
}

/// <summary>市场里的一行。<see cref="InstalledVersion"/> 非 null 表示本地已有同 id 的插件。</summary>
public sealed record MarketItem(
    string Id,
    string Name,
    string Version,
    string Summary,
    long Bytes,
    IReadOnlyList<string> Capabilities,
    string? InstalledVersion);
