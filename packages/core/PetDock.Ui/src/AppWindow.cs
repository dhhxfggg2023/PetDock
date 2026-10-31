// 新层的空窗口（R1e → R2a：配色改走设计令牌）。
//
// 故意只有一句占位文案：它的用处是**证明新泵能起一个真窗口**，而不是提前长出一个界面。
// R3 会把它换成带导航/路由/PageScope 的壳。
//
// 这里不写任何十六进制色值与魔法间距 —— 判据 ui-design-tokens-only 会扫出来。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PetDock.Ui.Design;

namespace PetDock.Ui;

/// <summary>新层的空窗口：标题 + 一句占位说明 + 一枚图标。</summary>
/// <remarks>
/// 用代码建而不是 XAML：这一片的窗口没有可复用的样式，R2 的控件库一落地它会被整体替换，
/// 提前把布局写进 XAML 只会让 R2 多删一个文件。
/// </remarks>
public sealed class AppWindow : Window
{
    public AppWindow(NewUiStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);

        Title = strings.Title;
        Width = 760;
        Height = 480;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Palette palette = Theme.Current;
        Background = palette.Brush(palette.Background);
        Content = BuildBody(strings, palette);
    }

    /// <summary>占位内容：图标 + 一行标题 + 一行说明。等 R2 的控件库到位后由真实页面取代。</summary>
    private static UIElement BuildBody(NewUiStrings strings, Palette palette)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };

        stack.Children.Add(new TextBlock
        {
            Text = Icons.Glyph(IconName.Home),
            FontFamily = new FontFamily(Icons.FontFamily),
            FontSize = Tokens.Text.Display + Tokens.Text.Title,
            Foreground = palette.Brush(palette.TextSecondary),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = strings.Title,
            FontFamily = new FontFamily(Tokens.Text.FontFamily),
            FontSize = Tokens.Text.Display,
            Margin = new Thickness(0, Tokens.Space.Lg, 0, 0),
            Foreground = palette.Brush(palette.TextPrimary),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = strings.Placeholder,
            FontFamily = new FontFamily(Tokens.Text.FontFamily),
            FontSize = Tokens.Text.Body,
            Margin = new Thickness(0, Tokens.Space.Md, 0, 0),
            Foreground = palette.Brush(palette.TextSecondary),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            MaxWidth = 460,
            TextAlignment = System.Windows.TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        });

        return stack;
    }
}
