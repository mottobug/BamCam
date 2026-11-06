using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using BamCam.App.Services;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>What the editor does with a script file of the Scripts menu.</summary>
/// <param name="Run">Runs the text under a name; returns an error text or null.</param>
/// <param name="Saved">The text was saved: written by the user, so it may run.</param>
public sealed record ScriptFileActions(Func<string, string, Task<string?>> Run, Action<string> Saved);

/// <summary>
/// C# script editor: line numbers, C# highlighting, Check (F7) lists all compile errors without running anything,
/// Run (Ctrl+Enter). For a script object Run shows the result in the drawing at once, OK keeps the script and Cancel
/// restores the old one. For a script of the Scripts menu it has New, Save and Save As instead.
/// </summary>
public sealed partial class ScriptEditorWindow : Window
{
    private readonly TextEditor _editor;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _position = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly ListBox _problems = new() { MaxHeight = 130, IsVisible = false, FontSize = 12 };
    private readonly Func<string, (int? Outlines, string? Error)>? _run;
    private readonly Func<string, IReadOnlyList<ScriptProblem>> _check;
    private readonly ScriptFileActions? _file;
    private readonly Button _runButton = new() { Content = T("Run (Ctrl+Enter)") };
    private string? _path;
    private bool _modified;
    private bool _closeConfirmed;

    /// <summary>The script when closed with OK, else null.</summary>
    public string? Result { get; private set; }

    // "Line 5: …" in any interface language: the first number before the colon.
    [GeneratedRegex(@"^\D*(\d+):")]
    private static partial Regex ErrorLine();

    /// <summary>Editor of a script object's script.</summary>
    public ScriptEditorWindow(string script, Func<string, (int? Outlines, string? Error)> run)
        : this(script, ScriptRunner.CheckObject)
    {
        _run = run;
        Title = T("Edit Script");
        var ok = new Button { Content = T("OK"), MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            Result = _editor.Text;
            Close();
        };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        BuildLayout([], [ok, cancel], T("Add(...) adds an outline. Helpers: Circle(x, y, r), Rect(x, y, w, h), Line(x1, y1, x2, y2), Arc(x, y, r, start, sweep), Polyline(closed, x1, y1, ...). See docs/scripts.md."));
    }

    /// <summary>Editor of a script file of the Scripts menu (<paramref name="path"/> null: not saved yet).</summary>
    public ScriptEditorWindow(string script, string? path, ScriptFileActions file)
        : this(script, ScriptRunner.CheckMenuScript)
    {
        _file = file;
        _path = path;
        var newButton = new Button { Content = T("New") };
        newButton.Click += async (_, _) => await NewScript();
        var save = new Button { Content = T("Save (Ctrl+S)") };
        save.Click += async (_, _) => await Save();
        var saveAs = new Button { Content = T("Save As...") };
        saveAs.Click += async (_, _) => await SaveAs();
        var close = new Button { Content = T("Close"), IsCancel = true, MinWidth = 80 };
        close.Click += (_, _) => Close();
        BuildLayout([newButton, save, saveAs], [close], T("Add(...) adds an outline. Also: Document, Selection, Change(\"name\", () => ...), Select(...), Log(\"text\"), await AskNumber(\"title\", \"label\", 10), await AskText(...). See docs/scripts.md."));
        UpdateTitle();
        _editor.TextChanged += (_, _) =>
        {
            _modified = true;
            UpdateTitle();
        };
        Closing += OnClosing;
    }

    private ScriptEditorWindow(string script, Func<string, IReadOnlyList<ScriptProblem>> check)
    {
        _check = check;
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
        _editor.TextArea.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                await RunClicked();
            }
            else if (e.Key == Key.F7)
            {
                e.Handled = true;
                await Check();
            }
            else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _file != null)
            {
                e.Handled = true;
                await Save();
            }
        };
        _problems.SelectionChanged += (_, _) =>
        {
            if (_problems.SelectedItem is ProblemRow row) GoToLine(row.Problem.Line, row.Problem.Column);
        };
        _runButton.Click += async (_, _) => await RunClicked();
        Opened += (_, _) =>
        {
            _editor.Focus();
            UpdatePosition();
        };
    }

    private void BuildLayout(Button[] fileButtons, Button[] closeButtons, string helpText)
    {
        var check = new Button { Content = T("Check (F7)") };
        ToolTip.SetTip(check, T("Lists all errors the compiler finds, without running the script. Errors that only show while it runs are not found."));
        check.Click += async (_, _) => await Check();

        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var b in fileButtons) commands.Children.Add(b);
        commands.Children.Add(check);
        commands.Children.Add(_runButton);

        var top = new DockPanel { Margin = new Thickness(6) };
        DockPanel.SetDock(commands, Dock.Left);
        top.Children.Add(commands);
        DockPanel.SetDock(_position, Dock.Right);
        top.Children.Add(_position);
        _status.Margin = new Thickness(8, 0);
        top.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6) };
        foreach (var b in closeButtons) buttons.Children.Add(b);

        var help = new TextBlock { Text = helpText, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(6, 0, 6, 4) };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(help, Dock.Bottom);
        root.Children.Add(help);
        DockPanel.SetDock(_problems, Dock.Bottom);
        root.Children.Add(_problems);
        root.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1), Child = _editor });
        Content = root;
    }

    private sealed record ProblemRow(ScriptProblem Problem)
    {
        public override string ToString() => F("Line {0}, column {1}: {2}", Problem.Line, Problem.Column, Problem.Message);
    }

    private void UpdatePosition() =>
        _position.Text = F("Line {0}, column {1}", _editor.TextArea.Caret.Line, _editor.TextArea.Caret.Column);

    private void ShowStatus(string text, bool error)
    {
        _status.Foreground = error ? Brushes.DarkRed : Brushes.DarkGreen;
        _status.Text = text;
    }

    private void GoToLine(int line, int column = 0)
    {
        if (line < 1 || line > _editor.Document.LineCount) return;
        var l = _editor.Document.GetLineByNumber(line);
        _editor.Select(l.Offset, l.Length);
        _editor.ScrollToLine(line);
        _editor.TextArea.Caret.Line = line;
        if (column > 0) _editor.TextArea.Caret.Column = column;
        _editor.Focus();
    }

    /// <summary>Compiles without running; lists all errors and goes to the first. True when there are none.</summary>
    internal async Task<bool> Check()
    {
        var text = _editor.Text;
        ShowStatus(T("Checking..."), error: false);
        var problems = await Task.Run(() => _check(text));
        _problems.ItemsSource = problems.Select(p => new ProblemRow(p)).ToList();
        _problems.IsVisible = problems.Count > 0;
        if (problems.Count == 0)
        {
            ShowStatus(T("No errors found."), error: false);
            return true;
        }
        ShowStatus(F("{0} error(s) found.", problems.Count), error: true);
        GoToLine(problems[0].Line, problems[0].Column);
        return false;
    }

    private async Task RunClicked()
    {
        if (_file == null)
        {
            Run();
            return;
        }
        if (!await Check()) return;
        _runButton.IsEnabled = false;
        ShowStatus(T("Script is running..."), error: false);
        try
        {
            var error = await _file.Run(_editor.Text, ScriptName);
            if (error == null) ShowStatus(T("Script finished."), error: false);
            else ShowStatus(error, error: true);
        }
        finally
        {
            _runButton.IsEnabled = true;
        }
    }

    /// <summary>Runs the script in the drawing and shows the result; a compile error selects its line.</summary>
    internal void Run()
    {
        _problems.IsVisible = false;
        var (outlines, error) = _run!(_editor.Text);
        if (error == null)
        {
            ShowStatus(F("{0} outlines.", outlines ?? 0), error: false);
            return;
        }
        ShowStatus(error, error: true);
        if (ErrorLine().Match(error) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var line)) GoToLine(line);
    }

    // ------------------------------------------------------------------ script files

    private string ScriptName => _path == null ? T("New script") : Path.GetFileNameWithoutExtension(_path);

    private void UpdateTitle() => Title = ScriptName + (_modified ? " *" : "");

    private async Task<bool> Save()
    {
        if (_path == null) return await SaveAs();
        return WriteTo(_path);
    }

    private async Task<bool> SaveAs()
    {
        Directory.CreateDirectory(ScriptLibrary.Folder);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Save script"),
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_path == null ? ScriptLibrary.Folder : Path.GetDirectoryName(_path)!),
            SuggestedFileName = _path == null ? T("New script") : Path.GetFileName(_path),
            DefaultExtension = ScriptLibrary.Extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(T("C# script")) { Patterns = ["*" + ScriptLibrary.Extension] }],
        });
        return file?.TryGetLocalPath() is { } path && WriteTo(path);
    }

    private bool WriteTo(string path)
    {
        try
        {
            File.WriteAllText(path, _editor.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(ex.Message, error: true);
            return false;
        }
        _path = path;
        _modified = false;
        _file!.Saved(_editor.Text);
        UpdateTitle();
        ShowStatus(F("Saved: {0}", path), error: false);
        return true;
    }

    /// <summary>Unsaved text: save it, drop it, or go back (false).</summary>
    private async Task<bool> ConfirmDiscard()
    {
        if (!_modified) return true;
        var answer = await Dialogs.Ask(this, ScriptName, T("Save the changes to this script?"));
        return answer == null ? false : answer == false || await Save();
    }

    private async Task NewScript()
    {
        if (!await ConfirmDiscard()) return;
        _editor.Text = ScriptLibrary.NewScript;
        _path = null;
        _modified = false;
        _problems.IsVisible = false;
        _status.Text = "";
        UpdateTitle();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !_modified) return;
        e.Cancel = true;
        if (!await ConfirmDiscard()) return;
        _closeConfirmed = true;
        Close();
    }
}
