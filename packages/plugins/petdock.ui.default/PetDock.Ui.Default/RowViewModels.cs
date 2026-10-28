using System.ComponentModel;
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
    }

    public string Id { get; }
    public string Name { get; }
    public string Source { get; }
    public string State { get; }
    public string Memory { get; }
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
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string State { get; }
    public int Granted { get; }
    public string Source { get; }
    public bool Loaded { get; }
    public bool Removable { get; }

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
