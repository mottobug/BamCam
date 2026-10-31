using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BamCam.App.Controls;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>Edits the properties of a settings object in a property grid; OK returns true.</summary>
public sealed class ObjectDialog : Window
{
    private bool _ok;

    public ObjectDialog(string title, object settings, string? hint = null)
    {
        Title = title;
        Width = 460;
        Height = 420;
        MinWidth = 360;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var grid = new PropertyGrid { NameColumnWidth = 170, SelectedObject = settings };
        var ok = new Button { Content = T("OK"), IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            _ok = true;
            Close();
        };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        if (hint != null)
        {
            var text = new TextBlock { Text = hint, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8, 6), FontSize = 11 };
            DockPanel.SetDock(text, Dock.Top);
            root.Children.Add(text);
        }
        root.Children.Add(grid);
        Content = root;
    }

    public async Task<bool> ShowFor(Window owner)
    {
        await ShowDialog(owner);
        return _ok;
    }
}
