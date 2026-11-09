using BamCam.Core.Cam;
using BamCam.Core.Geometry;

namespace BamCam.Tests;

/// <summary>Edit > Surface > Silhouette and Edge Detect (issue 6).</summary>
public class MeshOutlinesTests
{
    /// <summary>The faces of a box as triangles, each face split along a diagonal; <paramref name="top"/> false leaves the top open.</summary>
    private static List<Triangle> Box(double x0, double y0, double z0, double x1, double y1, double z1, bool top = true)
    {
        Point3 P(double x, double y, double z) => new(x, y, z);
        var tris = new List<Triangle>();
        void Quad(Point3 a, Point3 b, Point3 c, Point3 d)
        {
            tris.Add(new Triangle(a, b, c));
            tris.Add(new Triangle(a, c, d));
        }
        Quad(P(x0, y0, z0), P(x0, y1, z0), P(x1, y1, z0), P(x1, y0, z0)); // bottom
        if (top) Quad(P(x0, y0, z1), P(x1, y0, z1), P(x1, y1, z1), P(x0, y1, z1));
        Quad(P(x0, y0, z0), P(x1, y0, z0), P(x1, y0, z1), P(x0, y0, z1));
        Quad(P(x1, y0, z0), P(x1, y1, z0), P(x1, y1, z1), P(x1, y0, z1));
        Quad(P(x1, y1, z0), P(x0, y1, z0), P(x0, y1, z1), P(x1, y1, z1));
        Quad(P(x0, y1, z0), P(x0, y0, z0), P(x0, y0, z1), P(x0, y1, z1));
        return tris;
    }

    private static double Length(IEnumerable<Path2> paths) => paths.Sum(p => p.Length);

    [Fact]
    public void A_box_has_twelve_sharp_edges_and_no_face_diagonals()
    {
        var edges = MeshOutlines.Edges(Box(0, 0, 0, 10, 20, 5), 30);

        Assert.Equal(12, edges.Sum(p => p.Count - 1));
        Assert.Equal(4 * (10 + 20 + 5), Length(edges), 6);
        Assert.All(edges, p => Assert.All(p.Vertices, v => Assert.True(v.Point.Z is 0 or 5)));
    }

    [Fact]
    public void Edges_of_triangles_with_their_own_corners_are_welded()
    {
        // As in an STL file: every triangle has its own copies of the corners.
        var tris = Box(0, 0, 0, 10, 10, 10).Select(t => new Triangle(t.A + new Point3(0, 0, 1e-9), t.B, t.C)).ToList();

        Assert.Equal(12, MeshOutlines.Edges(tris, 30).Sum(p => p.Count - 1));
    }

    [Fact]
    public void The_rim_of_an_open_box_is_a_closed_outline()
    {
        // A cavity: the open top shows as one closed loop at the rim, the other edges at the bottom and corners.
        var edges = MeshOutlines.Edges(Box(0, 0, -8, 30, 20, 0, top: false), 30);

        var rim = edges.Where(p => p.Vertices.All(v => Math.Abs(v.Point.Z) < 1e-9)).ToList();
        Assert.Equal(2 * (30 + 20), Length(rim), 6);
        // Above a large angle only the open border is left: the rim.
        var border = MeshOutlines.Edges(Box(0, 0, -8, 30, 20, 0, top: false), 179);
        Assert.Single(border);
        Assert.True(border[0].Closed);
        Assert.Equal(2 * (30 + 20), border[0].Length, 6);
    }

    [Fact]
    public void Plane_slices_go_all_round_at_each_level()
    {
        // A box 10 x 20 x 6: slices across Z every 2 below the top (4 and 2) are its outline at those heights.
        var tris = Box(0, 0, 0, 10, 20, 6);

        var z = MeshOutlines.PlaneSlice(tris, 2, 2);
        Assert.Equal(2, z.Count);
        Assert.All(z, p => Assert.True(p.Closed));
        Assert.All(z, p => Assert.Equal(60, p.Length, 6));
        Assert.Equal([2.0, 4.0], z.Select(p => Math.Round(p.Vertices[0].Point.Z, 9)).Order());

        // Across X every 3 from x = 10 down: x = 7, 4, 1, each the 20 x 6 cross section.
        var x = MeshOutlines.PlaneSlice(tris, 0, 3);
        Assert.Equal(3, x.Count);
        Assert.All(x, p => Assert.Equal(2 * (20 + 6), p.Length, 6));
        Assert.All(x, p => Assert.All(p.Vertices, v => Assert.Equal(p.Vertices[0].Point.X, v.Point.X, 9)));
    }

    [Fact]
    public void A_slice_through_a_corner_or_a_face_stays_one_closed_line()
    {
        // The step pyramid sliced at the heights of its steps: corners and faces lie exactly in the planes.
        var tris = Box(0, 0, 0, 10, 10, 2).Concat(Box(2, 2, 2, 8, 8, 4)).Concat(Box(4, 4, 4, 6, 6, 6)).ToList();

        var slices = MeshOutlines.PlaneSlice(tris, 2, 1);

        Assert.All(slices, p => Assert.True(p.Closed, "every slice closes"));
        Assert.Equal(5, slices.Select(p => Math.Round(p.Vertices[0].Point.Z, 6)).Distinct().Count());
    }

    [Fact]
    public void Lines_are_laid_onto_the_surface_and_miss_to_the_lowest_height()
    {
        // A ramp from z 0 at x 0 to z 10 at x 10, over y 0..10.
        Point3 P(double x, double y) => new(x, y, x);
        var ramp = new List<Triangle> { new(P(0, 0), P(10, 0), P(10, 10)), new(P(0, 0), P(10, 10), P(0, 10)) };
        var line = Path2.FromPoints([new(2, 5), new(8, 5)], false);
        var beyond = Path2.FromPoints([new(-5, 5), new(5, 5)], false);

        var projected = MeshOutlines.Project([line, beyond], ramp, 0.5);

        Assert.Equal(13, projected[0].Count); // 6 / 0.5 + 1
        Assert.All(projected[0].Vertices, v => Assert.Equal(v.Point.X, v.Point.Z, 9));
        // Off the surface: the lowest height of the mesh (0).
        Assert.All(projected[1].Vertices.Where(v => v.Point.X < 0), v => Assert.Equal(0, v.Point.Z, 9));
    }

    [Fact]
    public void Inverted_faces_turn_every_normal_round()
    {
        var faces = new List<int> { 0, 1, 2, 2, 3, 0 };

        Assert.Equal([0, 2, 1, 2, 0, 3], MeshOutlines.InvertedFaces(faces));
    }

    [Fact]
    public void One_silhouette_is_the_whole_outline_at_the_top()
    {
        var tris = Box(0, 0, 0, 10, 10, 2).Concat(Box(2, 2, 2, 4, 4, 9)).ToList();

        var outline = MeshOutlines.Silhouette(tris, 0);

        Assert.Single(outline);
        Assert.True(outline[0].Closed);
        Assert.Equal(100, Math.Abs(outline[0].SignedArea()), 6);
        Assert.All(outline[0].Vertices, v => Assert.Equal(9, v.Point.Z, 9));
    }

    [Fact]
    public void Silhouettes_by_height_project_overhangs_down()
    {
        // A mushroom: a thin stem from 0 to 5, a wide cap from 5 to 10.
        var tris = Box(4, 4, 0, 6, 6, 5).Concat(Box(0, 0, 5, 10, 10, 10)).ToList();

        var levels = MeshOutlines.Silhouette(tris, 2.5).GroupBy(p => Math.Round(p.Vertices[0].Point.Z, 6)).ToDictionary(g => g.Key, g => g.ToList());

        Assert.Equal([0, 2.5, 5, 7.5], levels.Keys.Order());
        // Below the cap the stem alone would be 2 x 2; the cap above it is projected down.
        foreach (var z in new[] { 0, 2.5, 5, 7.5 })
            Assert.Equal(100, levels[z].Sum(p => Math.Abs(p.SignedArea())), 6);
    }

    [Fact]
    public void Silhouettes_by_height_grow_downwards()
    {
        // A step pyramid: 2 x 2 on top of 6 x 6 on top of 10 x 10.
        var tris = Box(0, 0, 0, 10, 10, 2).Concat(Box(2, 2, 2, 8, 8, 4)).Concat(Box(4, 4, 4, 6, 6, 6)).ToList();

        var levels = MeshOutlines.Silhouette(tris, 1).GroupBy(p => Math.Round(p.Vertices[0].Point.Z, 6))
            .ToDictionary(g => g.Key, g => g.Sum(p => Math.Abs(p.SignedArea())));

        // Between the steps; a level at the height of a step's top already holds that step.
        Assert.Equal(4, levels[5], 6);
        Assert.Equal(36, levels[3], 6);
        Assert.Equal(36, levels[4], 6);
        Assert.Equal(100, levels[1], 6);
    }
}
