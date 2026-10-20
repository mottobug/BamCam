using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.App.Drawing;

/// <summary>
/// Interactive control-point editing (double-click an object): drag square handles to move points.
/// Polylines, lines and point lists expose their vertices; circles expose centre and rim.
/// Enter, middle click or Esc leaves the edit mode.
/// </summary>
public sealed class EditPointsTool(Entity entity) : ViewTool
{
    private int _dragIndex = -1;
    private bool _recorded;

    public override string Prompt => "Edit: drag the handles  [Enter / Esc = finish]";

    private List<Point2> Handles()
    {
        var m = entity.Transform;
        return entity switch
        {
            PolylineEntity p => p.Points.Select(v => m.Transform(v.Point).XY).ToList(),
            LineEntity l => l.Points.Select(v => m.Transform(v).XY).ToList(),
            PointListEntity pl => pl.WorldPoints().Select(v => v.XY).ToList(),
            CircleEntity c => [m.Transform(c.Center).XY, m.Transform(c.Center).XY + new Point2(c.Radius * m.ScaleXY, 0)],
            RectEntity r => r.LocalPath.Vertices.Where((_, i) => r.CornerRadius <= 0 || i % 2 == 0).Select(v => m.Transform(v.Point).XY).Take(4).ToList(),
            _ => [],
        };
    }

    protected override void OnAttached()
    {
        // Edit in drawing coordinates.
        if (!entity.Transform.IsIdentity && entity is PolylineEntity or LineEntity or PointListEntity or CircleEntity)
            entity.ApplyTransform(Matrix4.Identity);
        Host.Session.Select([entity]);
    }

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        var handles = Handles();
        for (var i = 0; i < handles.Count; i++)
        {
            var s = Host.View.ToScreen(handles[i]);
            if (Math.Abs(s.X - screen.X) <= 6 && Math.Abs(s.Y - screen.Y) <= 6)
            {
                _dragIndex = i;
                _recorded = false;
                return;
            }
        }
        // Point lists: clicking empty space adds a point.
        if (entity is PointListEntity pl)
        {
            Host.Session.Execute("Add point", () => pl.Points.Add(world.ToPoint3()));
            Host.Invalidate();
            return;
        }
        // Clicking elsewhere ends editing.
        Host.EndTool();
    }

    public override void PointerMoved(Point screen, Point2 world, KeyModifiers mods)
    {
        if (_dragIndex < 0) return;
        if (!_recorded)
        {
            Host.Session.BeginEdit();
            _recorded = true;
        }
        var i = _dragIndex;
        switch (entity)
        {
            case PolylineEntity p when i < p.Points.Count:
                p.Points[i] = p.Points[i] with { Point = new Point3(world.X, world.Y, p.Points[i].Point.Z) };
                break;
            case LineEntity l when i < l.Points.Count:
                l.Points[i] = new Point3(world.X, world.Y, l.Points[i].Z);
                break;
            case PointListEntity pl when i < pl.Points.Count:
                pl.Points[i] = new Point3(world.X, world.Y, pl.Points[i].Z);
                break;
            case CircleEntity c when i == 0:
                c.Center = new Point3(world.X, world.Y, c.Center.Z);
                break;
            case CircleEntity c:
                c.Diameter = 2 * Point2.Distance(c.Center.XY, world);
                break;
            case RectEntity r:
            {
                // Opposite corner stays fixed.
                var corners = new[]
                {
                    r.LowerLeft.XY, r.LowerLeft.XY + new Point2(r.Width, 0),
                    r.LowerLeft.XY + new Point2(r.Width, r.Height), r.LowerLeft.XY + new Point2(0, r.Height),
                };
                var fixedCorner = corners[(i + 2) % 4];
                var nr = RectEntity.FromCorners(fixedCorner, world, r.CornerRadius);
                r.LowerLeft = nr.LowerLeft with { Z = r.LowerLeft.Z };
                r.Width = nr.Width;
                r.Height = nr.Height;
                break;
            }
        }
        Host.Invalidate();
    }

    public override void PointerReleased(Point screen, Point2 world, MouseButton button, KeyModifiers mods)
    {
        if (button == MouseButton.Middle)
        {
            Host.EndTool();
            return;
        }
        if (_dragIndex >= 0 && _recorded) Host.Session.NotifyChanged();
        _dragIndex = -1;
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key is Key.Enter or Key.Return or Key.Escape)
        {
            Host.EndTool();
            return true;
        }
        if (key == Key.Delete && entity is PointListEntity or PolylineEntity && _dragIndex < 0)
            return false;
        return false;
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        var pen = new Pen(Brushes.Yellow, 1);
        foreach (var h in Handles())
        {
            var s = view.ToScreen(h);
            dc.DrawRectangle(Brushes.Black, pen, new Rect(s.X - 4, s.Y - 4, 8, 8));
        }
    }
}
