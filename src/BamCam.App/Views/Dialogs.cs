using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>Small modal dialogs built in code (prompted values, messages, multi-line text).</summary>
public static class Dialogs
{
    private static readonly Dictionary<string, string> LastValues = [];

    private static Window Frame(string title, Control content, double width = 360) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        Content = new Border { Padding = new Thickness(12), Child = content },
    };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons)
        {
            b.MinWidth = 76;
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            p.Children.Add(b);
        }
        return p;
    }

    /// <summary>Asks for a text value; remembers the last value per prompt.</summary>
    public static async Task<string?> Prompt(Window owner, string title, string label, string? defaultValue = null)
    {
        var key = title + "|" + label;
        var box = new TextBox { Text = LastValues.GetValueOrDefault(key, defaultValue ?? "") };
        var ok = new Button { Content = T("OK"), IsDefault = true };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(Buttons(ok, cancel));
        var win = Frame(title, panel);
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; win.Close(); };
        cancel.Click += (_, _) => win.Close();
        win.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await win.ShowDialog(owner);
        if (result != null) LastValues[key] = result;
        return result;
    }

    public static async Task<double?> PromptNumber(Window owner, string title, string label, double defaultValue)
    {
        var text = await Prompt(owner, title, label, defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (text == null) return null;
        return double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    /// <summary>Asks to choose one of <paramref name="options"/>; returns its index or null. Remembers the last choice per prompt.</summary>
    public static async Task<int?> PromptChoice(Window owner, string title, string label, IReadOnlyList<string> options, int defaultIndex = 0)
    {
        var key = title + "|" + label;
        var start = LastValues.TryGetValue(key, out var last) && int.TryParse(last, out var i) && i >= 0 && i < options.Count ? i : defaultIndex;
        var list = new ListBox { ItemsSource = options, SelectedIndex = start, MaxHeight = 220 };
        var ok = new Button { Content = T("OK"), IsDefault = true };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(list);
        panel.Children.Add(Buttons(ok, cancel));
        var win = Frame(title, panel);
        int? result = null;
        ok.Click += (_, _) => { result = list.SelectedIndex >= 0 ? list.SelectedIndex : null; win.Close(); };
        list.DoubleTapped += (_, _) => { result = list.SelectedIndex >= 0 ? list.SelectedIndex : null; win.Close(); };
        cancel.Click += (_, _) => win.Close();
        win.Opened += (_, _) => list.Focus();
        await win.ShowDialog(owner);
        if (result is { } r) LastValues[key] = r.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result;
    }

    public static async Task Message(Window owner, string title, string message)
    {
        var ok = new Button { Content = T("OK"), IsDefault = true, IsCancel = true };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Buttons(ok));
        var win = Frame(title, panel, 420);
        ok.Click += (_, _) => win.Close();
        await win.ShowDialog(owner);
    }

    /// <summary>
    /// A question with its own answers as buttons (the first is the default). Returns the index of the button, or null
    /// when the window is closed.
    /// </summary>
    /// <summary>Diagnostic (--answers): the buttons to press in the next questions, in order; each question is logged.</summary>
    public static Queue<int>? DiagnosticAnswers { get; set; }

    public static Action<string>? DiagnosticLog { get; set; }

    public static async Task<int?> Choose(Window owner, string title, string message, params string[] answers)
    {
        if (DiagnosticAnswers is { Count: > 0 } queue)
        {
            var pressed = queue.Dequeue();
            DiagnosticLog?.Invoke($"question '{title}': {message.Replace('\n', ' ')} [{string.Join(" | ", answers)}] -> {answers.ElementAtOrDefault(pressed)}");
            return pressed;
        }
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        int? result = null;
        Window? win = null;
        var buttons = answers.Select((a, i) =>
        {
            var b = new Button { Content = a, IsDefault = i == 0 };
            b.Click += (_, _) => { result = i; win?.Close(); };
            return b;
        }).ToArray();
        var row = Buttons(buttons);
        row.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.Orientation = Orientation.Vertical;
        foreach (var b in buttons) b.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(row);
        win = Frame(title, panel, 460);
        await win.ShowDialog(owner);
        return result;
    }

    /// <summary>Yes / No / Cancel question. Returns true, false or null (cancel).</summary>
    public static async Task<bool?> Ask(Window owner, string title, string message, bool withCancel = true)
    {
        var yes = new Button { Content = T("Yes"), IsDefault = true };
        var no = new Button { Content = T("No") };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(withCancel ? Buttons(yes, no, cancel) : Buttons(yes, no));
        var win = Frame(title, panel, 420);
        bool? result = null;
        yes.Click += (_, _) => { result = true; win.Close(); };
        no.Click += (_, _) => { result = false; win.Close(); };
        cancel.Click += (_, _) => win.Close();
        await win.ShowDialog(owner);
        return result;
    }

    /// <summary>Multi-line text entry (Draw ▸ Text).</summary>
    public static async Task<string?> EditText(Window owner, string title, string text)
    {
        var box = new TextBox { Text = text, AcceptsReturn = true, Height = 120, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = T("OK") };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(box);
        panel.Children.Add(Buttons(ok, cancel));
        var win = Frame(title, panel, 420);
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; win.Close(); };
        cancel.Click += (_, _) => win.Close();
        win.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await win.ShowDialog(owner);
        return result;
    }
}

/// <summary>Internal G-code editor window (Machining ▸ Edit Gcode).</summary>
public sealed class GCodeWindow : Window
{
    private readonly TextBox _editor;
    private readonly string _path;

    public GCodeWindow(string path)
    {
        _path = path;
        Title = F("G-Code - {0}", Path.GetFileName(path));
        Width = 720;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _editor = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas,Menlo,DejaVu Sans Mono,monospace"), FontSize = 12,
            Text = File.Exists(path) ? File.ReadAllText(path) : "",
        };
        var save = new Button { Content = T("Save") };
        save.Click += (_, _) => File.WriteAllText(_path, _editor.Text ?? "");
        var close = new Button { Content = T("Close") };
        close.Click += (_, _) => Close();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6) };
        bar.Children.Add(save);
        bar.Children.Add(close);
        bar.Children.Add(new TextBlock { Text = path, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray });
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_editor);
        Content = dock;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                File.WriteAllText(_path, _editor.Text ?? "");
                e.Handled = true;
            }
        };
    }
}
