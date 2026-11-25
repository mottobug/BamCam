using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.App.Controls;
using BamCam.Core.Geometry;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// ICommandContext.AskValues: a window with a field for every public property of an object (the property grid), and
/// optionally a preview of outlines that follows the values. Cancel puts the values back as they were.
/// </summary>
public sealed class ValuesDialog : Window
{
    private readonly object _values;
    private readonly Dictionary<PropertyInfo, object?> _before;
    private bool _ok;

    public ValuesDialog(string title, object values, Func<IEnumerable<Path2>?>? preview)
    {
        _values = values;
        _before = Editable(values).ToDictionary(p => p, p => p.GetValue(values));
        Title = title;
        Width = preview == null ? 440 : 820;
        Height = 460;
        MinWidth = 360;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var grid = new PropertyGrid { NameColumnWidth = 160, Title = title, SelectedObject = values };
        var ok = new Button { Content = T("OK"), IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            _ok = true;
            Close();
        };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        if (preview != null)
        {
            var view = new PreviewControl(preview);
            var frame = new Border
            {
                Width = 380, Margin = new Thickness(8, 8, 8, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                Background = Brushes.White, Child = view,
            };
            DockPanel.SetDock(frame, Dock.Right);
            root.Children.Add(frame);
            grid.ValueChanged += (_, _) => view.InvalidateVisual();
        }
        root.Children.Add(grid);
        Content = root;
        // Closed with the window's own button or Esc: as Cancel.
        Closed += (_, _) =>
        {
            if (!_ok) Restore();
        };
    }

    /// <summary>The properties the grid can change (public, readable and writable).</summary>
    private static IEnumerable<PropertyInfo> Editable(object values) =>
        values.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);

    private void Restore()
    {
        foreach (var (property, value) in _before)
        {
            try { property.SetValue(_values, value); }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException) { } // a setter that refuses its old value
        }
    }

    public async Task<bool> ShowFor(Window owner)
    {
        await ShowDialog(owner);
        return _ok;
    }

    /// <summary>The outlines of the plugin's preview, fitted into the control (Y up).</summary>
    private sealed class PreviewControl(Func<IEnumerable<Path2>?> preview) : Control
    {
        private static readonly IPen OutlinePen = new Pen(Brushes.Red, 1.5);
        private static readonly IPen AxisPen = new Pen(Brushes.LightGray, 1);

        public override void Render(DrawingContext context)
        {
            List<(List<Point2> Points, bool Closed)> outlines;
            try
            {
                outlines = (preview() ?? []).Select(p => (p.Flatten(), p.Closed)).Where(p => p.Item1.Count >= 2).ToList();
            }
            catch (Exception)
            {
                return; // values the plugin cannot draw yet: an empty preview
            }
            var size = Bounds.Size;
            if (outlines.Count == 0 || size.Width < 20 || size.Height < 20) return;
            var all = outlines.SelectMany(p => p.Points).ToList();
            double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
            var scale = Math.Min((size.Width - 24) / Math.Max(maxX - minX, 1e-9), (size.Height - 24) / Math.Max(maxY - minY, 1e-9));
            var mid = new Point2((minX + maxX) / 2, (minY + maxY) / 2);
            Point ToScreen(Point2 p) => new(size.Width / 2 + (p.X - mid.X) * scale, size.Height / 2 - (p.Y - mid.Y) * scale);
            // The axes through the drawing's origin, where it is inside the picture.
            var origin = ToScreen(new Point2(0, 0));
            if (origin.Y >= 0 && origin.Y <= size.Height) context.DrawLine(AxisPen, new Point(0, origin.Y), new Point(size.Width, origin.Y));
            if (origin.X >= 0 && origin.X <= size.Width) context.DrawLine(AxisPen, new Point(origin.X, 0), new Point(origin.X, size.Height));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
                foreach (var outline in outlines)
                {
                    g.BeginFigure(ToScreen(outline.Points[0]), false);
                    foreach (var p in outline.Points.Skip(1)) g.LineTo(ToScreen(p));
                    g.EndFigure(outline.Closed);
                }
            context.DrawGeometry(null, OutlinePen, geometry);
        }
    }
}
