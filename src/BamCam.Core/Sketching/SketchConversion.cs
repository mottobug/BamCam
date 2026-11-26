using BamCam.Core.Geometry;
using static BamCam.Core.Localization.Tr;

namespace BamCam.Core.Sketching;

/// <summary>
/// Edit ▸ Convert To ▸ Sketch: drawing objects (polylines, lines, arcs, circles, rectangles) become one sketch with the
/// same lines, arcs and circles. Ends that meet stay joined (also with the origin), horizontal and vertical lines keep
/// that direction and smooth joins of arcs stay tangent; there are no dimensions, so the sketch can still move until
/// dimensions are added, as with geometry drawn in the sketch mode.
/// </summary>
public static class SketchConversion
{
    /// <summary>Most segments converted at once: the solver gets slow with very long chains (flattened splines).</summary>
    public const int MaxSegments = 1500;

    /// <summary>
    /// The sketch of <paramref name="paths"/> (drawing coordinates), or null with the reason in <paramref name="error"/>.
    /// The paths must lie in one plane parallel to XY: its height is returned in <paramref name="z"/>.
    /// </summary>
    public static Sketch? FromPaths(IReadOnlyList<Path2> paths, out double z, out string? error)
    {
        z = 0;
        error = null;
        var vertices = paths.SelectMany(p => p.Vertices).Select(v => v.Point).ToList();
        if (vertices.Count < 2)
        {
            error = T("Select drawing objects with lines, arcs or circles to convert into a sketch.");
            return null;
        }
        z = vertices[0].Z;
        var flatZ = z;
        if (vertices.Any(p => Math.Abs(p.Z - flatZ) > 1e-6))
        {
            error = T("A sketch lies in one plane parallel to XY: the selected objects have different heights (Z).");
            return null;
        }
        var segmentCount = paths.Sum(p => p.SegmentCount);
        if (segmentCount > MaxSegments)
        {
            error = F("{0} segments are too many for one sketch (at most {1}). Simplify the objects first or convert fewer at once.",
                segmentCount, MaxSegments);
            return null;
        }

        var extent = vertices.Max(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
        var tol = 1e-7 * Math.Max(1, extent);
        var s = Sketch.CreateNew();
        var added = new List<int>();
        var ends = new List<(int Id, Point2 At)> { (Sketch.OriginId, new Point2(0, 0)) };

        foreach (var path in paths)
        {
            if (AsCircle(path, tol) is { } circle)
            {
                s.AddCircle(circle.Center, circle.Radius);
                continue;
            }
            var pieces = new List<(SketchGeometry Geo, Point2 StartDir, Point2 EndDir)>();
            foreach (var seg in path.Segments())
            {
                if (seg.IsArc)
                {
                    var arc = seg.ToArc();
                    if (arc.Radius <= tol || Math.Abs(arc.Sweep) * arc.Radius <= tol) continue;
                    // Sketch arcs run counter-clockwise: a clockwise arc is stored from its end to its start.
                    var ccw = arc.Sweep > 0;
                    var g = ccw ? s.AddArc(arc.Center, arc.Start, arc.End) : s.AddArc(arc.Center, arc.End, arc.Start);
                    int startId = ccw ? g.Points[1] : g.Points[2], endId = ccw ? g.Points[2] : g.Points[1];
                    ends.Add((startId, s.P(startId)));
                    ends.Add((endId, s.P(endId)));
                    pieces.Add((g, ArcDirection(arc, arc.Start), ArcDirection(arc, arc.End)));
                }
                else
                {
                    Point2 a = seg.Start.XY, b = seg.End.XY;
                    if (Point2.Distance(a, b) <= tol) continue;
                    var g = s.AddLine(a, b);
                    ends.Add((g.Points[0], a));
                    ends.Add((g.Points[1], b));
                    var d = (b - a).Normalized();
                    pieces.Add((g, d, d));
                    if (Math.Abs(b.Y - a.Y) <= tol) added.Add(s.AddConstraint(ConstraintKind.Horizontal, [g.Id]).Id);
                    else if (Math.Abs(b.X - a.X) <= tol) added.Add(s.AddConstraint(ConstraintKind.Vertical, [g.Id]).Id);
                }
            }
            // Smooth joins where an arc takes part stay tangent.
            var joins = path.Closed ? pieces.Count : pieces.Count - 1;
            for (var i = 0; i < joins && pieces.Count > 1; i++)
            {
                var (first, _, endDir) = pieces[i];
                var (second, startDir, _) = pieces[(i + 1) % pieces.Count];
                if (first.Kind == SketchGeometryKind.Line && second.Kind == SketchGeometryKind.Line) continue;
                if (Point2.Dot(endDir, startDir) < 1 - 1e-9) continue;
                // A tangent takes a line (if any) first.
                var (g1, g2) = first.Kind == SketchGeometryKind.Line ? (first, second) : (second, first);
                added.Add(s.AddConstraint(ConstraintKind.Tangent, [g1.Id, g2.Id], SketchCommands.TangentData(s, g1.Id, g2.Id)).Id);
            }
        }

        // Ends at the same place are joined, each with the first one there (the origin when they are at 0, 0).
        var firsts = new List<(int Id, Point2 At)>();
        foreach (var end in ends)
        {
            var match = firsts.FindIndex(f => Point2.Distance(f.At, end.At) <= tol * 10);
            if (match < 0) firsts.Add(end);
            else added.Add(s.AddConstraint(ConstraintKind.Coincident, [firsts[match].Id, end.Id]).Id);
        }

        if (s.Geometry.Count <= 1)
        {
            error = T("Select drawing objects with lines, arcs or circles to convert into a sketch.");
            return null;
        }
        // What the solver finds contradicting or repeating is dropped again (rounding in the drawing).
        SketchCommands.Settle(s, added, out _);
        return s;
    }

    /// <summary>A closed path of arcs around one centre with one radius: a full circle.</summary>
    private static (Point2 Center, double Radius)? AsCircle(Path2 path, double tol)
    {
        if (!path.Closed) return null;
        var segments = path.Segments().ToList();
        if (segments.Count == 0 || segments.Any(sg => !sg.IsArc)) return null;
        var arcs = segments.Select(sg => sg.ToArc()).ToList();
        var c = arcs[0].Center;
        var r = arcs[0].Radius;
        if (arcs.Any(a => Point2.Distance(a.Center, c) > tol * 10 || Math.Abs(a.Radius - r) > tol * 10)) return null;
        return Math.Abs(Math.Abs(arcs.Sum(a => a.Sweep)) - 2 * Math.PI) < 1e-6 ? (c, r) : null;
    }

    /// <summary>The direction of travel along an arc at a point of it.</summary>
    private static Point2 ArcDirection(Arc2 arc, Point2 at)
    {
        var radial = (at - arc.Center).Normalized();
        var ccw = new Point2(-radial.Y, radial.X);
        return arc.Sweep > 0 ? ccw : -ccw;
    }
}
