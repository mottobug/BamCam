using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// Editor of a script object's C# script: line numbers, C# highlighting, Run (Ctrl+Enter) shows the result in the
/// drawing at once and jumps to the line of a compile error. OK keeps the script, Cancel restores the old one.
/// </summary>
public sealed partial class ScriptEditorWindow : Window
{
    private readonly TextEditor _editor;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _position = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly Func<string, (int? Outlines, string? Error)> _run;

    /// <summary>The script when closed with OK, else null.</summary>
    public string? Result { get; private set; }

    // "Line 5: …" in any interface language: the first number before the colon.
    [GeneratedRegex(@"^\D*(\d+):")]
    private static partial Regex ErrorLine();

    public ScriptEditorWindow(string script, Func<string, (int? Outlines, string? Error)> run)
    {
        _run = run;
        Title = T("Edit Script");
        Width = 760;
        Height = 560;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _editor = new TextEditor
        {
            Text = script,
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 13,
            SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#"),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 4;
        _editor.TextArea.Caret.PositionChanged += (_, _) => UpdatePosition();
        _editor.TextArea.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                Run();
                e.Handled = true;
            }
        };

        var runButton = new Button { Content = T("Run (Ctrl+Enter)") };
        runButton.Click += (_, _) => Run();
        var ok = new Button { Content = T("OK"), MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            Result = _editor.Text;
            Close();
        };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();

        var top = new DockPanel { Margin = new Thickness(6) };
        DockPanel.SetDock(runButton, Dock.Left);
        top.Children.Add(runButton);
        DockPanel.SetDock(_position, Dock.Right);
        top.Children.Add(_position);
        _status.Margin = new Thickness(8, 0);
        top.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var help = new TextBlock
        {
            Text = T("Add(...) adds an outline. Helpers: Circle(x, y, r), Rect(x, y, w, h), Line(x1, y1, x2, y2), Arc(x, y, r, start, sweep), Polyline(closed, x1, y1, ...). See docs/scripts.md."),
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(6, 0, 6, 4),
        };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(help, Dock.Bottom);
        root.Children.Add(help);
        root.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1), Child = _editor });
        Content = root;
        Opened += (_, _) =>
        {
            _editor.Focus();
            UpdatePosition();
        };
    }

    private void UpdatePosition() =>
        _position.Text = F("Line {0}, column {1}", _editor.TextArea.Caret.Line, _editor.TextArea.Caret.Column);

    /// <summary>Runs the script in the drawing and shows the result; a compile error selects its line.</summary>
    internal void Run()
    {
        var (outlines, error) = _run(_editor.Text);
        if (error == null)
        {
            _status.Foreground = Brushes.DarkGreen;
            _status.Text = F("{0} outlines.", outlines ?? 0);
            return;
        }
        _status.Foreground = Brushes.DarkRed;
        _status.Text = error;
        if (ErrorLine().Match(error) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var line) && line <= _editor.Document.LineCount)
        {
            var l = _editor.Document.GetLineByNumber(line);
            _editor.Select(l.Offset, l.Length);
            _editor.ScrollToLine(line);
            _editor.TextArea.Caret.Line = line;
            _editor.Focus();
        }
    }
}
