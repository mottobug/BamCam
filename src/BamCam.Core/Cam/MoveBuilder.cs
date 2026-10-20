using BamCam.Core.Geometry;

namespace BamCam.Core.Cam;

/// <summary>Helper that appends moves to a toolpath while tracking the current position.</summary>
internal sealed class MoveBuilder(Toolpath tp, double clearanceZ, double fastPlungeHeight)
{
    public Toolpath Toolpath { get; } = tp;
    public Point3? Position { get; private set; }
    public double ClearanceZ { get; } = clearanceZ;
    public double FastPlungeHeight { get; } = fastPlungeHeight;

    public bool AtClearance => Position is { } p && p.Z >= ClearanceZ - 1e-9;

    public void Rapid(Point3 to)
    {
        if (Position is { } p && p.AlmostEquals3(to)) return;
        Toolpath.Add(Move.Rapid(to));
        Position = to;
    }

    public void Line(Point3 to, double feed)
    {
        if (Position is { } p && p.AlmostEquals3(to)) return;
        Toolpath.Add(Move.Line(to, feed));
        Position = to;
    }

    public void Arc(Point3 to, Point2 center, bool clockwise, double feed)
    {
        Toolpath.Add(Move.Arc(to, center, clockwise, feed));
        Position = to;
    }

    public void Retract()
    {
        if (Position is not { } p) return;
        if (p.Z < ClearanceZ) Rapid(p with { Z = ClearanceZ });
    }

    /// <summary>Retracts, moves over <paramref name="target"/> and plunges to its Z.</summary>
    /// <param name="fastFromZ">Z level above which rapid plunging is safe (stock surface or last cut level).</param>
    public void MoveToAndPlunge(Point3 target, double plungeFeed, double fastFromZ)
    {
        if (Position is null)
        {
            Rapid(target with { Z = ClearanceZ });
        }
        // Positions closer than 0.1 µm are the same point (rounding of snapped geometry); don't retract.
        else if (!Position.Value.XY.AlmostEquals(target.XY, 1e-4))
        {
            Retract();
            Rapid(target with { Z = Position!.Value.Z });
        }
        PlungeTo(target.Z, plungeFeed, fastFromZ);
    }

    public void PlungeTo(double z, double plungeFeed, double fastFromZ)
    {
        var p = Position!.Value;
        if (p.Z <= z + 1e-9)
        {
            if (p.Z < z - 1e-9) Line(p with { Z = z }, plungeFeed);
            return;
        }
        var fastZ = fastFromZ + FastPlungeHeight;
        // Rapid down only when that saves more than the fast plunge height itself (matches reference output:
        // clearance 1.5 / height 1 feeds all the way, clearance 3 rapids to 1.0).
        if (FastPlungeHeight > 0 && p.Z - fastZ > FastPlungeHeight + 1e-9 && fastZ > z) Rapid(p with { Z = fastZ });
        Line(Position!.Value with { Z = z }, plungeFeed);
    }

    /// <summary>Follows a path from its start (the tool must already be at the start point).</summary>
    public void Follow(Path2 path, double feed, double zOffset = 0)
    {
        foreach (var s in path.Segments()) Segment(s, feed, zOffset);
    }

    public void Segment(Segment s, double feed, double zOffset = 0)
    {
        var end = s.End with { Z = s.End.Z + zOffset };
        if (s.IsArc)
        {
            var arc = s.ToArc();
            Arc(end, arc.Center, arc.IsClockwise, feed);
        }
        else
        {
            Line(end, feed);
        }
    }
}

internal static class PointExtensions
{
    public static bool AlmostEquals3(this Point3 a, Point3 b, double tol = 1e-9) => Point3.Distance(a, b) <= tol;
}
