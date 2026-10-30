// 宠物窗的短气泡接线（2026-10-29）：点击/拖动时给一句反馈。
// 气泡是独立窗（PetSpeechBubble），这里只负责「什么时候说、跟着宠物走、窗口销毁时收掉」。
using System;
using PetDock.Core.Ui;

namespace PetDock.Core.Compositor;

internal sealed partial class LayeredFrameWindow
{
    private PetSpeechBubble? _speech;

    /// <summary>
    /// 发给窗口线程的那一句话。自声明而不借系统委托：内核文本里那个词（含它的类型名）
    /// 不能整词落盘（D-03b），先例见 HostingTypes.cs。
    /// </summary>
    private delegate void SpeechRelay(string text);

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

    /// <summary>
    /// 从任意线程发一句话（陪伴层的 tick 在帧泵线程；气泡是窗口线程的窗体）。
    /// 句柄还没建好时直接走 ShowSpeech —— 它自己会按 Visible 判掉「窗口还没上桌」的情况。
    /// </summary>
    internal void PostSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsDisposed)
        {
            return;
        }

        if (IsHandleCreated && InvokeRequired)
        {
            try
            {
                BeginInvoke(new SpeechRelay(ShowSpeech), text);
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁：这句话丢掉，不因为它让帧泵线程抛出去。
            }

            return;
        }

        ShowSpeech(text);
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
