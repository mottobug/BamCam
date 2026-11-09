using BamCam.Core.Cad;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

/// <summary>Draw > Surface > Extrude, Extrude Solid, Fill and Draw > Fill Region.</summary>
public class SurfaceBuilderTests
{
    private static Path2 Rect(double x0, double y0, double x1, double y1, double z = 0) =>
        Path2.FromPoints([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], true).Transformed(Matrix4.Translation(0, 0, z));

    private static Path2 Circle(double cx, double cy, double r) =>
        new([new Vertex(new Point3(cx + r, cy, 0), 1), new Vertex(new Point3(cx - r, cy, 0), 1)], true);

    private static double Area(List<(Point2 A, Point2 B, Point2 C)> tris) =>
        tris.Sum(t => ((t.B.X - t.A.X) * (t.C.Y - t.A.Y) - (t.B.Y - t.A.Y) * (t.C.X - t.A.X)) / 2);

    private static Point2 Centroid((Point2 A, Point2 B, Point2 C) t) => new((t.A.X + t.B.X + t.C.X) / 3, (t.A.Y + t.B.Y + t.C.Y) / 3);

    [Fact]
    public void A_square_with_two_holes_is_covered_exactly_and_the_holes_stay_free()
    {
        var holes = new[] { Rect(2, 2, 4, 4), Rect(6, 5, 8, 8) };
        var tris = SurfaceBuilder.Triangulate(Rect(0, 0, 10, 10), holes.Select(h => Polygons.EnsureOrientation(h, ccw: false)).ToList());

        Assert.Equal(100 - 4 - 6, Area(tris), 6);
        Assert.All(tris, t => Assert.True(((t.B.X - t.A.X) * (t.C.Y - t.A.Y) - (t.B.Y - t.A.Y) * (t.C.X - t.A.X)) > 0, "counter-clockwise"));
        Assert.DoesNotContain(tris, t => holes.Any(h => h.Contains(Centroid(t))));
    }

    [Fact]
    public void A_concave_shape_and_round_holes_are_covered_exactly()
    {
        // An L shape.
        var l = Path2.FromPoints([new(0, 0), new(10, 0), new(10, 3), new(3, 3), new(3, 10), new(0, 10)], true);
        Assert.Equal(51, Area(SurfaceBuilder.Triangulate(l, [])), 6);

        // A disc with three round holes in a row (the cuts to the holes must not cross).
        var outer = Circle(0, 0, 20);
        var holes = new[] { Circle(-10, 0, 3), Circle(0, 0, 3), Circle(10, 0, 3) };
        var tris = SurfaceBuilder.Triangulate(outer, holes.Select(h => Polygons.EnsureOrientation(h, ccw: false)).ToList());
        // The area of the flattened outlines (the circles as the polygons the mesh is made of).
        double Flat(Path2 p) => Math.Abs(Path2.FromPoints(p.Flatten(SurfaceBuilder.ArcTolerance), true).SignedArea());
        var expected = Flat(outer) - holes.Sum(Flat);
        Assert.Equal(expected, Area(tris), 3);
        Assert.DoesNotContain(tris, t => holes.Any(h => h.Contains(Centroid(t))));
    }

    [Fact]
    public void A_jagged_outline_with_a_grid_of_round_holes_is_covered_exactly()
    {
        // A star with 200 points around a grid of 5 x 5 holes: many holes on one row and with the same rightmost X.
        var star = Path2.FromPoints(Enumerable.Range(0, 400).Select(i =>
        {
            var r = i % 2 == 0 ? 60 : 55;
            var a = i * Math.PI / 200;
            return new Point2(r * Math.Cos(a), r * Math.Sin(a));
        }), true);
        var holes = new List<Path2>();
        for (var i = -2; i <= 2; i++)
        for (var j = -2; j <= 2; j++)
            holes.Add(Circle(i * 15, j * 15, 4));

        var tris = SurfaceBuilder.Triangulate(star, holes.Select(h => Polygons.EnsureOrientation(h, ccw: false)).ToList());

        double Flat(Path2 p) => Math.Abs(Path2.FromPoints(p.Flatten(SurfaceBuilder.ArcTolerance), true).SignedArea());
        Assert.Equal(Flat(star) - holes.Sum(Flat), Area(tris), 3);
        Assert.DoesNotContain(tris, t => holes.Any(h => h.Contains(Centroid(t))));
        Assert.All(tris, t => Assert.True(star.Contains(Centroid(t))));
    }

    [Fact]
    public void Extrude_solid_is_closed_and_faces_outwards()
    {
        var mesh = SurfaceBuilder.ExtrudeSolid([Rect(0, 0, 10, 10, z: 1), Rect(3, 3, 6, 6, z: 1)], 4);

        // Every edge, by its corner positions, is used once in each direction.
        var v = mesh.Vertices;
        var edges = new Dictionary<(Point3, Point3), int>();
        for (var i = 0; i < mesh.Faces.Count; i += 3)
            for (var k = 0; k < 3; k++)
            {
                var key = (v[mesh.Faces[i + k]], v[mesh.Faces[i + (k + 1) % 3]]);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        Assert.All(edges, e => Assert.Equal(1, edges.GetValueOrDefault((e.Key.Item2, e.Key.Item1))));
        // Volume by the divergence theorem: positive when the triangles face outwards.
        var volume = 0.0;
        for (var i = 0; i < mesh.Faces.Count; i += 3)
            volume += Point3.Dot(v[mesh.Faces[i]], Point3.Cross(v[mesh.Faces[i + 1]], v[mesh.Faces[i + 2]])) / 6;
        Assert.Equal((100 - 9) * 4, volume, 6);
        var b = mesh.Bounds();
        Assert.Equal(1, b.Min.Z, 9);
        Assert.Equal(5, b.Max.Z, 9);

        // Downwards: the same body below the outline, still facing outwards.
        var down = SurfaceBuilder.ExtrudeSolid([Rect(0, 0, 10, 10)], -2);
        var downVolume = 0.0;
        for (var i = 0; i < down.Faces.Count; i += 3)
            downVolume += Point3.Dot(down.Vertices[down.Faces[i]], Point3.Cross(down.Vertices[down.Faces[i + 1]], down.Vertices[down.Faces[i + 2]])) / 6;
        Assert.Equal(200, downVolume, 6);
    }

    [Fact]
    public void Extrude_makes_open_walls_in_steps()
    {
        var open = Path2.FromPoints([new(0, 0), new(10, 0), new(10, 5)], false);
        var mesh = SurfaceBuilder.Extrude([open], 6, steps: 3);

        Assert.Equal(2 * 2 * 3, mesh.Faces.Count / 3); // two segments, three rows, two triangles each
        Assert.Equal(6, mesh.Bounds().Max.Z, 9);
        // A closed square: four walls, no top or bottom.
        Assert.Equal(4 * 2, SurfaceBuilder.Extrude([Rect(0, 0, 1, 1)], 1).Faces.Count / 3);
    }

    [Fact]
    public void Fill_is_flat_and_covers_the_shape()
    {
        var mesh = SurfaceBuilder.Fill([Rect(0, 0, 10, 10, z: 2), Rect(4, 4, 6, 6, z: 2)]);

        var v = mesh.Vertices;
        var area = 0.0;
        for (var i = 0; i < mesh.Faces.Count; i += 3)
            area += Point3.Cross(v[mesh.Faces[i + 1]] - v[mesh.Faces[i]], v[mesh.Faces[i + 2]] - v[mesh.Faces[i]]).Z / 2;
        Assert.Equal(96, area, 6); // positive: facing up
        Assert.All(v, p => Assert.Equal(2, p.Z, 9));
    }

    [Theory]
    [InlineData(RegionFillStyle.InsideOffsets)]
    [InlineData(RegionFillStyle.OutsideOffsets)]
    [InlineData(RegionFillStyle.InsideOutsideOffsets)]
    [InlineData(RegionFillStyle.HorizontalHatch)]
    [InlineData(RegionFillStyle.VerticalHatch)]
    public void Fill_region_stays_inside_the_margin(RegionFillStyle style)
    {
        var outer = Rect(0, 0, 40, 30);
        var hole = Rect(15, 10, 25, 20);

        var lines = SurfaceBuilder.FillRegion([outer, hole], margin: 1, step: 2, style);

        Assert.True(lines.Count > 3, $"{lines.Count} polylines");
        foreach (var p in lines.SelectMany(l => l.Vertices).Select(v => v.Point.XY))
        {
            Assert.True(outer.Contains(p) && outer.DistanceTo(p) >= 1 - 1e-3, $"{p} too near the outline");
            Assert.True(!hole.Contains(p) && hole.DistanceTo(p) >= 1 - 1e-3, $"{p} too near the hole");
        }
        if (style is RegionFillStyle.HorizontalHatch)
            Assert.Contains(lines, l => !l.Closed && Math.Abs(l.Vertices[0].Point.Y - l.Vertices[^1].Point.Y) < 1e-9);
        if (style is RegionFillStyle.VerticalHatch)
            Assert.Contains(lines, l => !l.Closed && Math.Abs(l.Vertices[0].Point.X - l.Vertices[^1].Point.X) < 1e-9);
    }
}
