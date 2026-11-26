// Demo 3: a window of your own layout. Draw ▸ Rounded Rectangle (Demo)... opens a small window with number fields and a
// preview, built in code with Avalonia, BamCam's user interface library (the .csproj references it; BamCam brings it
// at run time). Use this when AskValues (Demo 2) is not enough, e.g. for buttons that do something while the window
// stays open. A larger example: plugins/BamCam.SquirclePlugin/SquircleWindow.cs.
//
// The essentials:
// - BamCam's main window is the owner: the window stays in front of it, and ShowDialog waits until it is closed.
// - The window only changes its own values; the drawing changes after OK, through ctx (one undo step).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace BamCam.Example;

internal static class Demo3_OwnWindow
{
    private static double _width = 60, _height = 40, _radius = 5;

    public static void Register(IPluginHost host, bool german) =>
        host.AddCommand(new PluginCommand
        {
            Header = german ? "_Abgerundetes Rechteck (Demo)..." : "_Rounded Rectangle (Demo)...",
            Menu = "Draw",
            Description = german ? "Eigenes Fenster mit Avalonia" : "An own window with Avalonia",
            Execute = ctx => Run(ctx, german),
        });

    private static async Task Run(ICommandContext ctx, bool german)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner }) return;

        // The values the window works on (copies: Cancel leaves the last ones unchanged).
        double width = _width, height = _height, radius = _radius;

        var preview = new PreviewControl(() => Outline(width, height, radius)) { Width = 220, Height = 220 };
        NumericUpDown Field(double value, Action<double> set)
        {
            var box = new NumericUpDown { Value = (decimal)value, Minimum = 0, Increment = 1, FormatString = "0.###", Width = 140 };
            box.ValueChanged += (_, e) =>
            {
                if (e.NewValue is not { } v) return;
                set((double)v);
                preview.InvalidateVisual();
            };
            return box;
        }

        // Labels and fields in a grid of two columns.
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 6, ColumnSpacing = 8 };
        void Row(int row, string label, Control field)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            form.Children.Add(text);
            form.Children.Add(field);
        }
        Row(0, german ? "Breite" : "Width", Field(width, v => width = v));
        Row(1, german ? "Höhe" : "Height", Field(height, v => height = v));
        Row(2, german ? "Eckenradius" : "Corner radius", Field(radius, v => radius = v));

        var window = new Window
        {
            Title = german ? "Abgerundetes Rechteck" : "Rounded Rectangle",
            SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var ok = false;
        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancelButton = new Button { Content = german ? "Abbrechen" : "Cancel", IsCancel = true, MinWidth = 80 };
        okButton.Click += (_, _) => { ok = true; window.Close(); };
        cancelButton.Click += (_, _) => window.Close();
        var left = new StackPanel
        {
            Spacing = 12,
            Children = { form, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { okButton, cancelButton } } },
        };
        var frame = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brushes.White, Child = preview };
        window.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12), Children = { left, frame } };

        await window.ShowDialog(owner);
        if (!ok || width <= 0 || height <= 0) return;

        (_width, _height, _radius) = (width, height, radius);
        ctx.AddEntities(german ? "Abgerundetes Rechteck zeichnen" : "Draw rounded rectangle", [new PolylineEntity(Outline(width, height, radius))]);
    }

    /// <summary>A rectangle around the origin with rounded corners (arcs as bulges: 0.4142 = 90°).</summary>
    private static Path2 Outline(double w, double h, double r)
    {
        r = Math.Clamp(r, 0, Math.Min(w, h) / 2);
        double x = w / 2, y = h / 2;
        var bulge = r > 0 ? Math.Tan(Math.PI / 8) : 0;
        Vertex V(double px, double py, double b = 0) => new(new Point3(px, py, 0), b);
        return new Path2(
        [
            V(-x + r, -y), V(x - r, -y, bulge), V(x, -y + r), V(x, y - r, bulge),
            V(x - r, y), V(-x + r, y, bulge), V(-x, y - r), V(-x, -y + r, bulge),
        ], true);
    }

    /// <summary>Draws the outline fitted into the control.</summary>
    private sealed class PreviewControl(Func<Path2> outline) : Control
    {
        public override void Render(DrawingContext context)
        {
            var points = outline().Flatten();
            if (points.Count < 2) return;
            double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
            var scale = Math.Min((Bounds.Width - 20) / Math.Max(maxX - minX, 1e-9), (Bounds.Height - 20) / Math.Max(maxY - minY, 1e-9));
            Point ToScreen(Point2 p) => new(Bounds.Width / 2 + (p.X - (minX + maxX) / 2) * scale, Bounds.Height / 2 - (p.Y - (minY + maxY) / 2) * scale);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(ToScreen(points[0]), false);
                foreach (var p in points.Skip(1)) g.LineTo(ToScreen(p));
                g.EndFigure(true);
            }
            context.DrawGeometry(null, new Pen(Brushes.Red, 1.5), geometry);
        }
    }
}
