using System.Globalization;
using Avalonia;
using Avalonia.Media;
using BamCam.App.Drawing;
using BamCam.Core.Geometry;
using BamCam.Core.Sketching;

namespace BamCam.App.Sketching;

public sealed partial class SketchTool
{
    private static readonly Color FreeColor = Color.FromRgb(142, 201, 255);
    private static readonly Color DeterminedColor = Color.FromRgb(110, 224, 122);
    private static readonly Color ErrorColor = Color.FromRgb(255, 90, 90);
    private static readonly Color SelectedColor = Color.FromRgb(255, 176, 32);
    private static readonly Color HoverColor = Color.FromRgb(255, 255, 255);
    private static readonly Color ConstructionColor = Color.FromRgb(150, 165, 185);

    /// <summary>Screen boxes of the constraint symbols and dimension labels drawn last, for clicking them.</summary>
    private readonly List<(Rect Rect, int Id)> _labels = [];

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        _labels.Clear();
        var bad = _result.Conflicting.Concat(_result.Redundant).Concat(_result.ExpressionErrors.Keys).ToHashSet();
        var badRefs = _s.Constraints.Where(c => bad.Contains(c.Id)).SelectMany(c => c.Refs).ToHashSet();

        Color ColorOf(int id, bool construction, bool determined)
        {
            if (_selection.Contains(id)) return SelectedColor;
            if (_hover.Id == id && _drag == DragKind.None) return HoverColor;
            if (badRefs.Contains(id)) return ErrorColor;
            if (construction) return ConstructionColor;
            return determined ? DeterminedColor : FreeColor;
        }

        // Geometry.
        foreach (var g in _s.Geometry.Where(g => g.Kind != SketchGeometryKind.Point))
        {
            var color = ColorOf(g.Id, g.Construction, _result.DeterminedGeometry.Contains(g.Id));
            var pen = new Pen(new SolidColorBrush(color), _selection.Contains(g.Id) ? 2.5 : 1.6,
                g.Construction ? new DashStyle([6, 4], 0) : null);
            var path = _s.PathOf(g);
            DrawPath(dc, view, _m.IsIdentity ? path : path.Transformed(_m), pen);
        }

        // Points: squares, the origin as a cross.
        var coincident = _s.Constraints.Where(c => c.Kind == ConstraintKind.Coincident).SelectMany(c => c.Refs).Select(_s.PointOf).ToHashSet();
        foreach (var p in _s.Points)
        {
            var s = ToScreen(p.Position);
            var color = ColorOf(p.Id, false, _result.DeterminedPoints.Contains(p.Id));
            if (_s.Geometry.Any(g => g.Kind == SketchGeometryKind.Point && g.Points[0] == p.Id) && !_selection.Contains(p.Id))
            {
                var pg = _s.Geometry.First(g => g.Kind == SketchGeometryKind.Point && g.Points[0] == p.Id);
                if (_selection.Contains(pg.Id)) color = SelectedColor;
            }
            var brush = new SolidColorBrush(color);
            if (p.Id == Sketch.OriginId)
            {
                var pen = new Pen(brush, 1.5);
                dc.DrawLine(pen, s + new Point(-7, 0), s + new Point(7, 0));
                dc.DrawLine(pen, s + new Point(0, -7), s + new Point(0, 7));
                continue;
            }
            var size = coincident.Contains(p.Id) ? 3.5 : 2.5;
            dc.FillRectangle(brush, new Rect(s.X - size, s.Y - size, 2 * size, 2 * size));
        }

        // Constraint symbols beside what they belong to, moved along until they do not cover each other.
        var placed = new List<Rect>();
        Point Free(Point pos)
        {
            for (var i = 0; i < 12 && placed.Any(r => r.Intersects(new Rect(pos.X - 8, pos.Y - 7, 16, 14))); i++)
                pos += new Point(18, 0);
            return pos;
        }
        // The old corners of fillets and chamfers (construction points) are held on their lines without a symbol.
        var cornerPoints = _s.Geometry.Where(g => g is { Kind: SketchGeometryKind.Point, Construction: true }).Select(g => g.Points[0]).ToHashSet();
        foreach (var c in _s.Constraints.Where(c => !c.IsDimension && c.Kind != ConstraintKind.Coincident))
        {
            if (c.Kind == ConstraintKind.PointOnObject && c.Refs.Length > 0 && cornerPoints.Contains(_s.PointOf(c.Refs[0]))) continue;
            var color = bad.Contains(c.Id) ? ErrorColor : _selection.Contains(c.Id) ? SelectedColor : _hover.Id == c.Id ? HoverColor : Color.FromRgb(230, 230, 230);
            var anchors = new List<int> { c.Refs.Length > 0 ? (c.Kind is ConstraintKind.PointOnObject or ConstraintKind.Midpoint ? _s.PointOf(c.Refs[0]) : c.Refs[0]) : 0 };
            // Touching at an end of an arc (a fillet): one symbol at that point.
            if (c.Kind == ConstraintKind.Tangent && c.Refs.Length == 2 && _s.Geo(c.Refs[1]) is { Kind: SketchGeometryKind.Arc } arc
                && (c.Data.Length == 2 && _s.Geo(c.Refs[0]) is { Kind: SketchGeometryKind.Line } || c.Data.Length == 4) && c.Data[^1] is 1 or 2)
                anchors = [arc.Points[(int)c.Data[^1]]];
            // Parallel, perpendicular, equal and tangent: at both.
            else if (c.Kind is ConstraintKind.Parallel or ConstraintKind.Perpendicular or ConstraintKind.Equal or ConstraintKind.Tangent && c.Refs.Length > 1)
                anchors.Add(c.Refs[1]);
            foreach (var anchorId in anchors)
            {
                var (at, normal) = Anchor(anchorId);
                var pos = Free(ToScreen(at) + new Point(normal.X, -normal.Y) * 14);
                var rect = Label(dc, Symbol(c.Kind), pos, color, small: true);
                placed.Add(rect);
                _labels.Add((rect, c.Id));
            }
        }

        // Dimensions.
        foreach (var c in _s.Constraints.Where(c => c.IsDimension)) DrawDimension(dc, c, bad.Contains(c.Id));

        // Selection box and the preview of what is being drawn.
        if (_drag == DragKind.Box && _moved)
        {
            var a = ToScreen(_pressLocal);
            var b = ToScreen(_cursor);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 176, 32)), new Pen(new SolidColorBrush(SelectedColor), 1, DashStyle.Dash), new Rect(a, b));
        }
        DrawPreview(dc, view);
    }

    private static string Symbol(ConstraintKind k) => k switch
    {
        ConstraintKind.Horizontal => "H",
        ConstraintKind.Vertical => "V",
        ConstraintKind.Parallel => "∥",
        ConstraintKind.Perpendicular => "⊥",
        ConstraintKind.Tangent => "T",
        ConstraintKind.Equal => "=",
        ConstraintKind.Fix => "Fix",
        ConstraintKind.PointOnObject => "On",
        ConstraintKind.Midpoint => "Mid",
        ConstraintKind.Symmetric => "Sym",
        _ => "?",
    };

    /// <summary>Where the symbols of a point or geometry go (sketch coordinates) and the side to put them (unit normal).</summary>
    private (Point2 At, Point2 Normal) Anchor(int id)
    {
        if (_s.Point(id) is { } p) return (p.Position, new Point2(0.7, 0.7));
        if (_s.Geo(id) is not { } g) return (default, new Point2(0, 1));
        switch (g.Kind)
        {
            case SketchGeometryKind.Line:
            {
                var a = _s.P(g.Points[0]);
                var b = _s.P(g.Points[1]);
                var d = (b - a).Length < 1e-12 ? new Point2(1, 0) : (b - a).Normalized();
                var n = new Point2(-d.Y, d.X);
                if (n.Y < 0 || Math.Abs(n.Y) < 1e-9 && n.X < 0) n = new Point2(-n.X, -n.Y);
                return ((a + b) * 0.5, n);
            }
            case SketchGeometryKind.Circle:
                return (_s.P(g.Points[0]) + new Point2(0, g.Radius), new Point2(0, 1));
            case SketchGeometryKind.Arc:
            {
                var c = _s.P(g.Points[0]);
                var s = _s.P(g.Points[1]);
                var sweep = Sketch.ArcSweep(c, s, _s.P(g.Points[2]));
                var dir = Point2.FromAngle((s - c).Angle + sweep / 2, 1);
                return (c + dir * Point2.Distance(c, s), dir);
            }
            default:
                return (_s.P(g.Points[0]), new Point2(0.7, 0.7));
        }
    }

    /// <summary>A text with a dark box behind it at a screen position (its centre); returns the box.</summary>
    private static Rect Label(DrawingContext dc, string text, Point center, Color color, bool small = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, small ? 10 : 12,
            new SolidColorBrush(color));
        var rect = new Rect(center.X - ft.Width / 2 - 3, center.Y - ft.Height / 2 - 1, ft.Width + 6, ft.Height + 2);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 25, 25, 32)), new Pen(new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B)), 1), rect, 3, 3);
        dc.DrawText(ft, new Point(rect.X + 3, rect.Y + 1));
        return rect;
    }

    private void DrawDimension(DrawingContext dc, SketchConstraint c, bool bad)
    {
        var color = bad ? ErrorColor : _selection.Contains(c.Id) ? SelectedColor : _hover.Id == c.Id ? HoverColor
            : c.Reference ? ConstructionColor : Color.FromRgb(240, 220, 120);
        var pen = new Pen(new SolidColorBrush(color), 1);
        var measured = _s.Measure(c);
        var valueText = SketchCommands.Format(measured) + (c.Kind == ConstraintKind.Angle ? "°" : "");
        var expr = c.Expression?.Trim() ?? "";
        var plainNumber = double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        // Along X or Y, as opposed to the direct distance (which has no mark).
        var prefix = c.Kind switch
        {
            ConstraintKind.Radius => "R",
            ConstraintKind.Diameter => "Ø",
            ConstraintKind.HorizontalDistance => "X ",
            ConstraintKind.VerticalDistance => "Y ",
            _ => "",
        };
        var text = c.Reference ? $"({prefix}{valueText})"
            : plainNumber || expr.Length == 0 ? prefix + valueText
            : $"{prefix.TrimEnd()} {expr} = {valueText}".TrimStart();

        Point2 labelAt;
        switch (c.Kind)
        {
            case ConstraintKind.Radius or ConstraintKind.Diameter when _s.Geo(c.Refs[0]) is { } g:
            {
                var (center, r) = _s.CircleOf(g);
                labelAt = center + new Point2(c.LabelX, c.LabelY);
                var dir = (labelAt - center).Length < 1e-12 ? new Point2(1, 0) : (labelAt - center).Normalized();
                var from = c.Kind == ConstraintKind.Diameter ? center - dir * r : center;
                var to = center + dir * r;
                dc.DrawLine(pen, ToScreen(from), ToScreen(to));
                dc.DrawLine(pen, ToScreen(to), ToScreen(labelAt));
                break;
            }
            case ConstraintKind.Angle when _s.Geo(c.Refs[0]) is { } l1 && _s.Geo(c.Refs[1]) is { } l2:
            {
                var a = Intersection(_s.P(l1.Points[0]), _s.P(l1.Points[1]), _s.P(l2.Points[0]), _s.P(l2.Points[1]))
                        ?? (_s.P(l1.Points[0]) + _s.P(l2.Points[0])) * 0.5;
                labelAt = a + new Point2(c.LabelX, c.LabelY);
                dc.DrawLine(new Pen(new SolidColorBrush(color), 1, DashStyle.Dot), ToScreen(a), ToScreen(labelAt));
                break;
            }
            default:
            {
                Point2 a, b;
                if (c.Refs.Length == 2 && _s.Geo(c.Refs[1]) is { Kind: SketchGeometryKind.Line } line)
                {
                    a = _s.P(_s.PointOf(c.Refs[0]));
                    b = Project(_s, line, a);
                }
                else
                {
                    (a, b) = _s.Ends(c.Refs);
                }
                var mid = (a + b) * 0.5;
                labelAt = mid + new Point2(c.LabelX, c.LabelY);
                // Dimension line through the label, parallel to what is measured; extension lines to it.
                var d = c.Kind == ConstraintKind.HorizontalDistance ? new Point2(1, 0)
                    : c.Kind == ConstraintKind.VerticalDistance ? new Point2(0, 1)
                    : (b - a).Length < 1e-12 ? new Point2(1, 0) : (b - a).Normalized();
                var n = new Point2(-d.Y, d.X);
                var offset = Point2.Dot(labelAt - mid, n);
                var a2 = mid + d * Point2.Dot(a - mid, d) + n * offset;
                var b2 = mid + d * Point2.Dot(b - mid, d) + n * offset;
                dc.DrawLine(pen, ToScreen(a), ToScreen(a2));
                dc.DrawLine(pen, ToScreen(b), ToScreen(b2));
                dc.DrawLine(pen, ToScreen(a2), ToScreen(b2));
                foreach (var end in new[] { a2, b2 })
                {
                    var s = ToScreen(end);
                    dc.DrawEllipse(new SolidColorBrush(color), null, s, 2, 2);
                }
                break;
            }
        }
        _labels.Add((Label(dc, text, ToScreen(labelAt), color), c.Id));
    }

    private static Point2? Intersection(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        var r = b - a;
        var s = d - c;
        var den = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(den) < 1e-12) return null;
        var t = ((c.X - a.X) * s.Y - (c.Y - a.Y) * s.X) / den;
        return a + r * t;
    }

    private void DrawPreview(DrawingContext dc, ViewTransform view)
    {
        var pen = PreviewPen;
        switch (Mode)
        {
            case SketchMode.Line when _lineStart is { } start:
            {
                var end = Straighten(start.Pos, _cursor, out var hv);
                dc.DrawLine(pen, ToScreen(start.Pos), ToScreen(end));
                if (hv != null) Label(dc, hv == ConstraintKind.Horizontal ? "H" : "V", ToScreen((start.Pos + end) * 0.5) + new Point(0, -14), HoverColor, small: true);
                Label(dc, SketchCommands.Format(Point2.Distance(start.Pos, end)), _cursorScreen + new Point(30, 18), HoverColor, small: true);
                break;
            }
            case SketchMode.Rectangle when _first is { } a:
            {
                var c = _cursor;
                var p = Path2.FromPoints([a.Pos, new(c.X, a.Pos.Y), c, new(a.Pos.X, c.Y)], true);
                DrawPath(dc, view, p.Transformed(_m), pen);
                Label(dc, $"{SketchCommands.Format(Math.Abs(c.X - a.Pos.X))} x {SketchCommands.Format(Math.Abs(c.Y - a.Pos.Y))}", _cursorScreen + new Point(40, 18), HoverColor, small: true);
                break;
            }
            case SketchMode.Circle when _first is { } center:
            {
                var r = Point2.Distance(center.Pos, _cursor);
                if (r > 1e-9)
                {
                    var preview = new Sketch();
                    var g = preview.AddCircle(center.Pos, r);
                    DrawPath(dc, view, preview.PathOf(g).Transformed(_m), pen);
                    Label(dc, "Ø" + SketchCommands.Format(2 * r), _cursorScreen + new Point(30, 18), HoverColor, small: true);
                }
                break;
            }
            case SketchMode.Arc when _first is { } start:
            {
                if (_second is not { } end)
                {
                    dc.DrawLine(pen, ToScreen(start.Pos), ToScreen(_cursor));
                    break;
                }
                if (Arc2.FromThreePoints(start.Pos, _cursor, end.Pos) is { } through)
                {
                    var preview = new Sketch();
                    var g = through.Sweep > 0 ? preview.AddArc(through.Center, start.Pos, end.Pos) : preview.AddArc(through.Center, end.Pos, start.Pos);
                    DrawPath(dc, view, preview.PathOf(g).Transformed(_m), pen);
                    Label(dc, "R" + SketchCommands.Format(through.Radius), _cursorScreen + new Point(30, 18), HoverColor, small: true);
                }
                else dc.DrawLine(pen, ToScreen(start.Pos), ToScreen(end.Pos));
                break;
            }
            case SketchMode.ArcCenter when _first is { } center:
            {
                if (_second is not { } start)
                {
                    dc.DrawLine(pen, ToScreen(center.Pos), ToScreen(_cursor));
                    break;
                }
                var preview = new Sketch();
                if (Point2.Distance(center.Pos, start.Pos) > 1e-9 && Point2.Distance(center.Pos, _cursor) > 1e-9)
                {
                    var g = preview.AddArc(center.Pos, start.Pos, _cursor);
                    DrawPath(dc, view, preview.PathOf(g).Transformed(_m), pen);
                }
                break;
            }
        }
    }
}
