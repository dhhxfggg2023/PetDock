namespace PetDock.App;

/// <summary>
/// 宠物：上/下桌面、复制、删除、改尺寸。
///
/// 刻意**不含**「导入」：下载/解压/转码是长任务，它的形状（进度、取消、后台线程）
/// 与这些同步意图完全不同，等 R3 的 IWorkService 一起定，免得在这里先长出一个四不像。
///
/// 也不含「按 id 显示/隐藏窗口」这类纯运行时动作：那是宿主自己的事，界面层只表达用户意图。
/// </summary>
public interface IPetsService
{
    /// <summary>把一只从桌面上收起或放回来（不动清单、不动素材）。</summary>
    AppResult SetOnDesktop(string petId, bool onDesktop);

    /// <summary>给同一只再开一个实例。</summary>
    AppResult Duplicate(string petId);

    /// <summary>彻底删除一只（含最后一份素材）。不可逆 —— 确认与撤销属于界面层。</summary>
    AppResult Delete(string petId);

    /// <summary>改**这一只**的窗口边长；petId 为 null 表示只改新宠物的默认值。</summary>
    AppResult SetSize(string? petId, int size);
}
