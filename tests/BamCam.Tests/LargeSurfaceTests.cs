using BamCam.App.Drawing;
using BamCam.App.Services;
using BamCam.Core.Cad;
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
        Assert.Equal(unchecked((int)0xFF102030), SurfaceRaster.Pixel(Avalonia.Media.Color.FromArgb(255, 0x10, 0x20, 0x30)));
        Assert.Equal(unchecked((int)0x80804020), SurfaceRaster.Pixel(Avalonia.Media.Color.FromArgb(128, 255, 128, 64)));
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
