using BamCam.Core.Geometry;

namespace BamCam.Core.Cam;

/// <summary>
/// Outlines of turning and grooving inserts in lathe coordinates (Z along the axis, X away from the part), so that
/// toolpaths keep the whole insert out of the part and the simulation removes what the insert sweeps.
/// <para>
/// A turning insert has its cutting corner at the bottom: the main cutting edge leaves the corner at the approach angle
/// κr from the feed direction (−Z for right-hand turning), the minor edge follows at the corner angle ε of the insert
/// shape. The corner is rounded with the nose radius; the outline is given around the centre of that radius. The
/// minor edge then rises at 180° − κr − ε: a falling contour steeper than that cannot be followed.
/// </para>
/// </summary>
public static class LatheInsert
{
    /// <summary>Corner angle ε of an insert shape in degrees (0 = round or none).</summary>
    public static double CornerAngle(InsertShape shape) => shape switch
    {
        InsertShape.C80 or InsertShape.W80 => 80,
        InsertShape.D55 or InsertShape.K55 => 55,
        InsertShape.V35 => 35,
        InsertShape.T60 => 60,
        InsertShape.S90 => 90,
        _ => 0,
    };

    /// <summary>Whether a tool has an insert shape that matters beyond its nose radius.</summary>
    public static bool HasShape(ToolDefinition? tool) => tool != null && CornerAngle(tool.InsertShape) > 0;

    /// <summary>
    /// Steepest falling contour (degrees from the axis) the insert can follow in its feed direction: 180° − κr − ε.
    /// </summary>
    public static double MaxPlungeAngle(ToolDefinition tool) => 180 - tool.ApproachAngle - CornerAngle(tool.InsertShape);

    /// <summary>
    /// The insert around the centre of its nose radius, for cutting towards −Z (right hand) on the outside of a part;
    /// <paramref name="leftHand"/> mirrors it along Z, <paramref name="flipX"/> across (internal turning in a frame
    /// that is not mirrored). Null without an insert shape.
    /// </summary>
    public static Path2? Outline(ToolDefinition? tool, double noseRadius, bool leftHand = false, bool flipX = false)
    {
        if (tool == null || CornerAngle(tool.InsertShape) <= 0) return null;
        var eps = Angles.ToRadians(CornerAngle(tool.InsertShape));
        var kr = Angles.ToRadians(Math.Clamp(tool.ApproachAngle, 1, 179));
        var r = Math.Max(0, noseRadius);
        // Edge directions, measured from +Z: the main edge at 180° − κr, the minor edge ε further round towards +Z.
        var t1 = Math.PI - kr;
        var t2 = t1 - eps;
        var e1 = Point2.FromAngle(t1, 1);
        var e2 = Point2.FromAngle(t2, 1);
        var length = tool.CuttingEdgeLength > 0 ? tool.CuttingEdgeLength : Math.Max(20 * r, 1e-3);
        // The sharp corner lies outside the nose circle on the bisector, at r / sin(ε / 2) from its centre.
        var bisector = (e1 + e2).Normalized();
        var corner = bisector * -(r / Math.Sin(eps / 2));
        var triangle = tool.InsertShape is InsertShape.T60 or InsertShape.W80;
        List<Point2> pts = triangle
            ? [corner, corner + e2 * length, corner + e1 * length]
            : [corner, corner + e2 * length, corner + (e1 + e2) * length, corner + e1 * length];
        var sharp = Path2.FromPoints(pts, true);
        var shape = sharp;
        if (r > 1e-9)
        {
            // Round the corners with the nose radius: the cutting corner then is the nose circle around the origin.
            var inner = Polygons.Offset([sharp], -r);
            var rounded = Polygons.Offset(inner, r);
            if (rounded.Count > 0) shape = rounded.OrderByDescending(p => Math.Abs(p.SignedArea())).First();
        }
        if (leftHand || flipX) shape = shape.Transformed(Matrix4.Scale(leftHand ? -1 : 1, flipX ? -1 : 1, 1));
        return shape;
    }

    /// <summary>
    /// A grooving blade of <paramref name="width"/> with its tip (the corner towards −Z) at the origin, reaching
    /// <paramref name="height"/> up, its two bottom corners rounded with <paramref name="cornerRadius"/>; <paramref name="flipX"/>
    /// turns it downwards (internal grooves).
    /// </summary>
    public static Path2 GroovingBlade(double width, double cornerRadius, double height, bool flipX = false)
    {
        var rc = Math.Clamp(cornerRadius, 0, width / 2);
        var box = Path2.FromPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)], true);
        var blade = box;
        if (rc > 1e-9)
        {
            // Only the bottom corners are rounded: round all, then put the square top back.
            var rounded = Polygons.Offset(Polygons.Offset([box], -rc), rc);
            var top = Path2.FromPoints([new(0, height / 2), new(width, height / 2), new(width, height), new(0, height)], true);
            var union = Polygons.Union(rounded, [top]);
            if (union.Count > 0) blade = union.OrderByDescending(p => Math.Abs(p.SignedArea())).First();
        }
        return flipX ? blade.Transformed(Matrix4.Scale(1, -1, 1)) : blade;
    }
}
