using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.App.Controls;
using BamCam.Core.Cam;
using BamCam.IO.Cb;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// Edits one CAM style: its name, the style it inherits from and its values, shown for one kind of operation at a
/// time (a style holds the values of all kinds). Values left at Default are inherited from the parent style; every
/// change is saved to the library file at once.
/// </summary>
public sealed class StyleEditorWindow : Window
{
    /// <summary>Kinds of operations whose parameters a style can set.</summary>
    private static readonly Func<MachineOp>[] Kinds =
    [
        () => new ProfileOp(), () => new PocketOp(), () => new DrillOp(), () => new EngraveOp(), () => new Profile3DOp(),
        () => new VEngraveOp(), () => new DragKnifeOp(), () => new PlasmaOp(),
        () => new LatheOp(), () => new LatheFacingOp(), () => new LatheGrooveOp(), () => new LatheThreadOp(), () => new LatheDrillOp(),
    ];

    /// <summary>General settings of an operation that do not belong to a style.</summary>
    private static readonly HashSet<string> NotInStyles =
        [nameof(MachineOp.Name), nameof(MachineOp.Enabled), nameof(MachineOp.PrimitiveIds), nameof(MachineOp.Style), nameof(MachineOp.Tag)];

    private readonly CamStyleLibrary _library;
    private readonly CamStyle _style;
    private readonly bool _readOnly;
    private readonly Action _changed;
    private readonly PropertyGrid _grid = new() { NameColumnWidth = 190 };
    private readonly ComboBox _kind = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _parent = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new();
    private MachineOp _op;
    private bool _loading;

    public StyleEditorWindow(CamStyleLibrary library, CamStyle style, bool readOnly, Action changed)
    {
        _library = library;
        _style = style;
        _readOnly = readOnly;
        _changed = changed;
        Title = F("CAM Style: {0} ({1})", style, library.Name);
        Width = 520;
        Height = 700;
        MinWidth = 380;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _grid.PropertyFilter = p => !NotInStyles.Contains(p.Name);
        _grid.ValueChanged += (_, _) => Store();
        _grid.IsEnabled = !readOnly;

        var kinds = Kinds.Select(k => k()).ToList();
        _kind.ItemsSource = kinds.Select(k => T(k.DisplayType)).ToList();
        _kind.SelectedIndex = 0;
        _op = kinds[0];
        _kind.SelectionChanged += (_, _) =>
        {
            if (_kind.SelectedIndex < 0) return;
            _op = Kinds[_kind.SelectedIndex]();
            Load();
        };

        _name.Text = style.Name;
        _name.IsEnabled = !readOnly && style.Name.Length > 0; // the default style has no name
        _name.LostFocus += (_, _) => Rename();
        _name.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) Rename();
        };
        FillParents();
        _parent.IsEnabled = !readOnly && style.Name.Length > 0;
        _parent.SelectionChanged += (_, _) =>
        {
            if (_loading || _parent.SelectedItem is not string chosen) return;
            _style.ParentStyle = chosen == T("(default)") ? "" : chosen;
            Save();
            Load();
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(10, 10, 10, 6) };
        void Row(int row, string label, Control editor)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 10, 3) };
            Grid.SetRow(text, row);
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            editor.Margin = new Thickness(0, 3);
            header.Children.Add(text);
            header.Children.Add(editor);
        }
        Row(0, T("Name"), _name);
        Row(1, T("Parent Style"), _parent);
        Row(2, T("Values for"), _kind);

        var hint = new TextBlock
        {
            Text = readOnly
                ? T("This library belongs to CamBam and is read-only. Right-click it in the System tab and choose Copy to BamCam to edit it.")
                : T("Values set here (V, A) are part of the style; values marked D are inherited from the parent style. Use the D / V / A button of a value to inherit it again."),
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = readOnly ? Brushes.DarkRed : Brushes.DimGray, Margin = new Thickness(10, 0, 10, 6),
        };
        var close = new Button { Content = T("Close"), IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        close.Click += (_, _) => Close();

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(hint);
        root.Children.Add(close);
        root.Children.Add(_grid);
        Content = root;
        Load();
    }

    private void FillParents()
    {
        _loading = true;
        // Not the style itself nor styles inheriting from it (that would be a loop).
        var choices = _library.Styles.Where(s => s.Name.Length > 0 && s != _style && !_library.Chain(s.Name).Contains(_style))
            .Select(s => s.Name).Prepend(T("(default)")).ToList();
        _parent.ItemsSource = choices;
        _parent.SelectedItem = _style.ParentStyle.Length == 0 ? choices[0] : choices.FirstOrDefault(c => c == _style.ParentStyle) ?? choices[0];
        _loading = false;
    }

    private void Load()
    {
        LibraryFiles.LoadStyle(_library, _style, _op);
        _grid.SelectedObject = _op;
    }

    /// <summary>A value was edited: into the style, saved, and shown again with the inherited values updated.</summary>
    private void Store()
    {
        if (_readOnly) return;
        LibraryFiles.StoreStyle(_style, _op);
        Save();
        Load();
    }

    private void Rename()
    {
        var name = _name.Text?.Trim() ?? "";
        if (_readOnly || name == _style.Name) return;
        if (name.Length == 0 || _library.Styles.Any(s => s != _style && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _name.Text = _style.Name;
            return;
        }
        foreach (var child in _library.Styles.Where(s => s.ParentStyle == _style.Name)) child.ParentStyle = name;
        _style.Name = name;
        Title = F("CAM Style: {0} ({1})", _style, _library.Name);
        Save();
    }

    private void Save()
    {
        try
        {
            LibraryFiles.SaveStyles(_library, _library.SourcePath!);
        }
        catch (Exception ex)
        {
            Title = ex.Message;
        }
        _changed();
    }
}
