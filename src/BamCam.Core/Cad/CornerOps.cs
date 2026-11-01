using BamCam.Core.Geometry;

namespace BamCam.Core.Cad;

/// <summary>What to do with a polyline corner.</summary>
public enum CornerKind
{
    /// <summary>Tangent arc of the given radius; a negative radius gives an inverted arc around the corner point.</summary>
    Fillet,
    /// <summary>Straight cut, the given distance from the corner along both edges.</summary>
    Chamfer,
    /// <summary>
    /// Corner overcut ("dog bone"): a circle of the given (tool) radius through the corner point, centred on the
    /// bisector inside the corner, so that a round cutter reaches the corner.
    /// </summary>
    Overcut,
}

/// <summary>Fillets, chamfers and overcuts at single corners of a polyline (between two straight segments).</summary>
public static class CornerOps
{
    /// <summary>
    /// The path with the corner at vertex <paramref name="index"/> replaced, or null with a reason when the corner
    /// cannot be changed (end of an open path, arc segment, value too large for the edges, straight corner).
    /// </summary>
    public static (Path2? Path, string? Error) Apply(Path2 path, int index, CornerKind kind, double value)
    {
        var n = path.Count;
        if (n < 3 || index < 0 || index >= n) return (null, "no corner");
        if (!path.Closed && (index == 0 || index == n - 1)) return (null, "end point");
        var prevIndex = (index - 1 + n) % n;
        var prev = path.Vertices[prevIndex];
        var corner = path.Vertices[index];
        var next = path.Vertices[(index + 1) % n];
        if (Math.Abs(prev.Bulge) > 1e-12 || Math.Abs(corner.Bulge) > 1e-12) return (null, "arc segment");
        if (Math.Abs(value) < 1e-12) return (null, "zero");

        var v = corner.XY;
        var toPrev = prev.XY - v;
        var toNext = next.XY - v;
        var lenPrev = toPrev.Length;
        var lenNext = toNext.Length;
        if (lenPrev < 1e-12 || lenNext < 1e-12) return (null, "zero length segment");
        var u1 = toPrev / lenPrev;
        var u2 = toNext / lenNext;
        var angle = Math.Acos(Math.Clamp(Point2.Dot(u1, u2), -1, 1)); // between the two edges, 0 … π
        if (angle > Math.PI - 1e-9 || angle < 1e-9) return (null, "straight");
        // Turning direction of prev → corner → next (+1 left / counter-clockwise).
        var turn = Math.Sign(Point2.Cross(v - prev.XY, next.XY - v));

        double d1, d2;
        List<Vertex> replacement;
        switch (kind)
        {
            case CornerKind.Fillet when value > 0:
            {
                var t = value / Math.Tan(angle / 2);
                d1 = d2 = t;
                var bulge = turn * Math.Tan((Math.PI - angle) / 4);
                replacement = [Vtx(v + u1 * t, corner, bulge), Vtx(v + u2 * t, corner, 0)];
                break;
            }
            case CornerKind.Fillet:
            {
                // Inverted: arc centred on the corner point, bending the other way.
                var r = -value;
                d1 = d2 = r;
                var bulge = -turn * Math.Tan(angle / 4);
                replacement = [Vtx(v + u1 * r, corner, bulge), Vtx(v + u2 * r, corner, 0)];
                break;
            }
            case CornerKind.Chamfer:
            {
                var d = Math.Abs(value);
                d1 = d2 = d;
                replacement = [Vtx(v + u1 * d, corner, 0), Vtx(v + u2 * d, corner, 0)];
                break;
            }
            default:
            {
                var r = Math.Abs(value);
                var bisector = (u1 + u2).Normalized();
                var center = v + bisector * r;
                // The circle meets each edge again at twice the projection of the centre onto it.
                var q1 = v + u1 * (2 * Point2.Dot(center - v, u1));
                var q2 = v + u2 * (2 * Point2.Dot(center - v, u2));
                d1 = Point2.Distance(q1, v);
                d2 = Point2.Distance(q2, v);
                // From q1 round the far side through the corner point to q2.
                if (Arc2.FromThreePoints(q1, v, q2) is not { } arc) return (null, "straight");
                replacement = [Vtx(q1, corner, arc.ToBulge()), Vtx(q2, corner, 0)];
                break;
            }
        }
        if (d1 > lenPrev + 1e-9 || d2 > lenNext + 1e-9) return (null, "too large");

        var vertices = path.Vertices.ToList();
        // The second new vertex takes over the corner's outgoing segment (a straight line).
        replacement[1] = replacement[1] with { Bulge = corner.Bulge };
        vertices.RemoveAt(index);
        vertices.InsertRange(index, replacement);
        // Drop points that now coincide with their neighbours (value equal to an edge length).
        var cleaned = new List<Vertex>();
        for (var i = 0; i < vertices.Count; i++)
        {
            var nextVertex = vertices[(i + 1) % vertices.Count];
            var last = i == vertices.Count - 1 && !path.Closed;
            if (!last && vertices[i].XY.AlmostEquals(nextVertex.XY, 1e-9)) continue;
            cleaned.Add(vertices[i]);
        }
        return (new Path2(cleaned, path.Closed), null);
    }

    private static Vertex Vtx(Point2 p, Vertex corner, double bulge) => new(p.ToPoint3(corner.Point.Z), bulge);

    /// <summary>Index of the vertex of <paramref name="path"/> nearest to <paramref name="point"/> within <paramref name="tolerance"/>, or -1.</summary>
    public static int NearestVertex(Path2 path, Point2 point, double tolerance)
    {
        var best = -1;
        var bestDistance = tolerance;
        for (var i = 0; i < path.Count; i++)
        {
            var d = Point2.Distance(path.Vertices[i].XY, point);
            if (d <= bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }
        return best;
    }
}
