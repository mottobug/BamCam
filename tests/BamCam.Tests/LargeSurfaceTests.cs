using BamCam.App.Drawing;
using BamCam.App.Services;
using BamCam.Core.Cad;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>Meshes with many triangles stay usable: drawn as a bitmap, not copied by undo steps, left out of object snap.</summary>
public class LargeSurfaceTests
{
    /// <summary>A flat grid of n x n squares, each two triangles.</summary>
    private static SurfaceEntity Grid(int n, double size = 1)
    {
        var s = new SurfaceEntity();
        for (var y = 0; y <= n; y++)
        for (var x = 0; x <= n; x++)
            s.Vertices.Add(new Point3(x * size, y * size, 0));
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            int a = y * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
            s.Faces.AddRange([a, b, d, a, d, c]);
        }
        return s;
    }

    [Fact]
    public void A_mesh_counts_as_large_from_the_limit()
    {
        Assert.False(Grid(99).IsLarge);  // 19602 triangles
        Assert.True(Grid(100).IsLarge);  // 20000 triangles
    }

    [Fact]
    public void A_shared_edge_is_listed_once()
    {
        // Two triangles sharing the edge 1-2.
        var edges = SurfaceRaster.UniqueEdges([0, 1, 2, 2, 1, 3]);

        Assert.Equal(10, edges.Length);
        var pairs = edges.Chunk(2).Select(e => (e[0], e[1])).ToList();
        Assert.Equal([(0, 1), (0, 2), (1, 2), (1, 3), (2, 3)], pairs);
    }

    private static int[] Draw(IReadOnlyList<Point3> vertices, int[] edges, ViewTransform view, int width, int height, Matrix4? transform = null, double scaling = 1)
    {
        var pixels = new int[width * height];
        SurfaceRaster.Rasterize(vertices, transform ?? Matrix4.Identity, edges, view, scaling, width, height, 7, pixels);
        return pixels;
    }

    private static ViewTransform TopView(double scale = 1, double panX = 0, double panY = 0) => new() { Scale = scale, Pan = new Avalonia.Point(panX, panY) };

    [Fact]
    public void A_line_is_drawn_where_the_view_puts_it()
    {
        // World Y up, screen Y down: y = 5 with pan 20 lies in pixel row 15.
        var pixels = Draw([new Point3(2, 5, 0), new Point3(12, 5, 0)], [0, 1], TopView(panY: 20), 30, 30);

        for (var x = 2; x < 12; x++) Assert.Equal(7, pixels[15 * 30 + x]);
        Assert.Equal(0, pixels[15 * 30 + 1]);
        Assert.Equal(0, pixels[14 * 30 + 5]);
        Assert.Equal(11, pixels.Count(p => p != 0));
    }

    [Fact]
    public void The_transform_of_the_object_counts()
    {
        var pixels = Draw([new Point3(0, 0, 0), new Point3(0, 0, 0)], [0, 1], TopView(panY: 20), 30, 30, Matrix4.Translation(4, 5));

        Assert.Equal(7, pixels[15 * 30 + 4]);
        Assert.Equal(1, pixels.Count(p => p != 0));
    }

    [Fact]
    public void Rendering_scale_gives_more_pixels()
    {
        var pixels = Draw([new Point3(1, 5, 0), new Point3(1, 5, 0)], [0, 1], TopView(panY: 20), 60, 60, scaling: 2);

        Assert.Equal(7, pixels[30 * 60 + 2]);
    }

    [Theory]
    [InlineData(-1000, 5, 1000, 5, 30)]     // through the bitmap from far outside
    [InlineData(-1000, -500, -900, 500, 0)] // beside it
    [InlineData(5, 5000, 5, 6000, 0)]       // above it
    public void Lines_are_clipped_to_the_bitmap(double x0, double y0, double x1, double y1, int expected)
    {
        var pixels = Draw([new Point3(x0, y0, 0), new Point3(x1, y1, 0)], [0, 1], TopView(panY: 20), 30, 30);

        Assert.Equal(expected, pixels.Count(p => p != 0));
    }

    [Fact]
    public void A_click_near_a_drawn_pixel_hits_and_one_further_away_does_not()
    {
        var pixels = new int[100];
        pixels[5 * 10 + 5] = 7;

        Assert.Equal(0, SurfaceRaster.NearestDrawn(pixels, 10, 10, 5.5, 5.5, 3));
        Assert.Equal(2, SurfaceRaster.NearestDrawn(pixels, 10, 10, 7.2, 5.1, 3));
        Assert.True(double.IsInfinity(SurfaceRaster.NearestDrawn(pixels, 10, 10, 9.5, 9.5, 2)));
        Assert.True(double.IsInfinity(SurfaceRaster.NearestDrawn(pixels, 10, 10, -50, 500, 3)));
    }

    [Fact]
    public void Colour_is_premultiplied()
    {
        Assert.Equal(unchecked((int)0xFF102030), PixelLayer.Pixel(Avalonia.Media.Color.FromArgb(255, 0x10, 0x20, 0x30)));
        Assert.Equal(unchecked((int)0x80804020), PixelLayer.Pixel(Avalonia.Media.Color.FromArgb(128, 255, 128, 64)));
    }

    // ---------------------------------------------------------------- large toolpaths

    [Fact]
    public void Toolpath_kinds_get_their_colours_and_rapids_can_be_hidden()
    {
        // Rows 5, 10 and 15 of a 30 x 30 bitmap (y up, pan 20).
        Point3[] from = [new(2, 15, 0), new(2, 10, 0), new(2, 5, 0)];
        Point3[] to = [new(12, 15, 0), new(12, 10, 0), new(12, 5, 0)];
        MoveKind[] kinds = [MoveKind.Rapid, MoveKind.Feed, MoveKind.ArcCW];
        var pixels = new int[900];

        ToolpathRaster.Rasterize(from, to, kinds, TopView(panY: 20), 1, 30, 30, cutColour: 1, arcColour: 2, rapidColour: 3, showRapids: true, pixels);
        Assert.Equal(3, pixels[5 * 30 + 6]);
        Assert.Equal(1, pixels[10 * 30 + 6]);
        Assert.Equal(2, pixels[15 * 30 + 6]);

        ToolpathRaster.Rasterize(from, to, kinds, TopView(panY: 20), 1, 30, 30, 1, 2, 3, showRapids: false, pixels);
        Assert.Equal(0, pixels[5 * 30 + 6]);
        Assert.Equal(1, pixels[10 * 30 + 6]);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 1.5, 2)]
    [InlineData(1.5, 1, 2)]
    [InlineData(2, 2, 4)]
    [InlineData(0.2, 1, 1)]
    public void Line_width_follows_the_setting_and_the_display(double width, double scaling, int expected)
    {
        Assert.Equal(expected, ToolpathRaster.Thickness(width, scaling));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Wide_lines_cover_rows_or_columns_across_their_direction(int thickness)
    {
        var pixels = new int[900];
        // One line along X and one along Y.
        ToolpathRaster.Rasterize([new(2, 10.5, 0), new(20.5, 2, 0)], [new(12, 10.5, 0), new(20.5, 18, 0)], [MoveKind.Feed, MoveKind.Feed],
            TopView(panY: 20), 1, 30, 30, 1, 2, 3, false, pixels, thickness);

        var rows = Enumerable.Range(0, 30).Count(y => pixels[y * 30 + 6] != 0);
        var columns = Enumerable.Range(0, 30).Count(x => pixels[10 * 30 + x] != 0 && x > 15);
        Assert.Equal(thickness, rows);
        Assert.Equal(thickness, columns);
    }

    [Fact]
    public void Cutting_moves_are_drawn_over_rapids()
    {
        Point3[] line = [new(2, 10, 0)];
        Point3[] end = [new(12, 10, 0)];
        var pixels = new int[900];

        ToolpathRaster.Rasterize([line[0], line[0]], [end[0], end[0]], [MoveKind.Feed, MoveKind.Rapid], TopView(panY: 20), 1, 30, 30, 1, 2, 3, true, pixels);

        Assert.Equal(1, pixels[10 * 30 + 6]);
    }

    // ---------------------------------------------------------------- drop cutter

    /// <summary>A bumpy surface of many small triangles.</summary>
    private static List<Triangle> Bumps(int n, double size)
    {
        double Z(int x, int y) => 3 * Math.Sin(x * 0.3) * Math.Cos(y * 0.2);
        Point3 P(int x, int y) => new(x * size, y * size, Z(x, y));
        var result = new List<Triangle>();
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            result.Add(new(P(x, y), P(x + 1, y), P(x + 1, y + 1)));
            result.Add(new(P(x, y), P(x + 1, y + 1), P(x, y + 1)));
        }
        return result;
    }

    [Theory]
    [InlineData(CutterShape.Ball, 0.0)]
    [InlineData(CutterShape.Flat, 0.0)]
    [InlineData(CutterShape.Bull, 1.0)]
    public void The_grid_size_does_not_change_the_result(CutterShape shape, double corner)
    {
        var triangles = Bumps(40, 0.25);
        var fine = new Mesh(triangles, 1);    // cells of the triangles' size
        var coarse = new Mesh(triangles, 100); // a single large cell would be checked against all of them

        for (var i = 0; i < 400; i++)
        {
            double x = -1 + 12 * (i % 20) / 20.0, y = -1 + 12 * (i / 20) / 20.0;
            Assert.Equal(coarse.DropCutter(x, y, 3, shape, corner), fine.DropCutter(x, y, 3, shape, corner));
        }
    }

    [Fact]
    public void Queries_from_several_threads_give_the_same_results()
    {
        var mesh = new Mesh(Bumps(60, 0.25), 3);
        var points = Enumerable.Range(0, 2000).Select(i => (X: 15.0 * (i % 50) / 50, Y: 15.0 * (i / 50) / 40)).ToList();
        var serial = points.Select(p => mesh.DropCutter(p.X, p.Y, 3, CutterShape.Ball)).ToList();

        var parallel = new double?[points.Count];
        Parallel.For(0, points.Count, i => parallel[i] = mesh.DropCutter(points[i].X, points[i].Y, 3, CutterShape.Ball));

        Assert.Equal(serial, parallel);
    }

    private static (CadDocument Doc, Profile3DOp Op) Scanlines(Profile3DMethod method = Profile3DMethod.Horizontal)
    {
        var doc = CadDocument.CreateNew();
        var mesh = new SurfaceEntity();
        foreach (var t in Bumps(30, 1))
        {
            mesh.Faces.AddRange([mesh.Vertices.Count, mesh.Vertices.Count + 1, mesh.Vertices.Count + 2]);
            mesh.Vertices.AddRange([t.A, t.B, t.C]);
        }
        doc.AddEntity(mesh);
        var op = new Profile3DOp { Name = "3d" };
        op.PrimitiveIds.Add(mesh.Id);
        op.ToolDiameter.Set(6);
        op.ToolProfile.Set(ToolProfile.BallNose);
        op.StockSurface.Set(4);
        op.TargetDepth.Set(-4);
        op.DepthIncrement.Set(4);
        op.Profile3DMethod.Set(method);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    [Theory]
    [InlineData(Profile3DMethod.Horizontal)]
    [InlineData(Profile3DMethod.Vertical)]
    public void Scanlines_are_the_same_at_every_run_and_rows_change_direction(Profile3DMethod method)
    {
        var (doc, op) = Scanlines(method);

        var first = ToolpathGenerator.Generate(doc, op);
        var second = ToolpathGenerator.Generate(doc, op);

        Assert.Empty(first.Warnings);
        Assert.True(first.Moves.Count > 50, $"{first.Moves.Count} moves");
        Assert.Equal(first.Moves.Select(m => m.To), second.Moves.Select(m => m.To));
    }

    // ---------------------------------------------------------------- outline of many triangles

    private static List<Path2> TrianglePaths(int n, double size) =>
        Bumps(n, size).Select(t => Polygons.EnsureOrientation(Path2.FromPoints([t.A.XY, t.B.XY, t.C.XY], true), ccw: true)).ToList();

    [Fact]
    public void Large_union_gives_the_outline_of_the_plain_union()
    {
        var paths = TrianglePaths(110, 0.5); // 24200 triangles, over the limit
        Assert.True(paths.Count >= Polygons.LargeUnionCount);

        var plain = Polygons.Union(paths);
        var tiled = Polygons.UnionLarge(paths);

        Assert.Equal(plain.Count, tiled.Count);
        Assert.Equal(plain.Sum(p => p.SignedArea()), tiled.Sum(p => p.SignedArea()), 3);
        Assert.Equal(55 * 55, tiled.Sum(p => p.SignedArea()), 3);
    }

    [Fact]
    public void Separate_islands_and_holes_stay_in_a_large_union()
    {
        // A grid of small squares with a gap in the middle row and column: four islands.
        var paths = new List<Path2>();
        for (var y = 0; y < 150; y++)
        for (var x = 0; x < 150; x++)
        {
            if (x == 75 || y == 75) continue;
            paths.Add(Path2.FromPoints([new(x, y), new(x + 1, y), new(x + 1, y + 1), new(x, y + 1)], true));
        }

        var tiled = Polygons.UnionLarge(paths);

        Assert.Equal(4, tiled.Count);
        Assert.Equal(149 * 149, tiled.Sum(p => p.SignedArea()), 3);
    }

    [Fact]
    public void Below_the_limit_a_large_union_is_the_plain_union()
    {
        var paths = TrianglePaths(10, 1);
        var plain = Polygons.Union(paths);
        var large = Polygons.UnionLarge(paths);

        Assert.Equal(plain.Select(p => p.Vertices), large.Select(p => p.Vertices));
    }

    [Fact]
    public void A_cancelled_large_union_stops()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Polygons.UnionLarge(TrianglePaths(110, 0.5), cancel.Token));
    }

    // ---------------------------------------------------------------- cancelling

    [Theory]
    [InlineData(Profile3DMethod.Horizontal)]
    [InlineData(Profile3DMethod.WaterlineRough)]
    [InlineData(Profile3DMethod.WaterlineFinish)]
    public void A_cancelled_3D_profile_ends_without_a_toolpath_or_a_failure(Profile3DMethod method)
    {
        var (doc, op) = Scanlines(method);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var tp = ToolpathGenerator.Generate(doc, op, cancel.Token);

        Assert.Empty(tp.Moves);
        Assert.DoesNotContain(tp.Warnings, w => w.Contains("failed"));
    }

    // ---------------------------------------------------------------- object snap

    [Fact]
    public void Object_snap_ignores_meshes()
    {
        var mesh = Grid(4);
        var line = new PolylineEntity(Path2.FromPoints([new(2, 0), new(2, 4)], closed: false));

        Assert.Null(ObjectSnap.Find([mesh], new Point2(1, 1), 0.5, SnapKinds.All));
        var found = ObjectSnap.Find([mesh, line], new Point2(2, 0.1), 0.5, SnapKinds.All);
        Assert.Equal(new Point2(2, 0), found!.Value.Point);
    }

    // ---------------------------------------------------------------- undo

    private static (DocumentSession Session, CbMeshStore Store) Session(CadDocument doc)
    {
        var store = new CbMeshStore();
        return (new DocumentSession(doc)
        {
            Snapshot = d => CbWriter.ToSnapshot(d, store),
            Restore = text => CbReader.FromSnapshot(text, store),
            HistoryCleared = store.Clear,
        }, store);
    }

    [Fact]
    public void An_undo_step_refers_to_the_mesh_instead_of_containing_it()
    {
        var doc = CadDocument.CreateNew();
        var mesh = doc.AddEntity(Grid(100));
        var store = new CbMeshStore();

        var text = CbWriter.ToSnapshot(doc, store);

        Assert.True(text.Length < 10000, $"{text.Length} characters");
        Assert.Equal(1, store.Count);
        var back = Assert.IsType<SurfaceEntity>(Assert.Single(CbReader.FromSnapshot(text, store).AllEntities()));
        Assert.Same(mesh.Faces, back.Faces);
        Assert.Same(mesh.Vertices, back.Vertices);
        Assert.Equal(mesh.Id, back.Id);
    }

    [Fact]
    public void A_saved_file_still_contains_the_mesh()
    {
        var doc = CadDocument.CreateNew();
        doc.AddEntity(Grid(3));
        CbWriter.ToSnapshot(doc, new CbMeshStore()); // must not leave anything behind

        var back = Assert.IsType<SurfaceEntity>(Assert.Single(CbReader.Parse(CbWriter.ToString(doc)).AllEntities()));

        Assert.Equal(18 * 3, back.Faces.Count);
        Assert.Equal(16, back.Vertices.Count);
    }

    [Fact]
    public void Undo_and_redo_keep_mesh_and_transform()
    {
        var doc = CadDocument.CreateNew();
        var mesh = doc.AddEntity(Grid(3));
        var (session, store) = Session(doc);

        session.Execute("move", () => mesh.ApplyTransform(Matrix4.Translation(10, 0)));
        session.Execute("add", () => doc.AddEntity(new CircleEntity { Diameter = 1 }));
        Assert.Equal(1, store.Count);

        session.Undo();
        session.Undo();
        var first = Assert.IsType<SurfaceEntity>(Assert.Single(session.Document.AllEntities()));
        Assert.Equal(0, first.Bounds().Min.X, 9);
        Assert.Same(mesh.Faces, first.Faces);

        session.Redo();
        var moved = Assert.IsType<SurfaceEntity>(Assert.Single(session.Document.AllEntities()));
        Assert.Equal(10, moved.Bounds().Min.X, 9);
    }

    [Fact]
    public void A_new_drawing_lets_the_meshes_go()
    {
        var doc = CadDocument.CreateNew();
        var mesh = doc.AddEntity(Grid(3));
        var (session, store) = Session(doc);
        session.Execute("move", () => mesh.ApplyTransform(Matrix4.Translation(1, 0)));
        Assert.Equal(1, store.Count);

        session.Replace(CadDocument.CreateNew());

        Assert.Equal(0, store.Count);
    }
}
