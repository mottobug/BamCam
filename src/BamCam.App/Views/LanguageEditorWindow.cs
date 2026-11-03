using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.Core.Localization;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>One text of the language editor: English original, built-in translation and the user's own text.</summary>
public sealed class LanguageRow(string english, string builtIn, string own) : INotifyPropertyChanged
{
    private string _own = own;

    public string English { get; } = english;
    public string BuiltIn { get; } = builtIn;

    // Line breaks are shown and edited as \n so every text fits in one row.
    public string EnglishShown => Escape(English);
    public string BuiltInShown => Escape(BuiltIn);

    /// <summary>The user's own text (empty: the built-in translation is used).</summary>
    public string Own
    {
        get => _own;
        set
        {
            if (_own == value) return;
            _own = value;
            Changed?.Invoke(this);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OwnShown)));
        }
    }

    public string OwnShown
    {
        get => Escape(_own);
        set => Own = (value ?? "").Replace("\\n", "\n");
    }

    public bool IsValid => _own.Length == 0 || IsValidTranslation(English, _own);

    public event Action<LanguageRow>? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    private static string Escape(string s) => s.Replace("\n", "\\n");
}

/// <summary>
/// Tools ▸ Edit Language File: every text of the user interface with its built-in translation and an own text
/// that overrides it. Only the own texts are saved, to <c>lang/{code}.json</c> in the system folder, and apply at
/// once; texts left empty keep following the built-in translation (also after updates).
/// </summary>
public sealed class LanguageEditorWindow : Window
{
    /// <summary>Entry holding the language name, kept as it is (not a text of the interface).</summary>
    private const string NameKey = "_language";

    private readonly string _file;
    private readonly string _code;
    private readonly List<LanguageRow> _rows = [];
    private readonly Dictionary<string, string> _extra = [];
    private readonly DataGrid _grid;
    private readonly TextBox _search = new() { Watermark = T("Search"), MinWidth = 260 };
    private readonly CheckBox _onlyOwn = new() { Content = T("Only own texts"), Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Content = T("Save"), MinWidth = 80, IsEnabled = false };
    private bool _dirty;

    public LanguageEditorWindow(string folder, string code, string languageName)
    {
        _code = code;
        _file = Path.Combine(folder, code + ".json");
        Title = F("Language File: {0}", languageName);
        Width = 1000;
        Height = 680;
        MinWidth = 520;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var own = ReadOwn(out var error);
        var builtIn = Tr.BuiltIn(code);
        // English has no built-in file; its texts are those of the built-in translations.
        foreach (var key in BuiltInKeys().Union(own.Keys.Where(k => k != NameKey), StringComparer.Ordinal)
                     .OrderBy(k => k.TrimStart('_'), StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new LanguageRow(key, builtIn.GetValueOrDefault(key, code == "en" ? key : ""), own.GetValueOrDefault(key, ""));
            row.Changed += _ => SetDirty();
            _rows.Add(row);
        }
        // Entries not shown in the table (the language name) are kept when saving.
        foreach (var kv in own.Where(kv => kv.Key == NameKey)) _extra[kv.Key] = kv.Value;

        _grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserSortColumns = true, CanUserResizeColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column,
            Margin = new Thickness(6, 0, 6, 0),
        };
        _grid.Columns.Add(new DataGridTextColumn { Header = T("English"), Binding = new Binding(nameof(LanguageRow.EnglishShown)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        if (code != "en")
            _grid.Columns.Add(new DataGridTextColumn { Header = T("Built-in"), Binding = new Binding(nameof(LanguageRow.BuiltInShown)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = T("Own text"), Binding = new Binding(nameof(LanguageRow.OwnShown)) { Mode = BindingMode.TwoWay }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        _search.TextChanged += (_, _) => Filter();
        _onlyOwn.IsCheckedChanged += (_, _) => Filter();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6), Children = { _search, _onlyOwn } };
        _onlyOwn.VerticalAlignment = VerticalAlignment.Center;

        var hint = new TextBlock
        {
            Text = T("Double-click a cell in the column Own text (or press F2) to change a text; leave it empty to use the built-in text. Keep placeholders like {0}; an _ in front of a letter marks the access key in menus; \\n is a line break."),
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(8, 0, 8, 6),
        };

        var open = new Button { Content = T("Open Folder") };
        open.Click += (_, _) => MainWindow.OpenFolder(folder);
        _save.Click += (_, _) => Save();
        var close = new Button { Content = T("Close"), IsCancel = true, MinWidth = 80 };
        close.Click += (_, _) => Close();
        var buttons = new DockPanel { Margin = new Thickness(6) };
        DockPanel.SetDock(open, Dock.Left);
        buttons.Children.Add(open);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _save, close } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        _status.Margin = new Thickness(10, 0);
        buttons.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        root.Children.Add(_grid);
        Content = root;

        if (error != null) ShowStatus(F("The file {0} could not be read ({1}); saving replaces it.", _file, error), true);
        else ShowStatus(F("{0} own texts in {1}", own.Keys.Count(k => k != NameKey), _file), false);
        Filter();
        Closing += async (_, e) =>
        {
            if (!_dirty) return;
            e.Cancel = true;
            var answer = await Dialogs.Ask(this, Title ?? "", T("Save the changed texts?"));
            if (answer == null) return;
            if (answer == true && !Save()) return;
            _dirty = false;
            Close();
        };
    }

    /// <summary>The entries of the user's own file (empty when there is none).</summary>
    private Dictionary<string, string> ReadOwn(out string? error)
    {
        error = null;
        if (!File.Exists(_file)) return [];
        try
        {
            using var s = File.OpenRead(_file);
            return Parse(s);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return [];
        }
    }

    private void Filter()
    {
        var text = _search.Text?.Trim() ?? "";
        var onlyOwn = _onlyOwn.IsChecked == true;
        _grid.ItemsSource = _rows.Where(r => (!onlyOwn || r.Own.Length > 0) && (text.Length == 0 ||
            r.English.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
            r.BuiltIn.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
            r.Own.Contains(text, StringComparison.CurrentCultureIgnoreCase))).ToList();
    }

    private void SetDirty()
    {
        _dirty = true;
        _save.IsEnabled = true;
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Foreground = error ? Brushes.DarkRed : Brushes.DimGray;
    }

    /// <summary>Writes the own texts and applies them; refused while a text would break its placeholders.</summary>
    private bool Save()
    {
        if (_rows.FirstOrDefault(r => !r.IsValid) is { } bad)
        {
            ShowStatus(F("Not saved: the placeholders of \"{0}\" do not match the English text.", bad.Own), true);
            _search.Text = bad.English;
            return false;
        }
        var entries = _extra.Concat(_rows.Where(r => r.Own.Length > 0).Select(r => KeyValuePair.Create(r.English, r.Own))).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, Serialize(entries, [
                T("BamCam language file: own texts, replacing the built-in ones (Tools > Edit Language File)."),
                T("Left the English original, right the own text. Texts not listed here follow the built-in translation."),
            ]), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            ShowStatus(F("Could not save {0}: {1}", _file, ex.Message), true);
            return false;
        }
        _dirty = false;
        _save.IsEnabled = false;
        if (Tr.Language == _code) Tr.Reload();
        ShowStatus(F("Saved: {0} own texts. They apply at once.", entries.Count(e => e.Key != NameKey)), false);
        return true;
    }
}
