// The window of Draw ▸ Squircle: the values on the left, a preview on the right, as in the Squircle Pro plugin for
// CamBam by GeoffreyGRoy. Built in code with BamCam's user interface library Avalonia: also an example of a plugin
// with its own window (see docs/plugins.md).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.Core.Geometry;

namespace Squircle;

internal static class SquircleWindow
{
    /// <summary>
    /// Shows the window for <paramref name="values"/>, which it changes as the user types. <paramref name="make"/> runs
    /// on Make Squircle (new squircles: the window stays open for more) or on OK (editing: the window closes).
    /// </summary>
    public static async Task Show(SquircleEntity values, bool german, bool editing, Action make)
    {
        string T(string en, string de) => german ? de : en;
        var window = new Window
        {
            Title = T("Squircle", "Squircle"),
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var preview = new PreviewControl(values) { Width = 340, Height = 340 };
        var points = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var note = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 };
        var complexOnly = new List<Control>();
        NumericUpDown? angleBox = null, edgeBox = null;

        void Changed()
        {
            values.Invalidate();
            foreach (var c in complexOnly) c.IsEnabled = values.ShapeType == SquircleShape.Complex;
            if (angleBox != null) angleBox.IsEnabled = values.Accuracy == SquircleAccuracy.Angle;
            if (edgeBox != null) edgeBox.IsEnabled = values.Accuracy == SquircleAccuracy.EdgeLength;
            points.Text = values.NumberOfPoints.ToString();
            note.Text = values.Note;
            preview.InvalidateVisual();
        }

        // A number field: shows the value and writes it back on every change.
        NumericUpDown Number(double value, double minimum, double increment, Action<double> set)
        {
            var box = new NumericUpDown
            {
                Value = (decimal)value, Minimum = (decimal)minimum, Increment = (decimal)increment, FormatString = "0.#####",
                Width = 135, ClipValueToMinMax = true,
            };
            box.ValueChanged += (_, e) =>
            {
                if (e.NewValue is not { } v) return;
                set((double)v);
                Changed();
            };
            return box;
        }

        // Label and field in a row of the grid.
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,16,Auto,Auto"), RowSpacing = 6, ColumnSpacing = 6 };
        int row = -1;
        void Row() { row++; form.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); }
        void Put(Control c, int column, int span = 1)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            Grid.SetColumnSpan(c, span);
            form.Children.Add(c);
        }
        void Field(int column, string label, NumericUpDown box, bool complex = false)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Put(text, column);
            Put(box, column + 1);
            if (complex) complexOnly.AddRange([text, box]);
        }

        // Shape type.
        Row();
        var simple = new RadioButton { Content = T("Simple", "Einfach"), GroupName = "shape", IsChecked = values.ShapeType == SquircleShape.Simple };
        var complexRadio = new RadioButton { Content = T("Complex", "Komplex"), GroupName = "shape", IsChecked = values.ShapeType == SquircleShape.Complex };
        simple.IsCheckedChanged += (_, _) => { if (simple.IsChecked == true) { values.ShapeType = SquircleShape.Simple; Changed(); } };
        complexRadio.IsCheckedChanged += (_, _) => { if (complexRadio.IsChecked == true) { values.ShapeType = SquircleShape.Complex; Changed(); } };
        Put(new TextBlock { Text = T("Shape type", "Formtyp"), VerticalAlignment = VerticalAlignment.Center }, 0);
        Put(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { simple, complexRadio } }, 1, 4);

        // Shape values: n1 is used by both shapes, the others only by the complex one.
        Row();
        Field(0, "n1", Number(values.N1, 0.01, 0.1, v => values.N1 = v));
        Field(3, "m", Number(values.M, 0.01, 1, v => values.M = v), complex: true);
        Row();
        Field(0, "n2", Number(values.N2, 0, 0.1, v => values.N2 = v), complex: true);
        Field(3, "a", Number(values.A, 0.01, 0.1, v => values.A = v), complex: true);
        Row();
        Field(0, "n3", Number(values.N3, 0, 0.1, v => values.N3 = v), complex: true);
        Field(3, "b", Number(values.B, 0.01, 0.1, v => values.B = v), complex: true);

        // Size and position.
        Row();
        Put(new Border { Height = 6 }, 0);
        Row();
        Field(0, T("Rad-X", "Radius X"), Number(values.RadX, 0.001, 1, v => values.RadX = v));
        Field(3, T("Rad-Y", "Radius Y"), Number(values.RadY, 0.001, 1, v => values.RadY = v));
        Row();
        Field(0, T("Orig-X", "Mitte X"), Number(values.OrigX, -1e6, 1, v => values.OrigX = v));
        Field(3, T("Orig-Y", "Mitte Y"), Number(values.OrigY, -1e6, 1, v => values.OrigY = v));

        // Accuracy: by the angle of a segment at the centre, or by its length.
        Row();
        Put(new Border { Height = 6 }, 0);
        Row();
        var byAngle = new RadioButton { Content = T("Angle (°)", "Winkel (°)"), GroupName = "accuracy", IsChecked = values.Accuracy == SquircleAccuracy.Angle };
        var byEdge = new RadioButton { Content = T("Edge length", "Kantenlänge"), GroupName = "accuracy", IsChecked = values.Accuracy == SquircleAccuracy.EdgeLength };
        byAngle.IsCheckedChanged += (_, _) => { if (byAngle.IsChecked == true) { values.Accuracy = SquircleAccuracy.Angle; Changed(); } };
        byEdge.IsCheckedChanged += (_, _) => { if (byEdge.IsChecked == true) { values.Accuracy = SquircleAccuracy.EdgeLength; Changed(); } };
        angleBox = Number(values.Angle, 0.01, 0.5, v => values.Angle = v);
        edgeBox = Number(values.EdgeLength, 0.001, 0.1, v => values.EdgeLength = v);
        Put(byAngle, 0);
        Put(angleBox, 1);
        Put(byEdge, 3);
        Put(edgeBox, 4);
        Row();
        Put(new TextBlock { Text = T("No. of points", "Anzahl Punkte"), VerticalAlignment = VerticalAlignment.Center }, 0);
        Put(points, 1);
        Row();
        Put(note, 0, 5);

        // Buttons.
        var makeButton = new Button { Content = editing ? T("OK", "OK") : T("Make Squircle", "Squircle erzeugen"), IsDefault = true };
        var closeButton = new Button { Content = editing ? T("Cancel", "Abbrechen") : T("Close", "Schließen"), IsCancel = true };
        makeButton.Click += (_, _) =>
        {
            make();
            if (editing) window.Close();
        };
        closeButton.Click += (_, _) => window.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { makeButton, closeButton } };

        var left = new DockPanel { Width = 520 };
        DockPanel.SetDock(buttons, Dock.Bottom);
        left.Children.Add(buttons);
        left.Children.Add(form);
        var frame = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brushes.White, Child = preview };
        window.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(12), Children = { left, frame } };
        Changed();

        // BamCam's main window as the owner: the window stays in front of it and in its middle.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            await window.ShowDialog(owner);
        else
            window.Show();
    }

    /// <summary>The outline of the squircle, fitted into the control, with the axes through its centre.</summary>
    private sealed class PreviewControl(SquircleEntity values) : Control
    {
        private static readonly IPen OutlinePen = new Pen(Brushes.Red, 1.5);
        private static readonly IPen AxisPen = new Pen(Brushes.LightGray, 1);

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            var points = values.ToPaths().FirstOrDefault()?.Flatten() ?? [];
            var centre = new Point2(values.OrigX, values.OrigY);
            // The scale fits the shape around its centre, so the axes stay in the middle.
            var reach = points.Count == 0 ? 1 : points.Max(p => Math.Max(Math.Abs(p.X - centre.X), Math.Abs(p.Y - centre.Y)));
            var scale = (Math.Min(size.Width, size.Height) / 2 - 12) / Math.Max(reach, 1e-9);
            var mid = new Point(size.Width / 2, size.Height / 2);
            context.DrawLine(AxisPen, new Point(0, mid.Y), new Point(size.Width, mid.Y));
            context.DrawLine(AxisPen, new Point(mid.X, 0), new Point(mid.X, size.Height));
            if (points.Count < 2) return;
            Point ToScreen(Point2 p) => new(mid.X + (p.X - centre.X) * scale, mid.Y - (p.Y - centre.Y) * scale);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(ToScreen(points[0]), false);
                foreach (var p in points.Skip(1)) g.LineTo(ToScreen(p));
                g.EndFigure(true);
            }
            context.DrawGeometry(null, OutlinePen, geometry);
        }
    }
}
