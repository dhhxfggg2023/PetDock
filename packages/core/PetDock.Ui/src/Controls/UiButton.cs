// 按钮（R2c）：四种用途，靠颜色区分，不靠尺寸猜。
//
// 两处刻意「不写模板」：
//   ① 默认模板已经尊重 Background / BorderBrush / 边框粗细 / Padding —— 自己再写一份只会
//      多一处要跟着 WPF 版本维护的模板；
//   ② 上一版试过用 ControlTemplate 画圆角壳，撞上 D-03b 的三字母词表：WPF 里那个「粗细」
//      依赖属性的名字（Thickness 加后缀 Property）恰好含有这三个字母，而词表是硬约束 ——
//      改设计比改规则便宜。
// 焦点框因此改成「键盘焦点进来时把边框加粗到 2 像素」，不用 FocusVisualStyle
// （后者要求一个模板，而模板绕不开上面那个依赖属性）。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PetDock.Ui.Design;

namespace PetDock.Ui.Controls;

/// <summary>按钮的用途。Primary 一屏只能有一个（用户一眼知道主操作是哪个）。</summary>
public enum UiButtonKind
{
    Primary = 0,
    Secondary = 1,
    Quiet = 2,
    Danger = 3,
}

/// <summary>一枚按钮：文字 + 可选图标 + 用途 + 键盘焦点框。</summary>
public sealed class UiButton : Button, IThemed
{
    private readonly UiButtonKind _kind;

    public UiButton(string text, UiButtonKind kind = UiButtonKind.Secondary, IconName icon = IconName.None)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        _kind = kind;
        Content = BuildContent(text, icon);
        Padding = new Thickness(Tokens.Space.Md, Tokens.Space.Sm, Tokens.Space.Md, Tokens.Space.Sm);
        BorderThickness = new Thickness(Tokens.Line.Hairline);
        Cursor = System.Windows.Input.Cursors.Hand;
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;

        // 键盘焦点框：进来加粗、离开还原（鼠标点击也会进来，所以它同时是「我在哪」的提示）。
        IsKeyboardFocusWithinChanged += (_, _) =>
            BorderThickness = new Thickness(
                IsKeyboardFocusWithin ? Tokens.Line.Emphasis : Tokens.Line.Hairline);

        ThemeHub.Attach(this);
        ApplyTheme(Theme.Current);
    }

    /// <summary>当前用途（判据用）。</summary>
    public UiButtonKind Kind => _kind;

    /// <summary>按用途换颜色。Danger 用状态色，Quiet 不用底色。</summary>
    public void ApplyTheme(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        (Color background, Color foreground, Color border) = _kind switch
        {
            UiButtonKind.Primary => (palette.Accent, palette.AccentText, palette.Accent),
            UiButtonKind.Danger => (palette.Danger, palette.Background, palette.Danger),
            UiButtonKind.Secondary => (palette.SurfaceAlt, palette.TextPrimary, palette.Border),
            _ => (palette.Background, palette.TextSecondary, palette.Background),
        };

        Background = palette.Brush(background);
        Foreground = palette.Brush(foreground);
        BorderBrush = palette.Brush(border);
    }

    private static UIElement BuildContent(string text, IconName icon)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        if (icon != IconName.None)
        {
            row.Children.Add(new TextBlock
            {
                Text = Icons.Glyph(icon),
                FontFamily = new FontFamily(Icons.FontFamily),
                FontSize = Tokens.Text.Body,
                Margin = new Thickness(0, 0, Tokens.Space.Sm, 0),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily(Tokens.Text.FontFamily),
            FontSize = Tokens.Text.Body,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        });

        return row;
    }
}