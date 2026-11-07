using BamCam.App.Services;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

public class NumericTransformTests
{
    private static NumericTransform T(Point3? move = null, Point3? rotate = null, Point3? scale = null, Point3? center = null) =>
        new(move ?? Point3.Zero, rotate ?? Point3.Zero, scale ?? new Point3(1, 1, 1), center ?? Point3.Zero);

    private static void AssertPoint(double x, double y, double z, Point3 p)
    {
        Assert.Equal(x, p.X, 9);
        Assert.Equal(y, p.Y, 9);
        Assert.Equal(z, p.Z, 9);
    }

    [Fact]
    public void Nothing_set_changes_nothing()
    {
        Assert.True(T().Matrix.IsIdentity);
    }

    [Theory]
    // Right-hand rule, seen from the positive end of the axis: counter-clockwise.
    [InlineData(90, 0, 0, 0, 1, 0, 0, 0, 1)]   // about X: Y goes to Z
    [InlineData(0, 90, 0, 0, 0, 1, 1, 0, 0)]   // about Y: Z goes to X
    [InlineData(0, 0, 90, 1, 0, 0, 0, 1, 0)]   // about Z: X goes to Y
    public void Rotation_follows_the_right_hand_rule(double rx, double ry, double rz, double x, double y, double z, double ex, double ey, double ez)
    {
        var m = T(rotate: new Point3(rx, ry, rz)).Matrix;

        AssertPoint(ex, ey, ez, m.Transform(new Point3(x, y, z)));
    }

    [Fact]
    public void Order_is_scale_then_rotate_then_move()
    {
        // (1,0,0): scaled to (2,0,0), about Z to (0,2,0), moved to (10,2,0).
        var m = T(new Point3(10, 0, 0), new Point3(0, 0, 90), new Point3(2, 2, 2)).Matrix;

        AssertPoint(10, 2, 0, m.Transform(new Point3(1, 0, 0)));
    }

    [Fact]
    public void Rotations_run_about_X_then_Y_then_Z()
    {
        // (0,1,0): about X to (0,0,1), about Y to (1,0,0), about Z to (0,1,0).
        var m = T(rotate: new Point3(90, 90, 90)).Matrix;

        AssertPoint(0, 1, 0, m.Transform(new Point3(0, 1, 0)));
    }

    [Fact]
    public void The_centre_stays_where_it_is_and_the_move_comes_on_top()
    {
        var center = new Point3(10, 20, 30);
        var turned = T(rotate: new Point3(35, 70, 110), scale: new Point3(2, 3, 4), center: center).Matrix;
        AssertPoint(10, 20, 30, turned.Transform(center));

        var moved = T(new Point3(1, 2, 3), new Point3(0, 0, 90), center: center).Matrix;
        AssertPoint(11, 22, 33, moved.Transform(center));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void A_scale_of_0_is_not_valid(double x, double y, double z)
    {
        Assert.False(T(scale: new Point3(x, y, z)).IsValid);
    }

    // ---------------------------------------------------------------- circles and arcs

    [Theory]
    [InlineData(0, 0, 45, 1, 1, 1, true)]    // turning in the plane
    [InlineData(0, 0, 0, 2, 2, 2, true)]     // even scale
    [InlineData(0, 0, 0, 2, 2, 5, true)]     // Z does not matter in the plane
    [InlineData(180, 0, 0, 1, 1, 1, true)]   // a half turn only mirrors
    [InlineData(0, 180, 0, 1, 1, 1, true)]
    [InlineData(90, 0, 0, 1, 1, 1, false)]   // out of the plane
    [InlineData(0, 30, 0, 1, 1, 1, false)]
    [InlineData(0, 0, 0, 2, 1, 1, false)]    // uneven scale
    public void Arcs_are_kept_when_nothing_leaves_the_plane_and_the_scale_is_even(double rx, double ry, double rz, double sx, double sy, double sz, bool expected)
    {
        var m = T(rotate: new Point3(rx, ry, rz), scale: new Point3(sx, sy, sz)).Matrix;

        Assert.Equal(expected, NumericTransform.KeepsArcs(m));
    }

    public static TheoryData<string, Entity, bool> Objects() => new()
    {
        { "circle", new CircleEntity { Diameter = 10 }, true },
        { "arc", new ArcEntity { Radius = 5, Sweep = 90 }, true },
        { "text", new TextEntity(), true },
        { "rounded rectangle", new RectEntity { Width = 10, Height = 10, CornerRadius = 1 }, true },
        { "polyline with an arc", new PolylineEntity(new Path2([new Vertex(new Point3(0, 0, 0), 1), new Vertex(new Point3(1, 0, 0))], false)), true },
        { "straight polyline", new PolylineEntity(Path2.FromPoints([new(0, 0), new(1, 0)], false)), false },
        { "sharp rectangle", new RectEntity { Width = 10, Height = 10 }, false },
        { "mesh", new SurfaceEntity(), false },
        { "point list", new PointListEntity(), false },
    };

    [Theory]
    [MemberData(nameof(Objects))]
    public void Objects_with_arcs_are_left_out_of_a_turn_out_of_the_plane(string name, Entity entity, bool hasArcs)
    {
        Assert.True(hasArcs == NumericTransform.HasArcs(entity), name);
        Assert.Equal(!hasArcs, NumericTransform.CanApply(entity, T(rotate: new Point3(90, 0, 0)).Matrix));
        Assert.True(NumericTransform.CanApply(entity, T(rotate: new Point3(0, 0, 90)).Matrix));
    }

    // ---------------------------------------------------------------- objects

    private static SurfaceEntity Triangle() => new()
    {
        Vertices = { new Point3(0, 0, 0), new Point3(10, 0, 0), new Point3(0, 20, 0) },
        Faces = { 0, 1, 2 },
    };

    [Fact]
    public void A_mesh_turns_in_3D_and_the_turn_is_saved()
    {
        var doc = CadDocument.CreateNew();
        var mesh = doc.AddEntity(Triangle());

        mesh.ApplyTransform(T(rotate: new Point3(90, 0, 0)).Matrix);

        var box = mesh.Bounds();
        Assert.Equal(20, box.Depth, 9);
        Assert.Equal(0, box.Height, 9);
        var back = Assert.IsType<SurfaceEntity>(Assert.Single(CbReader.Parse(CbWriter.ToString(doc)).AllEntities()));
        Assert.Equal(20, back.Bounds().Depth, 6);
    }

    [Fact]
    public void A_straight_polyline_and_a_point_list_turn_in_3D()
    {
        var m = T(rotate: new Point3(90, 0, 0)).Matrix;
        var line = new PolylineEntity(Path2.FromPoints([new(0, 0), new(0, 10)], false));
        var points = new PointListEntity { Points = { new Point3(0, 10, 0) } };

        line.ApplyTransform(m);
        points.ApplyTransform(m);

        AssertPoint(0, 0, 10, line.Points[1].Point);
        AssertPoint(0, 0, 10, points.Points[0]);
    }

    [Fact]
    public void A_transform_of_a_mesh_is_one_undo_step_and_does_not_copy_it()
    {
        var doc = CadDocument.CreateNew();
        var mesh = doc.AddEntity(Triangle());
        var store = new CbMeshStore();
        var session = new DocumentSession(doc)
        {
            Snapshot = d => CbWriter.ToSnapshot(d, store),
            Restore = text => CbReader.FromSnapshot(text, store),
        };

        session.Execute("Transform", () => CadOps.Transform([mesh], T(rotate: new Point3(90, 0, 0)).Matrix));
        Assert.Equal(20, session.Document.AllEntities().Single().Bounds().Depth, 9);

        session.Undo();

        var back = Assert.IsType<SurfaceEntity>(session.Document.AllEntities().Single());
        Assert.Equal(0, back.Bounds().Depth, 9);
        Assert.Same(mesh.Faces, back.Faces);
    }

    // ---------------------------------------------------------------- matrix text and preview

    [Fact]
    public void A_matrix_survives_its_text()
    {
        var m = T(new Point3(1, 2, 3), new Point3(0, 0, 90), new Point3(2, 2, 2)).Matrix;

        var text = NumericTransform.FormatMatrix(m);

        Assert.Equal(4, text.Split('\n').Length);
        var back = NumericTransform.ParseMatrix(text)!.Value;
        AssertPoint(1, 2 + 2, 3, back.Transform(new Point3(1, 0, 0)));
    }

    [Fact]
    public void A_comma_is_a_decimal_point_in_the_matrix()
    {
        var m = NumericTransform.ParseMatrix("1,5 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1")!.Value;
        Assert.Equal(1.5, m.M11);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1 0 0 0  0 1 0 0  0 0 1 0  0 0 0")]             // 15 numbers
    [InlineData("1 0 0 0  0 1 0 0  0 0 1 0  0 0 0 1 7")]         // 17 numbers
    [InlineData("1 0 0 0  0 1 0 0  0 0 1 x  0 0 0 1")]           // not a number
    [InlineData("1 0 0 2  0 1 0 0  0 0 1 0  0 0 0 1")]           // last column
    [InlineData("0 0 0 0  0 1 0 0  0 0 1 0  0 0 0 1")]           // flattens
    public void A_matrix_that_is_no_usable_transform_is_refused(string text)
    {
        Assert.Null(NumericTransform.ParseMatrix(text));
    }

    [Fact]
    public void The_preview_is_the_box_after_the_transform()
    {
        var box = BoundingBox.FromPoints([new Point3(0, 0, 0), new Point3(10, 20, 30)]);

        var edges = NumericTransform.BoxEdges(box, T(new Point3(100, 0, 0)).Matrix);

        Assert.Equal(12, edges.Count);
        Assert.All(edges, e => Assert.True(e.From.X >= 100 && e.To.X <= 110));
    }
}
