using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.Core.Geometry;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// Edit ▸ Transform ▸ Resize (issue 24), after CamBam's resize dialog: per axis the current size, the new size and the
/// percentage, each axis switched on or off; Preserve Aspect Ratio scales the switched-on axes alike; mm → inch and
/// inch → mm convert with one click. The objects are scaled about the centre of the selection or the drawing origin.
/// </summary>
public sealed class ResizeDialog : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Format = "0.####";

    private readonly double[] _size;
    private readonly double[] _factor = [1, 1, 1];
    private readonly CheckBox[] _use = new CheckBox[3];
    private readonly TextBox[] _newSize = new TextBox[3];
    private readonly TextBox[] _percent = new TextBox[3];
    private readonly CheckBox _aspect = new() { IsChecked = true };
    private readonly ComboBox _about = new() { MinWidth = 180 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, MaxWidth = 380, Foreground = Brushes.DarkRed };
    private bool _updating;
    private (double X, double Y, double Z, bool AboutOrigin)? _result;

    private ResizeDialog(BoundingBox bounds)
    {
        Title = T("Resize");
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _size = [bounds.Width, bounds.Height, bounds.Max.Z - bounds.Min.Z];

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,110,80"), RowSpacing = 4, ColumnSpacing = 8 };
        void Cell(Control c, int row, int col)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            grid.Children.Add(c);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell(new TextBlock { Text = T("Current Size"), Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Right }, 0, 2);
        Cell(new TextBlock { Text = T("New Size"), Foreground = Brushes.Gray }, 0, 3);
        Cell(new TextBlock { Text = "%", Foreground = Brushes.Gray }, 0, 4);
        string[] axes = ["X", "Y", "Z"];
        for (var i = 0; i < 3; i++)
        {
            var axis = i;
            var row = i + 1;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            // An axis without extent (Z of a flat drawing) is off; its factor still follows Preserve Aspect Ratio.
            _use[i] = new CheckBox { Content = axes[i], IsChecked = _size[i] > 1e-9, VerticalAlignment = VerticalAlignment.Center };
            _use[i].IsCheckedChanged += (_, _) => Refresh();
            Cell(_use[i], row, 0);
            Cell(new TextBlock { Text = _size[i].ToString(Format, Inv), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 70 }, row, 2);
            _newSize[i] = new TextBox { HorizontalContentAlignment = HorizontalAlignment.Right, IsEnabled = _size[i] > 1e-9 };
            _newSize[i].TextChanged += (_, _) =>
            {
                if (_updating || Read(_newSize[axis]) is not { } v || _size[axis] <= 1e-9) return;
                SetFactor(axis, v / _size[axis], _newSize[axis]);
            };
            Cell(_newSize[i], row, 3);
            _percent[i] = new TextBox { HorizontalContentAlignment = HorizontalAlignment.Right };
            _percent[i].TextChanged += (_, _) =>
            {
                if (_updating || Read(_percent[axis]) is not { } v) return;
                SetFactor(axis, v / 100, _percent[axis]);
            };
            Cell(_percent[i], row, 4);
        }

        _aspect.Content = T("Preserve Aspect Ratio");
        _aspect.IsCheckedChanged += (_, _) =>
        {
            if (_aspect.IsChecked != true) return;
            // On again: the first switched-on axis gives the factor for all of them.
            var first = Enumerable.Range(0, 3).FirstOrDefault(i => _use[i].IsChecked == true);
            SetFactor(first, _factor[first], null);
        };

        var toInch = new Button { Content = T("mm → inch") };
        ToolTip.SetTip(toInch, T("Scales the switched-on axes by 1 / 25.4: a drawing made in millimetres becomes inches."));
        toInch.Click += (_, _) => SetAll(1 / 25.4);
        var toMm = new Button { Content = T("inch → mm") };
        ToolTip.SetTip(toMm, T("Scales the switched-on axes by 25.4: a drawing made in inches becomes millimetres."));
        toMm.Click += (_, _) => SetAll(25.4);
        var reset = new Button { Content = T("Reset") };
        reset.Click += (_, _) => SetAll(1);
        var units = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        units.Children.Add(toInch);
        units.Children.Add(toMm);
        units.Children.Add(reset);

        _about.ItemsSource = new[] { T("Centre of the selection"), T("Drawing origin") };
        _about.SelectedIndex = 0;
        var about = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        about.Children.Add(new TextBlock { Text = T("Resize about:"), VerticalAlignment = VerticalAlignment.Center });
        about.Children.Add(_about);

        var ok = new Button { Content = T("Apply"), IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(10), Spacing = 8 };
        root.Children.Add(grid);
        root.Children.Add(_aspect);
        root.Children.Add(units);
        root.Children.Add(about);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        Content = root;
        Refresh();
    }

    /// <summary>Shows the dialog for objects with these bounds; the scale factors and the centre, or null when cancelled.</summary>
    public static async Task<(double X, double Y, double Z, bool AboutOrigin)?> Ask(Window owner, BoundingBox bounds)
    {
        var dialog = new ResizeDialog(bounds);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    /// <summary>Diagnostics: the dialog for these bounds, to be shown without waiting.</summary>
    internal static ResizeDialog Preview(BoundingBox bounds) => new(bounds);

    private static double? Read(TextBox box)
    {
        var s = (box.Text ?? "").Trim().TrimEnd('%').Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : null;
    }

    /// <summary>A new factor for one axis (from its size or percentage); with Preserve Aspect Ratio for all switched-on axes.</summary>
    private void SetFactor(int axis, double factor, TextBox? typedIn)
    {
        _factor[axis] = factor;
        if (_aspect.IsChecked == true)
            for (var i = 0; i < 3; i++)
                if (_use[i].IsChecked == true || _size[i] <= 1e-9) _factor[i] = factor;
        Refresh(typedIn);
    }

    private void SetAll(double factor)
    {
        for (var i = 0; i < 3; i++)
            if (_use[i].IsChecked == true || _size[i] <= 1e-9) _factor[i] = factor;
        Refresh();
    }

    /// <summary>Shows the factors in the fields (not in the one being typed in) and checks them.</summary>
    private void Refresh(TextBox? typedIn = null)
    {
        _updating = true;
        for (var i = 0; i < 3; i++)
        {
            var on = _use[i].IsChecked == true;
            var f = on || _size[i] <= 1e-9 ? _factor[i] : 1;
            if (_newSize[i] != typedIn) _newSize[i].Text = (_size[i] * f).ToString(Format, Inv);
            if (_percent[i] != typedIn) _percent[i].Text = (f * 100).ToString("0.###", Inv);
            _newSize[i].IsEnabled = on && _size[i] > 1e-9;
            _percent[i].IsEnabled = on;
        }
        _updating = false;
        _status.Text = Factors().Any(f => Math.Abs(f) < 1e-12) ? T("A size or percentage of 0 would flatten the objects.") : "";
    }

    /// <summary>The factors that are applied: 1 for a switched-off axis with extent.</summary>
    private double[] Factors() => Enumerable.Range(0, 3)
        .Select(i => _use[i].IsChecked == true || _size[i] <= 1e-9 ? _factor[i] : 1).ToArray();

    private void Accept()
    {
        var f = Factors();
        if (f.Any(x => Math.Abs(x) < 1e-12)) return;
        _result = (f[0], f[1], f[2], _about.SelectedIndex == 1);
        Close();
    }
}
