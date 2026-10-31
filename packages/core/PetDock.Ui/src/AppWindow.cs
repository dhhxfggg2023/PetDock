// 新层的空窗口（R1e）。故意只有一句占位文案：它的用处是**证明新泵能起一个真窗口**，
// 而不是提前长出一个界面。R3 会把它换成带导航/路由/PageScope 的壳。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PetDock.Ui;

/// <summary>新层的空窗口：标题 + 一句占位说明。</summary>
/// <remarks>
/// 用代码建而不是 XAML：这一片的窗口没有可复用的样式，R2 的设计系统一落地它会被整体替换，
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
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22));
        Content = BuildBody(strings);
    }

    /// <summary>占位内容：一行标题 + 一行说明。等 R2 的控件库到位后由真实页面取代。</summary>
    private static UIElement BuildBody(NewUiStrings strings)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };

        stack.Children.Add(new TextBlock
        {
            Text = strings.Title,
            FontSize = 22,
            Foreground = Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = strings.Placeholder,
            FontSize = 13,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xC4)),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            MaxWidth = 460,
            TextAlignment = System.Windows.TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        });

        return stack;
    }
}
