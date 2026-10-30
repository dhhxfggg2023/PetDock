// 随包发行的默认界面插件：三个页面都走 IUiPage 契约，壳不认识它们。
// 第三方可以照这个项目的样子贡献自己的页面；清单 contributes.pages 指向实现类型。
using System.Collections.Generic;
using PetDock.Core.Ui;

namespace PetDock.Ui.Default;

public sealed class DefaultUiPlugin : IUiPlugin
{
    public string Id => "petdock.ui.default";

    public IReadOnlyList<IUiPage> CreatePages(IUiPageContext context) => new IUiPage[]
    {
        new PetsPage(context),
        new PluginsPage(context),
        new SettingsPage(context),
    };
}
