using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>First run of a script file not written here: shows the whole text and warns before anything runs.</summary>
public sealed class ScriptApprovalWindow : Window
{
    /// <summary>True when the user chose to run the script.</summary>
    public bool Approved { get; private set; }

    public ScriptApprovalWindow(string name, string path, string script)
    {
        Title = F("Run script {0}?", name);
        Width = 760;
        Height = 560;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var warning = new TextBlock
        {
            Text = F("This script has not run on this computer yet:\n{0}\n\nA script can do anything a program can do on this computer, such as reading or deleting files. Only run it if you trust its source or have read it. You are asked again when the text changes.", path),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8),
        };
        var text = new TextEditor
        {
            Text = script, IsReadOnly = true, ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"), FontSize = 13,
            SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#"),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        var run = new Button { Content = T("Run Script"), MinWidth = 80 };
        run.Click += (_, _) =>
        {
            Approved = true;
            Close();
        };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, IsDefault = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6), Children = { run, cancel } };

        var root = new DockPanel();
        DockPanel.SetDock(warning, Dock.Top);
        root.Children.Add(warning);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1), Child = text });
        Content = root;
    }
}
