// 宠物窗的短气泡接线（2026-10-29）：点击/拖动时给一句反馈。
// 气泡是独立窗（PetSpeechBubble），这里只负责「什么时候说、跟着宠物走、窗口销毁时收掉」。
using System;
using PetDock.Core.Ui;

namespace PetDock.Core.Compositor;

internal sealed partial class LayeredFrameWindow
{
    private PetSpeechBubble? _speech;

    /// <summary>气泡是否正在显示（判据用：证明点击/拖动真的说了话，而不是只抛了事件）。</summary>
    internal bool SpeechVisible => _speech is { BubbleVisible: true };

    /// <summary>在宠物头顶说一句话；已有气泡先收掉，避免两条叠在一起。</summary>
    internal void ShowSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsDisposed || !Visible)
        {
            return;
        }

        _speech?.Dispose();
        _speech = new PetSpeechBubble(text);
        _speech.ShowNear(this);
    }

    /// <summary>判据清理用：不关窗口，只收气泡。</summary>
    internal void HideSpeechForCheck()
    {
        _speech?.Dispose();
        _speech = null;
    }

    /// <summary>拖动时气泡跟着走：用户把宠物拖到哪儿，话就在它头上。</summary>
    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        _speech?.Reposition(this);
    }
}
