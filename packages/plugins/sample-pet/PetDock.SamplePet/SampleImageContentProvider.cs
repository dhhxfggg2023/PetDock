// 实现 docs/40 §1.1 的分层归属与 docs/10「内核白名单」里的插件宿主接入口：
// 「这一帧的像素是什么」由 plugins 层决定并实现 IFrameContentProvider；内核只按契约调用，
// 既不认识素材文件，也不认识本类型（docs/40 §3.1 D-03b：内核零上层概念）。
using PetDock.Core.Compositor;

namespace PetDock.SamplePet;

/// <summary>
/// 示例插件的帧内容装配：加载仓库 assets/sample 下的静态图，交给上层窗口合成。
/// 装配失败（图缺失、解码失败）时 <see cref="Note"/> 说明原因，内核据此退回纯色兜底。
/// </summary>
internal sealed class SampleImageContentProvider : IFrameContentProvider
{
    private string _note = "not-assembled";

    /// <summary>实现契约：能加载出来就返回内容源，加载不出来就返回 null 让内核退回兜底。</summary>
    public IFrameContentSource? Create()
    {
        SampleImageContent? content = SampleImageContent.TryLoad(out string note);
        _note = note;
        return content;
    }

    /// <summary>最近一次装配的说明（内容源自带「加载了哪个文件」或「为什么没有」）。</summary>
    public string Note => _note;
}
