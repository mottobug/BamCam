using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// Edit ▸ Transform ▸ Numeric (modeless): move, rotate and scale from numbers, with a preview in the drawing. Stays
/// open, so that several steps can be applied one after another.
/// </summary>
public sealed class NumericTransformWindow : Window
{
    /// <summary>What the transform turns around and scales from.</summary>
    private enum CenterMode { Origin, Selection, Point }

    private readonly TextBox[] _move = Fields("0");
    private readonly TextBox[] _rotate = Fields("0");
    private readonly TextBox[] _scale = Fields("1");
    private readonly TextBox[] _center = Fields("0");
    private readonly CheckBox _uniform = new() { Content = T("Same scale for X, Y and Z") };
    private readonly ComboBox _centerMode = new()
    {
        ItemsSource = new[] { T("Origin"), T("Center of the selection"), T("This point") }, SelectedIndex = 1, Width = 220,
    };
    private readonly Grid _centerPoint = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), ColumnSpacing = 6 };
    private readonly CheckBox _copy = new() { Content = T("Transform a copy") };
    private readonly CheckBox _editMatrix = new() { Content = T("Enter the matrix") };
    private readonly TextBox _matrix = new()
    {
        AcceptsReturn = true, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
        FontSize = 12, Height = 92,
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 34 };
    private readonly Button _apply = new() { Content = T("Apply"), IsDefault = true, MinWidth = 90 };
    private readonly Func<BoundingBox> _selectionBounds;
    private readonly Func<int> _selectionCount;
    private readonly Action<Matrix4, bool> _applyTransform;
    private readonly Action<IReadOnlyList<(Point3 From, Point3 To)>?> _preview;
    private readonly List<Control> _numberParts = [];
    private bool _updating;

    private static TextBox[] Fields(string value) =>
        [.. Enumerable.Range(0, 3).Select(_ => new TextBox { Text = value, Width = 92, HorizontalContentAlignment = HorizontalAlignment.Right })];

    /// <param name="applyTransform">Applies a matrix to the selection; true for a copy.</param>
    /// <param name="preview">Shows these lines in the drawing, or nothing.</param>
    public NumericTransformWindow(Func<BoundingBox> selectionBounds, Func<int> selectionCount, Action<Matrix4, bool> applyTransform,
        Action<IReadOnlyList<(Point3 From, Point3 To)>?> preview)
    {
        _selectionBounds = selectionBounds;
        _selectionCount = selectionCount;
        _applyTransform = applyTransform;
        _preview = preview;
        Title = T("Numeric Transform");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), ColumnSpacing = 6, RowSpacing = 6 };
        var row = 0;
        void AddRow(string label, Control[] controls)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            grid.Children.Add(text);
            for (var i = 0; i < controls.Length; i++)
            {
                Grid.SetRow(controls[i], row);
                Grid.SetColumn(controls[i], i + 1);
                grid.Children.Add(controls[i]);
            }
            row++;
        }
        TextBlock Head(string s) => new() { Text = s, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.Gray };
        AddRow("", [Head("X"), Head("Y"), Head("Z")]);
        AddRow(T("Scale"), _scale);
        AddRow(T("Rotate (degrees)"), _rotate);
        AddRow(T("Move"), _move);

        var centerGrid = _centerPoint;
        var centerLabel = new TextBlock { Text = T("Point"), VerticalAlignment = VerticalAlignment.Center };
        centerGrid.Children.Add(centerLabel);
        for (var i = 0; i < 3; i++)
        {
            Grid.SetColumn(_center[i], i + 1);
            centerGrid.Children.Add(_center[i]);
        }
        var centerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = T("Rotate and scale around"), VerticalAlignment = VerticalAlignment.Center }, _centerMode },
        };
        var order = new TextBlock
        {
            Text = T("Order: scale, rotate about X, then Y, then Z, then move."), FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
        };
        var reset = new Button { Content = T("Reset") };
        reset.Click += (_, _) => Reset();
        var close = new Button { Content = T("Close"), IsCancel = true, MinWidth = 80 };
        close.Click += (_, _) => Close();
        _apply.Click += (_, _) => Apply();
        var buttons = new DockPanel();
        DockPanel.SetDock(reset, Dock.Left);
        buttons.Children.Add(reset);
        buttons.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { _apply, close } });

        _numberParts.AddRange([grid, centerRow, centerGrid, _uniform]);
        Content = new StackPanel
        {
            Margin = new Thickness(12), Spacing = 8,
            Children = { grid, _uniform, centerRow, centerGrid, order, _copy, _editMatrix, _matrix, _status, buttons },
        };

        foreach (var box in _move.Concat(_rotate).Concat(_scale).Concat(_center)) box.TextChanged += (_, _) => Update();
        for (var i = 0; i < 3; i++)
        {
            var source = _scale[i];
            source.TextChanged += (_, _) => CopyScale(source);
        }
        _uniform.IsCheckedChanged += (_, _) => CopyScale(_scale[0]);
        _centerMode.SelectionChanged += (_, _) => Update();
        _editMatrix.IsCheckedChanged += (_, _) =>
        {
            _matrix.IsReadOnly = _editMatrix.IsChecked != true;
            Update();
        };
        _matrix.TextChanged += (_, _) =>
        {
            if (_editMatrix.IsChecked == true) Update();
        };
        Opened += (_, _) => Update();
        Closed += (_, _) => _preview(null);
        Update();
    }

    /// <summary>The selection changed in the drawing.</summary>
    public void SelectionChanged() => Update();

    /// <summary>Sets the numbers from outside (diagnostics).</summary>
    public void SetValues(double[] values)
    {
        var boxes = _scale.Concat(_rotate).Concat(_move).ToArray();
        for (var i = 0; i < boxes.Length && i < values.Length; i++) boxes[i].Text = values[i].ToString(CultureInfo.InvariantCulture);
    }

    private void CopyScale(TextBox source)
    {
        if (_uniform.IsChecked != true || _updating) return;
        _updating = true;
        foreach (var box in _scale)
            if (box != source) box.Text = source.Text;
        _updating = false;
        Update();
    }

    private void Reset()
    {
        _updating = true;
        foreach (var box in _move.Concat(_rotate)) box.Text = "0";
        foreach (var box in _scale) box.Text = "1";
        _editMatrix.IsChecked = false;
        _updating = false;
        Update();
    }

    private static Point3? Read(TextBox[] boxes)
    {
        var values = new double[3];
        for (var i = 0; i < 3; i++)
            if (!double.TryParse((boxes[i].Text ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
                return null;
        return new Point3(values[0], values[1], values[2]);
    }

    /// <summary>The matrix from the current input, or null with the reason in the status line.</summary>
    private Matrix4? CurrentMatrix()
    {
        if (_editMatrix.IsChecked == true)
        {
            if (NumericTransform.ParseMatrix(_matrix.Text ?? "") is { } typed) return typed;
            Show(T("The matrix needs 16 numbers in 4 rows, with 0 0 0 1 as the last column, and must not flatten the objects."), error: true);
            return null;
        }
        var mode = (CenterMode)Math.Max(_centerMode.SelectedIndex, 0);
        if (Read(_scale) is not { } scale || Read(_rotate) is not { } rotate || Read(_move) is not { } move || Read(_center) is not { } point)
        {
            Show(T("Please enter numbers in all fields."), error: true);
            return null;
        }
        var center = mode switch
        {
            CenterMode.Selection when _selectionCount() > 0 => _selectionBounds().Center,
            CenterMode.Point => point,
            _ => Point3.Zero,
        };
        var transform = new NumericTransform(move, rotate, scale, center);
        if (transform.IsValid) return transform.Matrix;
        Show(T("A scale of 0 is not possible."), error: true);
        return null;
    }

    private void Show(string text, bool error)
    {
        _status.Text = text;
        _status.Foreground = error ? Brushes.DarkRed : Brushes.Gray;
    }

    private void Update()
    {
        if (_updating) return;
        foreach (var part in _numberParts) part.IsEnabled = _editMatrix.IsChecked != true;
        _centerPoint.IsVisible = _centerMode.SelectedIndex == (int)CenterMode.Point;
        var count = _selectionCount();
        Show(count == 0 ? T("Select drawing objects first.") : F("{0} object(s) selected.", count), error: false);
        var matrix = CurrentMatrix();
        _apply.IsEnabled = matrix != null && count > 0;
        if (matrix is { } m && _editMatrix.IsChecked != true) _matrix.Text = NumericTransform.FormatMatrix(m);
        _preview(matrix is { IsIdentity: false } shown && count > 0 ? NumericTransform.BoxEdges(_selectionBounds(), shown) : null);
    }

    /// <summary>Applies the current values to the selection. The numbers stay for the next step.</summary>
    public void Apply()
    {
        if (CurrentMatrix() is not { } m || _selectionCount() == 0) return;
        _applyTransform(m, _copy.IsChecked == true);
        Update();
    }
}
