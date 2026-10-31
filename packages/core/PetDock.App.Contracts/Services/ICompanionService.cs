using System.Collections.Generic;

namespace PetDock.App;

/// <summary>
/// 陪伴层上的一枚按钮。Label 由插件本地化 —— 宿主不认识「喂食」是什么，
/// 只负责把按钮摆出来、把点击原样转回插件。
/// </summary>
public sealed record CompanionButton(string CommandId, string Label);

/// <summary>
/// 一只宠物的一层陪伴（只读快照）。Title/Status/Detail 全是插件给的原话，
/// 宿主只做转述：不解释、不改写、不缓存（时间在走，两次读取之间它本来就会变）。
/// </summary>
public sealed record CompanionRow(
    string PetId,
    string PetName,
    string CompanionId,
    string Title,
    string Status,
    string Detail,
    IReadOnlyList<CompanionButton> Buttons);

/// <summary>
/// 陪伴层：列出桌面每只宠物每层的现状，并把按钮点击转回声明它的插件。
/// 界面层不认宠物、也不认陪伴层的含义；判定、说话、落盘都在宿主与插件侧。
/// </summary>
public interface ICompanionService
{
    /// <summary>当前读数（每次调用重新取，宿主不在这里缓存）。</summary>
    IReadOnlyList<CompanionRow> Rows();

    /// <summary>按下某层上的某枚按钮。成功返回 Ok；失败给一句给人看的原因。</summary>
    AppResult Invoke(string petId, string companionId, string commandId);
}
