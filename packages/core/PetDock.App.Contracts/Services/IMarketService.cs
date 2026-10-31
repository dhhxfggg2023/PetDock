using System.Threading.Tasks;

namespace PetDock.App;

/// <summary>
/// 插件市场：拉索引、从市场装一只。
///
/// 「只有点这一下才联网」是本程序的两条出网纪律之一（另一条是检查更新）：
/// 构造、刷新、切页**都不许**调这两个方法。
/// 安装成功后返回的是「已装好」的事实；索引里的下载地址与 sha256 都由服务自己校验，
/// 界面层**不**传 URL 进来。
/// </summary>
public interface IMarketService
{
    /// <summary>拉索引并返回可装的条目（联网一次）。</summary>
    Task<MarketResult> LoadAsync();

    /// <summary>从市场装一只：下载 → 校验 sha256 → 交给安装器（同一条安装管线）。</summary>
    Task<AppResult> InstallAsync(string pluginId);
}
