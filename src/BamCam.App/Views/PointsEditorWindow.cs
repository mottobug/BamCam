using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using BamCam.Core.Geometry;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>One editable row of the points table.</summary>
public sealed class PointRow : INotifyPropertyChanged
{
    private double _x, _y, _z, _bulge;

    public int Index { get; set; }
    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public double Z { get => _z; set => Set(ref _z, value); }
    /// <summary>Arc of the segment to the next point: tan(sweep/4); 0 = straight, 1 = semicircle CCW, -1 = CW.</summary>
    public double Bulge { get => _bulge; set => Set(ref _bulge, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref double field, double value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Table editor for the points of a polyline, line or point list (property grid "Points [...]").
/// Rows can be added, inserted, deleted and pasted from / copied to the clipboard (tab, semicolon or
/// space separated, spreadsheet friendly).
/// </summary>
public sealed class PointsEditorWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ObservableCollection<PointRow> _rows = [];
    private readonly DataGrid _grid;
    private readonly CheckBox? _closed;
    private readonly bool _withBulge;
    private bool _ok;

    public PointsEditorWindow(string title, IEnumerable<Vertex> points, bool withBulge, bool? closed)
    {
        Title = title;
        Width = withBulge ? 560 : 460;
        Height = 520;
        MinWidth = 380;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _withBulge = withBulge;

        foreach (var v in points) _rows.Add(new PointRow { X = v.Point.X, Y = v.Point.Y, Z = v.Point.Z, Bulge = v.Bulge });
        Renumber();

        _grid = new DataGrid
        {
            ItemsSource = _rows,
            AutoGenerateColumns = false,
            CanUserSortColumns = false,
            CanUserReorderColumns = false,
            SelectionMode = DataGridSelectionMode.Extended,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Margin = new Thickness(6),
        };
        _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(PointRow.Index)), IsReadOnly = true, Width = new DataGridLength(44) });
        foreach (var name in withBulge ? new[] { "X", "Y", "Z", "Bulge" } : ["X", "Y", "Z"])
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = name == "Bulge" ? T("Bulge") : name,
                Binding = new Binding(name) { StringFormat = "0.######" },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            });
        _grid.KeyDown += OnGridKeyDown;

        Button B(string text, Action a, string? tip = null)
        {
            var b = new Button { Content = text, MinWidth = 70, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (tip != null) ToolTip.SetTip(b, tip);
            b.Click += (_, _) => a();
            return b;
        }

        var tools = new WrapPanel { Margin = new Thickness(6, 6, 6, 0) };
        foreach (var b in new[]
                 {
                     B(T("Add"), AddRow, T("Append a point after the last one")),
                     B(T("Insert"), InsertRow, T("Insert a point before the selected one")),
                     B(T("Delete"), DeleteRows, T("Delete the selected points (Del)")),
                     B(T("Copy"), () => _ = CopyRows(), T("Copy all points as tab-separated text")),
                     B(T("Paste"), () => _ = PasteRows(), T("Replace the selection (or append) with points from the clipboard: one point per line, x y [z] [bulge]")),
                 })
        {
            b.Margin = new Thickness(0, 0, 4, 4);
            tools.Children.Add(b);
        }
        if (closed is { } c)
        {
            _closed = new CheckBox { Content = T("Closed"), IsChecked = c, Margin = new Thickness(8, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center };
            tools.Children.Add(_closed);
        }

        var ok = new Button { Content = T("OK"), IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) =>
        {
            _grid.CommitEdit();
            _ok = true;
            Close();
        };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var hint = new TextBlock
        {
            Text = withBulge ? T("Bulge = tan(arc angle / 4) of the segment to the next point: 0 straight, 1 half circle counter-clockwise, -1 clockwise.") : "",
            Margin = new Thickness(8, 0), FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var root = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(tools);
        root.Children.Add(buttons);
        root.Children.Add(hint);
        root.Children.Add(_grid);
        Content = root;
    }

    /// <summary>Shows the editor; returns the edited points and closed flag, or null when cancelled.</summary>
    public async Task<(List<Vertex> Points, bool Closed)?> ShowFor(Window owner)
    {
        await ShowDialog(owner);
        if (!_ok) return null;
        var pts = _rows.Select(r => new Vertex(new Point3(r.X, r.Y, r.Z), _withBulge ? r.Bulge : 0)).ToList();
        return (pts, _closed?.IsChecked == true);
    }

    private void Renumber()
    {
        for (var i = 0; i < _rows.Count; i++) _rows[i].Index = i + 1;
        // Index has no change notification: refresh the column by resetting the source.
        if (_grid != null)
        {
            _grid.ItemsSource = null;
            _grid.ItemsSource = _rows;
        }
    }

    private void AddRow()
    {
        var last = _rows.LastOrDefault();
        var row = last == null ? new PointRow() : new PointRow { X = last.X, Y = last.Y, Z = last.Z };
        _rows.Add(row);
        Renumber();
        _grid.SelectedItem = row;
        _grid.ScrollIntoView(row, null);
    }

    private void InsertRow()
    {
        var index = _grid.SelectedIndex < 0 ? 0 : _grid.SelectedIndex;
        var reference = index < _rows.Count ? _rows[index] : null;
        var row = reference == null ? new PointRow() : new PointRow { X = reference.X, Y = reference.Y, Z = reference.Z };
        _rows.Insert(index, row);
        Renumber();
        _grid.SelectedItem = row;
    }

    private void DeleteRows()
    {
        foreach (var r in _grid.SelectedItems.OfType<PointRow>().ToList()) _rows.Remove(r);
        Renumber();
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _grid.SelectedItems.Count > 0 && FocusManager?.GetFocusedElement() is not TextBox)
        {
            DeleteRows();
            e.Handled = true;
        }
        else if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) && FocusManager?.GetFocusedElement() is not TextBox)
        {
            _ = PasteRows();
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && FocusManager?.GetFocusedElement() is not TextBox)
        {
            _ = CopyRows();
            e.Handled = true;
        }
    }

    private async Task CopyRows()
    {
        var rows = _grid.SelectedItems.Count > 1 ? _grid.SelectedItems.OfType<PointRow>() : _rows;
        var text = string.Join("\n", rows.Select(r =>
            _withBulge ? FormattableString.Invariant($"{r.X}\t{r.Y}\t{r.Z}\t{r.Bulge}") : FormattableString.Invariant($"{r.X}\t{r.Y}\t{r.Z}")));
        if (Clipboard != null) await Clipboard.SetTextAsync(text);
    }

    private async Task PasteRows()
    {
        if (Clipboard == null) return;
        var text = await Clipboard.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return;
        var parsed = new List<PointRow>();
        foreach (var line in text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Accept tab, semicolon, space or comma separated values (comma only if no other separator is used).
            var sep = line.Contains('\t') ? '\t' : line.Contains(';') ? ';' : line.Trim().Contains(' ') ? ' ' : ',';
            var parts = line.Split(sep, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
            double V(int i) => i < parts.Length && double.TryParse(parts[i].Replace(',', sep == ',' ? ',' : '.'), NumberStyles.Float, Inv, out var d) ? d : 0;
            if (parts.Length < 2 || !double.TryParse(parts[0].Replace(',', sep == ',' ? ',' : '.'), NumberStyles.Float, Inv, out _)) continue;
            parsed.Add(new PointRow { X = V(0), Y = V(1), Z = V(2), Bulge = V(3) });
        }
        if (parsed.Count == 0) return;
        var selected = _grid.SelectedItems.OfType<PointRow>().ToList();
        var at = selected.Count > 0 ? _rows.IndexOf(selected[0]) : _rows.Count;
        foreach (var r in selected) _rows.Remove(r);
        at = Math.Min(at, _rows.Count);
        foreach (var r in parsed) _rows.Insert(at++, r);
        Renumber();
    }
}
