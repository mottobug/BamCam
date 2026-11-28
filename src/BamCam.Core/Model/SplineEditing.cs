using BamCam.Core.Geometry;

namespace BamCam.Core.Model;

/// <summary>
/// Splines as drawn in CamBam: a chain of cubic Bezier segments in NURBS form (degree 3, inner knots with
/// multiplicity 3). Control points alternate anchor (on the curve), outgoing handle, incoming handle, anchor, ...
/// Closed chains repeat the first anchor at the end.
/// </summary>
public static class SplineEditing
{
    /// <summary>True when the spline is a cubic Bezier chain (anchors at every third control point).</summary>
    public static bool IsBezierChain(SplineEntity s)
    {
        var n = s.ControlPoints.Count;
        if (s.Degree != 3 || n < 4 || (n - 1) % 3 != 0 || s.Knots.Count != n + 4) return false;
        var expected = ChainKnots((n - 1) / 3);
        for (var i = 0; i < expected.Count; i++)
            if (Math.Abs(s.Knots[i] - expected[i] * (s.Knots[^1] - s.Knots[0]) / ((n - 1) / 3) - s.Knots[0]) > 1e-9) return false;
        return true;
    }

    public static bool IsAnchor(int index) => index % 3 == 0;

    /// <summary>Knots of a chain with <paramref name="segments"/> Bezier segments: 0 0 0 0 1 1 1 ... n n n n.</summary>
    public static List<double> ChainKnots(int segments)
    {
        var knots = new List<double> { 0, 0, 0, 0 };
        for (var k = 1; k < segments; k++) knots.AddRange([k, k, k]);
        knots.AddRange([segments, segments, segments, segments]);
        return knots;
    }

    /// <summary>
    /// A smooth chain through the clicked points. Inner handles lie on the line through the neighbouring anchors,
    /// each reaching out half of the neighbour distance in proportion to the adjacent segment length (as CamBam
    /// places them); end handles point two thirds of the way to the neighbour's handle.
    /// </summary>
    public static SplineEntity FromAnchors(IReadOnlyList<Point3> anchors, bool closed)
    {
        var a = anchors.ToList();
        var n = a.Count;
        if (n < 2) throw new ArgumentException("A spline needs at least two points.", nameof(anchors));
        var outH = new Point3[n];
        var inH = new Point3[n];
        for (var k = 0; k < n; k++)
        {
            var hasPrev = closed || k > 0;
            var hasNext = closed || k < n - 1;
            if (!hasPrev || !hasNext) continue;
            var prev = a[(k - 1 + n) % n];
            var next = a[(k + 1) % n];
            var cp = Distance(a[k], prev);
            var cn = Distance(next, a[k]);
            var d = next - prev;
            var sum = cp + cn;
            if (sum < 1e-12) { outH[k] = inH[k] = a[k]; continue; }
            outH[k] = a[k] + d * (0.5 * cn / sum);
            inH[k] = a[k] - d * (0.5 * cp / sum);
        }
        if (!closed)
        {
            // Two points only: a straight segment with handles at thirds.
            outH[0] = n > 2 ? a[0] + (inH[1] - a[0]) * (2.0 / 3) : a[0] + (a[1] - a[0]) * (1.0 / 3);
            inH[n - 1] = n > 2 ? a[n - 1] + (outH[n - 2] - a[n - 1]) * (2.0 / 3) : a[1] + (a[0] - a[1]) * (1.0 / 3);
        }

        var segments = closed ? n : n - 1;
        var ctrl = new List<Point3> { a[0] };
        for (var s = 0; s < segments; s++)
        {
            var next = (s + 1) % n;
            ctrl.Add(outH[s]);
            ctrl.Add(inH[next]);
            ctrl.Add(a[next]);
        }
        return new SplineEntity(3, ctrl, ChainKnots(segments), closed: closed);
    }

    /// <summary>
    /// Closes or opens a Bezier chain (the Closed property of a spline). Closing adds a smooth segment from the last
    /// anchor back to the first one: the handles at the two ends are placed as the spline tool places them for a closed
    /// chain, the inner handles stay as they are. A last anchor already on the first one is joined smoothly instead.
    /// Opening removes the closing segment. Other splines are left as they are.
    /// </summary>
    public static void SetChainClosed(SplineEntity s, bool closed)
    {
        if (!IsBezierChain(s) || s.Weights.Count > 0) return;
        var pts = s.ControlPoints;
        var anchors = pts.Where((_, i) => IsAnchor(i)).ToList();
        var endsMeet = Distance(anchors[0], anchors[^1]) < 1e-9;
        if (!closed)
        {
            // Only a chain that really comes back to its start has a closing segment to remove.
            if (!endsMeet || anchors.Count < 3) return;
            var open = pts.Take(pts.Count - 3).ToList();
            ReplaceChain(s, open);
            return;
        }

        // The anchors of the closed chain: a last anchor on the first one is the same point.
        var original = pts.ToList();
        if (endsMeet) anchors.RemoveAt(anchors.Count - 1);
        var n = anchors.Count;
        if (n < 3) return; // two points give no curve worth closing: the straight line stays
        var ctrl = FromAnchors(anchors, closed: true).ControlPoints.ToList();
        // Handles of the inner anchors as they were (edited by hand perhaps); index 3k is anchor k, 3k±1 its handles.
        var lastInner = endsMeet ? n - 1 : n - 2;
        for (var k = 1; k <= lastInner; k++)
        {
            ctrl[3 * k - 1] = original[3 * k - 1];
            if (k < n - 1 || endsMeet) ctrl[3 * k + 1] = original[3 * k + 1];
        }
        ReplaceChain(s, ctrl);
    }

    private static void ReplaceChain(SplineEntity s, List<Point3> ctrl)
    {
        s.ControlPoints.Clear();
        s.ControlPoints.AddRange(ctrl);
        s.Knots.Clear();
        s.Knots.AddRange(ChainKnots((ctrl.Count - 1) / 3));
    }

    /// <summary>
    /// Moves one control point. In a Bezier chain an anchor takes its handles along, and with
    /// <paramref name="linked"/> a handle keeps the opposite handle of its anchor on one line (its length stays).
    /// Other splines move just the control point.
    /// </summary>
    public static void MoveControlPoint(SplineEntity s, int index, Point3 target, bool linked)
    {
        var pts = s.ControlPoints;
        var last = pts.Count - 1;
        if (index < 0 || index > last) return;
        if (!IsBezierChain(s))
        {
            pts[index] = target;
            return;
        }
        var closedChain = s.Closed && Distance(pts[0], pts[last]) < 1e-9;

        if (IsAnchor(index))
        {
            var delta = target - pts[index];
            foreach (var i in AnchorGroup(index, last, closedChain)) pts[i] = pts[i] + delta;
            return;
        }

        pts[index] = target;
        if (!linked) return;
        var anchor = index % 3 == 1 ? index - 1 : index + 1;
        var opposite = 2 * anchor - index;
        if (closedChain)
        {
            if (opposite < 0) opposite = last - 1;
            else if (opposite > last) opposite = 1;
        }
        if (opposite < 0 || opposite > last) return;
        var center = pts[anchor];
        var dir = center - target;
        var len = Distance(dir, Point3.Zero);
        if (len < 1e-12) return;
        var keep = Distance(pts[opposite], center);
        pts[opposite] = center + dir * (keep / len);
    }

    /// <summary>The anchor and its handles (both copies of the shared anchor of a closed chain).</summary>
    private static IEnumerable<int> AnchorGroup(int anchor, int last, bool closedChain)
    {
        var set = new HashSet<int>();
        void Add(int a)
        {
            set.Add(a);
            if (a - 1 >= 0) set.Add(a - 1);
            if (a + 1 <= last) set.Add(a + 1);
        }
        Add(anchor);
        if (closedChain && (anchor == 0 || anchor == last)) Add(anchor == 0 ? last : 0);
        return set;
    }

    private static double Distance(Point3 a, Point3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
