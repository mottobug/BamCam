using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BamCam.App.Controls;
using BamCam.App.Services;

namespace BamCam.App.Views;

/// <summary>Tools ▸ Options: edits a copy of the settings; OK returns it, Cancel returns null.</summary>
public sealed class OptionsWindow : Window
{
    private AppSettings _edited;
    private readonly PropertyGrid _grid = new() { NameColumnWidth = 210 };
    private AppSettings? _result;

    public OptionsWindow(AppSettings current, IEnumerable<string> postNames)
    {
        Title = "Options";
        Width = 560;
        Height = 640;
        MinWidth = 420;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _edited = current.Clone();
        _grid.SelectedObject = _edited;

        var posts = new TextBlock
        {
            Text = "Available post processors: " + string.Join(", ", postNames),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8, 4), FontSize = 11,
        };

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var defaults = new Button { Content = "Reset to defaults", MinWidth = 120 };
        ok.Click += (_, _) =>
        {
            _edited.Sanitize();
            _result = _edited;
            Close();
        };
        cancel.Click += (_, _) => Close();
        defaults.Click += (_, _) =>
        {
            var fresh = new AppSettings
            {
                // Keep history and window layout; reset only the configurable values.
                RecentFiles = _edited.RecentFiles,
                WindowWidth = _edited.WindowWidth,
                WindowHeight = _edited.WindowHeight,
                WindowMaximized = _edited.WindowMaximized,
                LeftColumnWidth = _edited.LeftColumnWidth,
                MessagesHeight = _edited.MessagesHeight,
            };
            _edited = fresh;
            _grid.SelectedObject = _edited;
        };

        var buttons = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(defaults, Dock.Left);
        buttons.Children.Add(defaults);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(ok);
        right.Children.Add(cancel);
        buttons.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(posts, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(posts);
        root.Children.Add(_grid);
        Content = root;
    }

    public async Task<AppSettings?> ShowFor(Window owner)
    {
        await ShowDialog(owner);
        return _result;
    }
}
