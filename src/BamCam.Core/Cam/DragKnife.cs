using System.ComponentModel;
using BamCam.Core.Geometry;

namespace BamCam.Core.Cam;

/// <summary>
/// Drag knife (vinyl and foil cutting): the blade tip trails the spindle axis by <see cref="KnifeOffset"/>. The tool
/// path is moved forward by that offset and gets swivel arcs around sharp corners, so that the blade follows the
/// drawn shape and turns on the spot at corners.
/// </summary>
public sealed class DragKnifeOp : DepthOp
{
    public override string XmlName => "BamCamDragKnife";
    public override string DisplayType => "Drag Knife";

    public DragKnifeOp()
    {
        // Set explicitly: a CAM style's milling depth would cut through the backing of the foil.
        TargetDepth = new(-0.1, ParamState.Value);
        DepthIncrement = new(0, ParamState.Value);
    }

    /// <summary>Starting values for inch drawings.</summary>
    public void UseInchDefaults()
    {
        TargetDepth.Set(-0.004);
        DepthIncrement.Set(0);
        KnifeOffset.Value = 0.01;
        Overcut.Value = 0.02;
    }

    [Param(60, "Drag Knife"), DisplayName("Knife Offset"),
     Description("Distance of the blade tip behind the spindle axis (blade offset), e.g. 0.25 for 45° blades, 0.5 for 60° blades.")]
    public CamParam<double> KnifeOffset { get; set; } = new(0.25);

    [Param(61, "Drag Knife"), DisplayName("Swivel Angle"),
     Description("Corners where the direction changes by more than this angle (degrees) get a swivel arc around the corner point; smaller changes are cut straight on.")]
    public CamParam<double> SwivelAngle { get; set; } = new(10);

    [Param(62, "Drag Knife"),
     Description("Closed shapes are cut this far beyond their start point, so that the cut closes cleanly.")]
    public CamParam<double> Overcut { get; set; } = new(0.5);
}

internal static class DragKnifeGenerator
{
    public static void Generate(GenContext ctx, DragKnifeOp op, Toolpath tp)
    {
        var offset = Math.Max(0, op.KnifeOffset.Value);
        var swivel = Angles.ToRadians(Math.Clamp(op.SwivelAngle.Value, 0, 180));
        var overcut = Math.Max(0, op.Overcut.Value);
        var mb = new MoveBuilder(tp, ctx.Clearance, ctx.FastPlunge);
        var paths = ctx.SourcePaths().Select(s => s.Path).Where(p => p.SegmentCount > 0).ToList();
        var pos = op.StartXY ?? (paths.Count > 0 ? paths[0].StartPoint.XY : Point2.Zero);
        while (paths.Count > 0)
        {
            var next = paths.OrderBy(p => Point2.Distance(p.StartPoint.XY, pos)).First();
            paths.Remove(next);
            var axis = AxisPath(next, offset, swivel, overcut);
            var prev = ctx.StockSurface;
            foreach (var z in ctx.Levels(op))
            {
                var start = axis.StartPoint;
                if (mb.Position is { } p && !p.XY.AlmostEquals(start.XY, 1e-7)) mb.Retract();
                mb.MoveToAndPlunge(new Point3(start.X, start.Y, z), ctx.PlungeFeed, prev);
                foreach (var s in axis.Segments()) mb.Segment(s, ctx.CutFeed, z);
                prev = z;
            }
            mb.Retract();
            pos = next.EndPoint.XY;
        }
        mb.Retract();
        ctx.Finish(tp);
    }

    /// <summary>
    /// Path of the spindle axis for a blade that follows <paramref name="shape"/> (Z = 0). Lines move forward by the
    /// offset; arcs become arcs of radius √(R² + offset²) turned forward; direction changes above
    /// <paramref name="swivel"/> get an arc of radius offset around the corner. A closed shape starts with the
    /// swivel from its last direction and ends <paramref name="overcut"/> beyond its start.
    /// </summary>
    internal static Path2 AxisPath(Path2 shape, double offset, double swivel, double overcut)
    {
        var segments = shape.Segments().Where(s => s.Length > 1e-9).ToList();
        if (shape.Closed && overcut > 0) segments.AddRange(Overcut(segments, overcut));

        var vertices = new List<Vertex>();
        Point2? current = null;
        void LineTo(Point2 p)
        {
            if (current is { } c && c.AlmostEquals(p, 1e-9)) return;
            if (current != null) vertices[^1] = vertices[^1] with { Bulge = 0 };
            vertices.Add(new Vertex(p.ToPoint3()));
            current = p;
        }
        void ArcTo(Point2 p, double sweep)
        {
            if (current is { } c && c.AlmostEquals(p, 1e-9)) return;
            vertices[^1] = vertices[^1] with { Bulge = Math.Tan(sweep / 4) };
            vertices.Add(new Vertex(p.ToPoint3()));
            current = p;
        }
        // Turns the blade at a corner from one direction to the next (straight on below the swivel angle).
        void Corner(Point2 corner, Point2 from, Point2 to)
        {
            var turn = Math.Atan2(Point2.Cross(from, to), Point2.Dot(from, to));
            var target = corner + to * offset;
            if (Math.Abs(turn) > swivel && offset > 0) ArcTo(target, turn);
            else LineTo(target);
        }

        var first = segments[0];
        if (shape.Closed)
        {
            // Plunge with the blade still pointing in the last direction, then swivel into the first one.
            var lastDir = EndTangent(segments[shape.SegmentCount - 1]);
            LineTo(first.Start.XY + lastDir * offset);
            Corner(first.Start.XY, lastDir, StartTangent(first));
        }
        else
        {
            LineTo(first.Start.XY + StartTangent(first) * offset);
        }
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (i > 0) Corner(s.Start.XY, EndTangent(segments[i - 1]), StartTangent(s));
            if (!s.IsArc)
            {
                LineTo(s.End.XY + EndTangent(s) * offset);
                continue;
            }
            // Same sweep on the circle through the axis positions (radius √(R² + d²)).
            ArcTo(s.End.XY + EndTangent(s) * offset, s.ToArc().Sweep);
        }
        return new Path2(vertices, false);
    }

    /// <summary>The first <paramref name="length"/> of the closed shape again (to cut beyond the start).</summary>
    internal static IEnumerable<Segment> Overcut(List<Segment> segments, double length)
    {
        foreach (var s in segments)
        {
            if (length <= 1e-9) yield break;
            if (s.Length <= length)
            {
                yield return s;
                length -= s.Length;
                continue;
            }
            var t = length / s.Length;
            if (!s.IsArc)
            {
                yield return new Segment(s.Start, s.Start + (s.End - s.Start) * t, 0);
            }
            else
            {
                var arc = s.ToArc();
                yield return new Segment(s.Start, arc.PointAt(t).ToPoint3(s.Start.Z), Math.Tan(arc.Sweep * t / 4));
            }
            yield break;
        }
    }

    internal static Point2 StartTangent(Segment s)
    {
        if (!s.IsArc) return (s.End.XY - s.Start.XY).Normalized();
        var arc = s.ToArc();
        var radial = (arc.Start - arc.Center).Normalized();
        return arc.Sweep > 0 ? new Point2(-radial.Y, radial.X) : new Point2(radial.Y, -radial.X);
    }

    internal static Point2 EndTangent(Segment s)
    {
        if (!s.IsArc) return (s.End.XY - s.Start.XY).Normalized();
        var arc = s.ToArc();
        var radial = (arc.End - arc.Center).Normalized();
        return arc.Sweep > 0 ? new Point2(-radial.Y, radial.X) : new Point2(radial.Y, -radial.X);
    }
}
