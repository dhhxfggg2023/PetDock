// 宠物页的导入入口（RFC-15 §3.5，原规格 18 §2.1）。
//
// 面板在这里**只做两件事**：给用户一个「导入」入口，以及把失败原因显示成一条内联错误条。
// 面板**不解析素材**（原规格 18:35「导入必须走 原规格 15 的管线，面板本身不做解析」）——
// 真正的复制、登记、换源都在宿主编排里（PetImportService）。
//
// 为什么用内联错误条而不是 MessageBox：原规格 18 §5 明写「插件崩溃也不弹模态框」，
// RFC-15 §3.5 把导入失败也归到同一类 —— 失败信息出现在宠物页里，用户当场看得见。
using System;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

internal sealed partial class ControlPanel
{
    /// <summary>「导入素材包」入口（宠物页专属，不进插件页的操作栏）。</summary>
    private readonly ThemedButton _importButton = new()
    {
        Text = I18n.Get("cp.pets.import"),
        Name = "import-pack",
    };

    /// <summary>内联错误条（平时不可见；有失败信息时出现）。</summary>
    private readonly Label _importError = new()
    {
        Name = "import-error",
        Dock = DockStyle.Bottom,
        Visible = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Text = string.Empty,
    };

    /// <summary>导入动作的回调（宿主侧执行；null = 没有导入能力，面板不显示入口）。</summary>
    private PetImportCommand? _importCommand;

    /// <summary>错误条「被要求显示」的状态（与 WinForms 的容器可见性分开记，见 ImportErrorShown）。</summary>
    private bool _importErrorShown;

    /// <summary>
    /// 导入回调的形状：面板已经选好了目录（它弹对话框），
    /// 宿主侧只管「拿这个目录去导入」。返回 true = 成功。
    /// </summary>
    internal delegate bool PetImportCommand(string sourceDir, out string message);

    /// <summary>导入入口的名字（自检按它断言入口确实存在）。</summary>
    public string ImportButtonName => _importButton.Name;

    /// <summary>
    /// 导入入口挂在哪个页上（自检断言它在**宠物页**，不在别的页）。
    /// 走控件树往上找 TabPage，不靠记「第几个页」。
    /// </summary>
    public string ImportEntryPage
    {
        get
        {
            Control? node = _importButton;
            while (node?.Parent is not null)
            {
                node = node.Parent;
                if (node is Panel page && page.Name.StartsWith("tab-", StringComparison.Ordinal))
                {
                    return page.Name;
                }
            }

            return string.Empty;
        }
    }

    /// <summary>导入入口当前是否可见（没有导入能力时为 false）。</summary>
    public bool ImportButtonVisible => _importButton.Visible;

    /// <summary>
    /// 错误条当前**是否被要求显示**（自检读它）。
    /// 为什么不用 WinForms 的 Visible 属性：那个 getter 对「父容器不可见」的子控件一律返回 false
    /// （自检形态下整个面板不显示，错误条再亮也读成 false）——那是容器状态，不是错误条自己的状态。
    /// </summary>
    public bool ImportErrorShown => _importErrorShown;

    /// <summary>错误条当前的文字（自检读它，非空即可判）。</summary>
    public string ImportErrorText => _importError.Text;

    /// <summary>给导入入口装一个宿主侧的回调（构造时传入）。</summary>
    public void SetImportCommand(PetImportCommand? command) => _importCommand = command;

    /// <summary>
    /// 宠物页的控件树：列表 + 底部操作栏（导入入口）+ 错误条。
    /// 顺序要紧：后加的先排版 —— 错误条与操作栏都 Dock=Bottom，所以错误条先加、操作栏后加，
    /// 操作栏才会贴在最下面，错误条在它上面。
    /// </summary>
    private void InitPetsPage()
    {
        _importButton.Click += (_, _) => RunImport();

        Panel page = PageOf(PanelPages.Pets);
        int dpi = Theme.DpiOf(page);
        _importButton.FitToText();

        // 错误条：赤陶字压在压暗的赤陶底上（原规格 18 §2.6 的失败色对）。
        _importError.Font = Theme.FontFor(page, TypeRole.Body);
        _importError.ForeColor = Theme.Bad;
        _importError.BackColor = Theme.BadSurface;
        _importError.Height = Theme.Dip(dpi, 46);
        _importError.Padding = new Padding(
            Theme.Dip(dpi, Theme.SpaceMd), Theme.Dip(dpi, Theme.SpaceXs), Theme.Dip(dpi, Theme.SpaceMd), Theme.Dip(dpi, Theme.SpaceXs));

        FlowLayoutPanel bar = FormGrid.Bar(page);
        bar.Controls.Add(_importButton);

        page.Controls.Add(_importError);
        page.Controls.Add(bar);
    }

    /// <summary>
    /// 跑一次导入。选目录的对话框在 UI 线程上弹（模态），
    /// 复制与换源走宿主编排 —— 面板自己一个字节都不解析。
    /// </summary>
    private void RunImport()
    {
        if (_importCommand is null)
        {
            ShowImportError(I18n.Get("cp.pets.unavailable"));
            return;
        }

        using var picker = new FolderBrowserDialog
        {
            Description = I18n.Get("cp.pets.pickFolder"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        string? picked;
        try
        {
            picked = picker.ShowDialog() == DialogResult.OK ? picker.SelectedPath : null;
        }
        catch (Exception error)
        {
            ShowImportError(I18n.Get("cp.pets.pickerFailed") + error.Message);
            return;
        }

        // 取消不是失败（RFC-15 §3.5 第一行）：静默返回，不显示错误条。
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        TryImport(picked);
    }

    /// <summary>
    /// 用**已经选好的目录**跑一次导入（自检走这条路：它不需要弹目录选择器）。
    /// 返回 true = 导入成功。
    /// </summary>
    internal bool TryImport(string picked)
    {
        if (_importCommand is null)
        {
            ShowImportError(I18n.Get("cp.pets.unavailable"));
            return false;
        }

        bool ok = _importCommand(picked, out string message);
        if (ok)
        {
            _importErrorShown = false;
            _importError.Visible = false;
            _importError.Text = string.Empty;
            RefreshCurrent();
            return true;
        }

        ShowImportError(message);
        return false;
    }

    /// <summary>显示内联错误条（失败原因留在这页里，不弹模态框）。</summary>
    private void ShowImportError(string message)
    {
        _importError.Text = message;
        _importError.Visible = true;
        _importErrorShown = true;
    }
}
