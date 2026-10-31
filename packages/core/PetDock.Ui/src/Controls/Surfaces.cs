// 容器与提示（R2c）：卡片、分段标题、横幅、空状态、表单行。
//
// 这一组都是**薄**的：没有自己的模板引擎，只把令牌套到 Border/StackPanel 上。
// 薄的好处是 R4–R6 的页面不会为了排版去写一堆 XAML 属性，读起来是「加一张卡片」而不是
// 「设 BorderBrush、CornerRadius、Padding、Margin、SnapsToDevicePixels…」。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PetDock.Ui.Design;

namespace PetDock.Ui.Controls;

/// <summary>卡片：一档底色 + 一条边框 + 统一内边距。层的区分靠边框，不靠阴影。</summary>
public sealed class Card : Border, IThemed
{
    public Card(UIElement? content = null)
    {
        Child = content;
        Padding = new Thickness(Tokens.Space.Lg);
        BorderThickness = new Thickness(Tokens.Line.Hairline);
        CornerRadius = new CornerRadius(Tokens.Radius.Lg);

        ThemeHub.Attach(this);
        ApplyTheme(Theme.Current);
    }

    public void ApplyTheme(Palette palette)
    {
        Background = palette.Brush(palette.Surface);
        BorderBrush = palette.Brush(palette.Border);
    }
}

/// <summary>一段的标题 + 一行说明（说明可省）。</summary>
public sealed class SectionHeader : StackPanel, IThemed
{
    private readonly TextBlock _title;
    private readonly TextBlock _note;

    public SectionHeader(string title, string? note = null)
    {
        Orientation = Orientation.Vertical;
        Margin = new Thickness(0, 0, 0, Tokens.Space.Md);

        _title = new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily(Tokens.Text.FontFamily),
            FontSize = Tokens.Text.Title,
            FontWeight = FontWeights.SemiBold,
        };
        Children.Add(_title);

        _note = new TextBlock
        {
            Text = note ?? string.Empty,
            FontFamily = new FontFamily(Tokens.Text.FontFamily),
            FontSize = Tokens.Text.Body,
            Margin = new Thickness(0, Tokens.Space.Xs, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible,
        };
        Children.Add(_note);

        ThemeHub.Attach(this);
        ApplyTheme(Theme.Current);
    }

    public void ApplyTheme(Palette palette)
    {
        _title.Foreground = palette.Brush(palette.TextPrimary);
        _note.Foreground = palette.Brush(palette.TextSecondary);
    }
}

/// <summary>横幅的四种语气。Info 与 Error 不能混用：用户会据此决定「要不要停手」。</summary>
public enum BannerKind
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>一条提示：图标 + 一句话 + 可选动作槽。</summary>
public sealed class Banner : Border, IThemed
{
    private readonly BannerKind _kind;
    private readonly TextBlock _icon;
    private readonly TextBlock _text;

    public Banner(BannerKind kind, string text)
    {
        _kind = kind;
        Padding = new Thickness(Tokens.Space.Md);
        BorderThickness = new Thickness(Tokens.Line.Hairline);
        CornerRadius = new CornerRadius(Tokens.Radius.Md);

        _icon = new TextBlock
        {
            FontFamily = new FontFamily(Icons.FontFamily),
            FontSize = Tokens.Text.Emphasized,
            Margin = new Thickness(0, 0, Tokens.Space.Sm, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
        };

        _text = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily(Tokens.Text.FontFamily),
            FontSize = Tokens.Text.Body,
            TextWrapping = TextWrapping.Wrap,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_icon);
        row.Children.Add(_text);
        Child = row;

        ThemeHub.Attach(this);
        ApplyTheme(Theme.Current);
    }

    /// <summary>语气（判据用）。</summary>
    public BannerKind Kind => _kind;

    /// <summary>正文（判据用：错误条必须原样带着服务给的原因）。</summary>
    public string Text => _text.Text;

    public void ApplyTheme(Palette palette)
    {
        (Color tone, IconName icon) = _kind switch
        {
            BannerKind.Success => (palette.Success, IconName.Check),
            BannerKind.Warning => (palette.Warning, IconName.Warning),
            BannerKind.Error => (palette.Danger, IconName.Error),
            _ => (palette.Accent, IconName.Info),
        };

        Background = palette.Brush(palette.SurfaceAlt);
        BorderBrush = palette.Brush(tone);
        _icon.Text = Icons.Glyph(icon);
        _icon.Foreground = palette.Brush(tone);
        _text.Foreground = palette.Brush(palette.TextPrimary);
    }
}

/// <summary>空状态：图标 + 一句「这里为什么是空的」 + 一句「怎么让它不空」。</summary>
public sealed class EmptyState : StackPanel, IThemed
{
    private readonly TextBlock _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _note;

    public EmptyState(string title, string note, IconName icon = IconName.Info)
    {
        Orientation = Orientation.Vertical;
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        Margin = new Thickness(0, Tokens.Space.Xl, 0, Tokens.Space.Xl);

        _icon = new TextBlock
        {
            Text = Icons.Glyph(icon),
            FontFamily = new FontFamily(Icons.FontFamily),
            FontSize = Tokens.Text.Display + Tokens.Text.Title,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        Children.Add(_icon);

        _title = new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily(Tokens.Text.FontFamily),
            FontSize = Tokens.Text.Emphasized,
            Margin = new Thickness(0, Tokens.Space.Md, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        Children.Add(_title);

        _note = new TextBlock
        {
            Text = note,
            FontFamily = new FontFamily(Tokens.Text.FontFamily),
            FontSize = Tokens.Text.Body,
            Margin = new Thickness(0, Tokens.Space.Xs, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        Children.Add(_note);

        ThemeHub.Attach(this);
        ApplyTheme(Theme.Current);
    }

    /// <summary>标题文字（判据用）。</summary>
    public string Title => _title.Text;

    public void ApplyTheme(Palette palette)
    {
        _icon.Foreground = palette.Brush(palette.TextSecondary);
        _title.Foreground = palette.Brush(palette.TextPrimary);
        _note.Foreground = palette.Brush(palette.TextSecondary);
    }
}

/// <summary>表单一行：标签 + 控件 + 一句提示（提示可省）。</summary>
public sealed class Field : StackPanel, IThemed
{
    private readonly TextBlock _label;
    private readonly TextBlock _hint;

    public Field(string label, UIElement editor, string? hint = null)
    {
        ArgumentNullException.ThrowIfNull(editor);

        Orientation = Orientation.Vertical;
        Margin = new Thickness(0, 0, 0, Tokens.Space.Md);

        _label = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily(Tokens.Text.FontFamily),
            FontSize = Tokens.Text.Body,
            Margin = new Thickness(0, 0, 0, Tokens.Space.Xs),
        };
        Children.Add(_label);
        Children.Add(editor);

        _hint = new TextBlock
        {
            Text = hint ?? string.Empty,
            FontFamily = new FontFamily(Tokens.Text.FontFamily),
            FontSize = Tokens.Text.Caption,
            Margin = new Thickness(0, Tokens.Space.Xs, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible,
        };
        Children.Add(_hint);

        ThemeHub.Attach(this);
        ApplyTheme(Theme.Current);
    }

    public void ApplyTheme(Palette palette)
    {
        _label.Foreground = palette.Brush(palette.TextPrimary);
        _hint.Foreground = palette.Brush(palette.TextSecondary);
    }
}