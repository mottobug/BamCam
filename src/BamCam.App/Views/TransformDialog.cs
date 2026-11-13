using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.Core.Geometry;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// Edits the transform of a machining operation as numbers: scale X/Y/Z and rotate about Z (about the origin), then
/// move. Scale -1 mirrors, e.g. for the second half of a mold. Matrices with other rotations (from CamBam) are shown
/// as they are and replaced only when a value is changed.
/// </summary>
public sealed class TransformDialog : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly TextBox[] _boxes;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, MaxWidth = 340, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Matrix4 _initial;
    private readonly bool _decomposable;
    private bool _edited;
    private Matrix4? _result;

    private TransformDialog(Matrix4 initial)
    {
        Title = T("Transform of the operation");
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _initial = initial;

        var parts = TransformParts.From(initial);
        _decomposable = parts != null;
        var p = parts ?? TransformParts.None;
        string[] labels = [T("Scale X"), T("Scale Y"), T("Scale Z"), T("Rotate about Z (°)"), T("Move X"), T("Move Y"), T("Move Z")];
        double[] values = [p.ScaleX, p.ScaleY, p.ScaleZ, p.RotateZ, p.MoveX, p.MoveY, p.MoveZ];

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,120"), RowSpacing = 4, ColumnSpacing = 8 };
        _boxes = new TextBox[labels.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, i);
            grid.Children.Add(label);
            var box = new TextBox { Text = values[i].ToString("0.######", Inv), HorizontalContentAlignment = HorizontalAlignment.Right };
            box.TextChanged += (_, _) =>
            {
                _edited = true;
                Update();
            };
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            _boxes[i] = box;
        }

        var hint = new TextBlock
        {
            Text = T("Scale and rotate about the drawing origin, then move. Scale X or Y -1 mirrors the operation, e.g. for the other half of a mold."),
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, MaxWidth = 340, Margin = new Thickness(0, 0, 0, 8),
        };

        var mirrorX = new Button { Content = T("Mirror X") };
        ToolTip.SetTip(mirrorX, T("Changes the sign of Scale X: mirrors about the Y axis."));
        mirrorX.Click += (_, _) => Negate(0);
        var mirrorY = new Button { Content = T("Mirror Y") };
        ToolTip.SetTip(mirrorY, T("Changes the sign of Scale Y: mirrors about the X axis."));
        mirrorY.Click += (_, _) => Negate(1);
        var reset = new Button { Content = T("Reset") };
        ToolTip.SetTip(reset, T("No transform (Identity)."));
        reset.Click += (_, _) =>
        {
            double[] none = [1, 1, 1, 0, 0, 0, 0];
            for (var i = 0; i < _boxes.Length; i++) _boxes[i].Text = none[i].ToString(Inv);
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        tools.Children.Add(mirrorX);
        tools.Children.Add(mirrorY);
        tools.Children.Add(reset);

        var ok = new Button { Content = T("OK"), IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(10) };
        root.Children.Add(hint);
        root.Children.Add(grid);
        root.Children.Add(tools);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        Content = root;
        _edited = false;
        Update();
    }

    /// <summary>Shows the dialog; the new matrix, or null when cancelled.</summary>
    public static async Task<Matrix4?> Edit(Window owner, Matrix4 current)
    {
        var dialog = new TransformDialog(current);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private void Negate(int index)
    {
        if (TryRead(index) is { } v) _boxes[index].Text = (-v).ToString("0.######", Inv);
    }

    private double? TryRead(int index)
    {
        var s = (_boxes[index].Text ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : null;
    }

    private TransformParts? Read()
    {
        var v = new double[_boxes.Length];
        for (var i = 0; i < v.Length; i++)
        {
            if (TryRead(i) is not { } x) return null;
            v[i] = x;
        }
        return new TransformParts(v[0], v[1], v[2], v[3], v[4], v[5], v[6]);
    }

    private void Update()
    {
        if (_boxes == null) return;
        if (Read() is not { } parts)
        {
            _status.Text = T("Enter a number in every field.");
            _status.Foreground = Brushes.DarkRed;
            return;
        }
        if (Math.Abs(parts.ScaleX) < 1e-12 || Math.Abs(parts.ScaleY) < 1e-12 || Math.Abs(parts.ScaleZ) < 1e-12)
        {
            _status.Text = T("A scale of 0 flattens the operation: use 1 for no scaling, -1 to mirror.");
            _status.Foreground = Brushes.DarkRed;
            return;
        }
        _status.Foreground = Brushes.Gray;
        // BamCam transforms the drawing objects before it computes the toolpath: a mirrored operation keeps its milling
        // direction (climb stays climb).
        _status.Text = !_decomposable && !_edited
            ? T("This matrix also turns about X or Y or is skewed (e.g. from CamBam). It stays as it is unless you change a value here.")
            : "";
    }

    private void Accept()
    {
        if (!_decomposable && !_edited)
        {
            _result = _initial;
            Close();
            return;
        }
        if (Read() is not { } parts || Math.Abs(parts.ScaleX) < 1e-12 || Math.Abs(parts.ScaleY) < 1e-12 || Math.Abs(parts.ScaleZ) < 1e-12) return;
        _result = parts.ToMatrix();
        Close();
    }
}
