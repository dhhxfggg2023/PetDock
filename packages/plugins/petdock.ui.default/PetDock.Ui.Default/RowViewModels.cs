using System;
using PetDock.App;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PetDock.Core.Ui;

namespace PetDock.Ui.Default;

/// <summary>宠物页一行。只读：刷新时整行替换，不做增量更新。</summary>
public sealed class PetRow
{
    public PetRow(PetLine line)
    {
        Id = line.Id;
        Name = line.Name;
        Source = line.Source;
        State = line.State;
        Memory = line.Memory;
        Author = line.Author;
        License = line.License;
        Path = line.Path;
        IconPath = line.IconPath;
        OnDesktop = line.OnDesktop;
        SizePx = line.SizePx;
    }

    public string Id { get; }
    public string Name { get; }
    public string Source { get; }
    public string State { get; }
    public string Memory { get; }
    public string? Author { get; }
    public string? License { get; }
    public string? Path { get; }
    public string? IconPath { get; }

    /// <summary>这一只现在在不在桌面上（决定按钮是「显示到桌面」还是「从桌面收起」）。</summary>
    public bool OnDesktop { get; }

    /// <summary>这一只的窗口边长（每只单独设，2026-10-31）。</summary>
    public int SizePx { get; }
}

/// <summary>插件页一行。Enabled 是唯一可写的字段（行内开关）。</summary>
public sealed class PluginRow : INotifyPropertyChanged
{
    private bool _enabled;

    public PluginRow(PluginLine line)
    {
        Id = line.Id;
        Name = line.Name;
        Version = line.Version;
        State = line.State;
        Granted = line.Granted;
        Source = line.Source;
        Enabled = line.Enabled;
        Loaded = line.Loaded;
        Removable = line.Removable;
        Essential = line.Essential;
        Description = line.Description;
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string State { get; }
    public int Granted { get; }
    public string Source { get; }
    public bool Loaded { get; }
    public bool Removable { get; }

    /// <summary>清单声明的「我是干什么的」；null = 没声明（界面显示「未声明」）。</summary>
    public string? Description { get; }

    /// <summary>系统必需插件（清单 essential）：不能停用，也不能删除；界面必须说明原因。</summary>
    public bool Essential { get; }

    public bool CanToggle => !Essential;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }
            _enabled = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 市场里的一行。装完只改这一行的动作位（不重新联网拉列表）。
/// 动作位的三种含义：没装过 → 安装；装的是别的版本 → 更新；装的就是这一版 → 已装（不可点）。
/// </summary>
public sealed class MarketRow : INotifyPropertyChanged
{
    private readonly IUiPageContext _context;
    private string _actionLabel;
    private bool _canInstall;

    public MarketRow(MarketItem line, IUiPageContext context)
    {
        _context = context;
        Id = line.Id;
        Name = line.Name;
        Version = line.Version;
        Summary = line.Summary;
        SizeText = (line.Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

        if (string.Equals(line.InstalledVersion, line.Version, StringComparison.Ordinal))
        {
            _actionLabel = context.T("cp.market.installed", line.Version);
            _canInstall = false;
        }
        else
        {
            _actionLabel = context.T(line.InstalledVersion is null ? "cp.market.install" : "cp.market.update");
            _canInstall = true;
        }
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Summary { get; }
    public string SizeText { get; }

    public string ActionLabel
    {
        get => _actionLabel;
        private set
        {
            if (_actionLabel == value)
            {
                return;
            }
            _actionLabel = value;
            OnPropertyChanged();
        }
    }

    public bool CanInstall
    {
        get => _canInstall;
        private set
        {
            if (_canInstall == value)
            {
                return;
            }
            _canInstall = value;
            OnPropertyChanged();
        }
    }

    /// <summary>装好了：动作位变成「已装 本行版本」，不再可点。</summary>
    public void MarkInstalled()
    {
        ActionLabel = _context.T("cp.market.installed", Version);
        CanInstall = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
