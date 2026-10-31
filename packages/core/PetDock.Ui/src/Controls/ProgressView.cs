// 长任务进度（R2c）：确定/不确定两种形态 + 一枚取消按钮。
//
// 与旧层的取舍相同但收得更紧：不确定态（还不知道总量）与确定态（知道比例）必须分开画，
// 否则「0% 卡住」和「正在做」看起来一模一样（research/ui-lessons.md 的进度类教训）。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PetDock.Ui.Design;

namespace PetDock.Ui.Controls;

/// <summary>一条进度：一句「正在做什么」 + 进度条 + 取消。</summary>
public sealed class ProgressView : StackPanel, IThemed
{
    private readonly TextBlock _stage;
    private readonly ProgressBar _bar;
    private readonly UiButton _cancel;

    public ProgressView(string cancelText)
    {
        ArgumentException.ThrowIfNullOrEmpty(cancelText);

        Orientation = Orientation.Vertical;
        Margin = new Thickness(0, Tokens.Space.Md, 0, 0);
        Visibility = Visibility.Collapsed;

        _stage = new TextBlock
        {
            FontFamily = new FontFamily(Tokens.Text.FontFamily),
            FontSize = Tokens.Text.Body,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _bar = new ProgressBar
        {
            Height = 4,
            Margin = new Thickness(0, Tokens.Space.Xs, 0, 0),
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            IsIndeterminate = true,
        };

        _cancel = new UiButton(cancelText, UiButtonKind.Quiet);
        _cancel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        _cancel.Margin = new Thickness(0, Tokens.Space.Xs, 0, 0);
        _cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);

        Children.Add(_stage);
        Children.Add(_bar);
        Children.Add(_cancel);

        ThemeHub.Attach(this);
        ApplyTheme(Theme.Current);
    }

    /// <summary>用户按了取消。宿主只当请求处理（任务在下一个安全点停）。</summary>
    public event EventHandler? CancelRequested;

    /// <summary>正在显示（判据用）。</summary>
    public bool IsBusy => Visibility == Visibility.Visible;

    /// <summary>当前阶段文字（判据用）。</summary>
    public string Stage => _stage.Text;

    /// <summary>是否是确定态（判据用）。</summary>
    public bool IsDeterminate => !_bar.IsIndeterminate;

    /// <summary>确定态的比例（0..1）。</summary>
    public double Ratio => _bar.Value;

    /// <summary>开始：显示出来，回到不确定态。</summary>
    public void Begin(string stage)
    {
        _stage.Text = stage;
        _bar.IsIndeterminate = true;
        _bar.Value = 0;
        Visibility = Visibility.Visible;
    }

    /// <summary>报一次进度：ratio ≥ 0 就切到确定态（-1 表示回到不确定态，例如换阶段了）。</summary>
    public void Report(double ratio, string? stage = null)
    {
        if (stage is not null)
        {
            _stage.Text = stage;
        }

        if (ratio < 0)
        {
            _bar.IsIndeterminate = true;
            return;
        }

        _bar.IsIndeterminate = false;
        _bar.Value = Math.Clamp(ratio, 0, 1);
    }

    /// <summary>结束：收起来（并清掉文案，避免下一次 Begin 之前露出旧的那句）。</summary>
    public void End()
    {
        Visibility = Visibility.Collapsed;
        _stage.Text = string.Empty;
        _bar.Value = 0;
        _bar.IsIndeterminate = true;
    }

    public void ApplyTheme(Palette palette)
    {
        _stage.Foreground = palette.Brush(palette.TextSecondary);
        _bar.Foreground = palette.Brush(palette.Accent);
        _bar.Background = palette.Brush(palette.SurfaceAlt);
    }
}