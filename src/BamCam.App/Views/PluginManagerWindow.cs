using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using BamCam.App.Services;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>One row of the plugin list.</summary>
public sealed class PluginRow(PluginInfo info, bool enabled) : INotifyPropertyChanged
{
    private bool _enabled = enabled;

    public PluginInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Vendor => Info.Vendor;
    public string Version => Info.Version;
    /// <summary>File name; the folder opens with "Open Plugin Folder".</summary>
    public string File => System.IO.Path.GetFileName(Info.Path);

    public string State => Info.State switch
    {
        PluginState.Loaded => T("Loaded"),
        PluginState.Failed => F("Error: {0}", Info.Error ?? ""),
        PluginState.NotApproved => T("Not approved"),
        _ => T("Not loaded"),
    };

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Tools ▸ Plugins: installed plugins, their state, and switching them on or off.</summary>
public sealed class PluginManagerWindow : Window
{
    private readonly ObservableCollection<PluginRow> _rows = [];
    private readonly List<PluginApproval> _approvals;

    /// <summary>True when the approvals were changed (to be saved).</summary>
    public bool Changed { get; private set; }

    public PluginManagerWindow(PluginManager manager, List<PluginApproval> approvals)
    {
        _approvals = approvals;
        Title = T("Plugins");
        Width = 900;
        Height = 380;
        MinWidth = 420;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        foreach (var p in manager.Plugins) _rows.Add(new PluginRow(p, PluginManager.IsEnabled(p, approvals)));

        var grid = new DataGrid
        {
            ItemsSource = _rows, AutoGenerateColumns = false, CanUserSortColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column,
            Margin = new Thickness(6), IsVisible = _rows.Count > 0,
        };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = T("Enabled"), Binding = new Binding(nameof(PluginRow.Enabled)), Width = new DataGridLength(90) });
        grid.Columns.Add(new DataGridTextColumn { Header = T("Name"), Binding = new Binding(nameof(PluginRow.Name)), IsReadOnly = true, Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridTextColumn { Header = T("Vendor"), Binding = new Binding(nameof(PluginRow.Vendor)), IsReadOnly = true, Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridTextColumn { Header = T("Version"), Binding = new Binding(nameof(PluginRow.Version)), IsReadOnly = true, Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridTextColumn { Header = T("State"), Binding = new Binding(nameof(PluginRow.State)), IsReadOnly = true, Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = T("File"), Binding = new Binding(nameof(PluginRow.File)), IsReadOnly = true, Width = DataGridLength.Auto });
        foreach (var row in _rows) row.PropertyChanged += (_, _) => Apply(row);

        var empty = new TextBlock
        {
            Text = F("No plugins installed. Plugins are .NET assemblies (.dll) placed in:\n{0}", PluginManager.UserFolder),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(12), IsVisible = _rows.Count == 0,
        };
        var hint = new TextBlock
        {
            Text = T("Changes take effect after restarting BamCam. Plugins run with the same rights as BamCam."),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8, 0), FontSize = 11,
        };

        var open = new Button { Content = T("Open Plugin Folder") };
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(PluginManager.UserFolder);
            MainWindow.OpenFolder(PluginManager.UserFolder);
        };
        var close = new Button { Content = T("Close"), IsDefault = true, IsCancel = true, MinWidth = 80 };
        close.Click += (_, _) => Close();
        var buttons = new DockPanel { Margin = new Thickness(6) };
        DockPanel.SetDock(open, Dock.Left);
        buttons.Children.Add(open);
        buttons.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { close } });

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(hint);
        root.Children.Add(empty);
        root.Children.Add(grid);
        Content = root;
    }

    private void Apply(PluginRow row)
    {
        _approvals.RemoveAll(a => string.Equals(a.File, row.Info.Path, StringComparison.OrdinalIgnoreCase));
        _approvals.Add(new PluginApproval { File = row.Info.Path, Hash = row.Info.Hash, Enabled = row.Enabled });
        Changed = true;
    }
}
