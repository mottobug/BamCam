using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Simulation;

namespace BamCam.Tests;

/// <summary>Operations mirrored with their transform (scale -1), as for the two halves of a mold (issue 13).</summary>
public class MirroredOperationTests
{
    /// <summary>A sloped triangle at X 10..20, Y 2..8 (Z 0 at its low edge, -4 at its tip), wholly at positive Y.</summary>
    private static SurfaceEntity Ramp() => new()
    {
        Vertices = { new Point3(10, 2, 0), new Point3(20, 2, 0), new Point3(15, 8, -4) },
        Faces = { 0, 1, 2 },
    };

    [Fact]
    public void The_simulation_target_of_a_mirrored_3D_profile_is_the_mirrored_surface()
    {
        var doc = CadDocument.CreateNew();
        var surface = doc.AddEntity(Ramp());
        var op = new Profile3DOp { Name = "half 2" };
        op.PrimitiveIds.Add(surface.Id);
        op.MOPTransform.Set(Matrix4.Scale(1, -1));
        doc.ActivePart.Operations.Add(op);

        var f = new HeightField(0, -10, 0.25, 120, 80, 0, -10);
        MillingSimulation.SetTargets(f, doc, [op]);

        float At(double x, double y) => f.Target[(int)((y - f.Y0) / f.Cell) * f.Nx + (int)((x - f.X0) / f.Cell)];
        // The target lies at negative Y, where the operation cuts, and nowhere at positive Y.
        Assert.InRange(At(15, -5), -2.2, -1.8);
        Assert.True(float.IsNaN(At(15, 5)));
    }

    /// <summary>
    /// Issue 13 (Bob's test file): a trough cut with its outline as boundary shape, the copy mirrored with scale Y -1.
    /// The boundary moves with the surface, so the copy cuts the mirrored trough, not a flat pocket in the old place.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_boundary_shapes_and_the_clip_area_move_with_the_transform(bool clipArea)
    {
        var doc = CadDocument.CreateNew();
        // Trough along Y: Z 0 at X 10 and 20, -2 in the middle, from Y 2 to Y 8.
        var trough = doc.AddEntity(new SurfaceEntity
        {
            Vertices = { new Point3(10, 2, 0), new Point3(15, 2, -2), new Point3(20, 2, 0), new Point3(10, 8, 0), new Point3(15, 8, -2), new Point3(20, 8, 0) },
            Faces = { 0, 4, 3, 0, 1, 4, 1, 5, 4, 1, 2, 5 },
        });
        var outline = doc.AddEntity(RectEntity.FromCorners(new Point2(10, 2), new Point2(20, 8)));
        var op = new Profile3DOp { Name = "half 2" };
        op.PrimitiveIds.Add(trough.Id);
        op.BoundaryMethod.Set(BoundaryMethod.SelectedShapes);
        op.BoundaryShapes.Add(outline.Id);
        if (clipArea)
        {
            op.ClipAreaMin.Set(new Point2(12, 0));
            op.ClipAreaMax.Set(new Point2(30, 10));
        }
        op.ToolProfile.Set(ToolProfile.BallNose);
        op.ToolDiameter.Set(2);
        op.StockSurface.Set(0);
        op.TargetDepth.Set(-3);
        op.ClearancePlane.Set(2);
        op.MOPTransform.Set(Matrix4.Scale(1, -1));
        doc.ActivePart.Operations.Add(op);

        var cuts = ToolpathGenerator.Generate(doc, op).Moves.Where(m => m.Kind == MoveKind.Feed).Select(m => m.To).ToList();

        Assert.NotEmpty(cuts);
        Assert.All(cuts, p => Assert.InRange(p.Y, -8.001, -1.999));
        Assert.All(cuts, p => Assert.InRange(p.X, clipArea ? 11.999 : 9.999, 20.001));
        // Following the trough: deepest in its middle, not a flat bottom at target depth.
        Assert.True(cuts.Min(p => p.Z) > -2.6, $"lowest cut {cuts.Min(p => p.Z)}");
        Assert.True(cuts.Max(p => p.Z) > -1, $"highest cut {cuts.Max(p => p.Z)}");
    }

    [Fact]
    public void The_start_point_moves_with_the_transform()
    {
        var op = new PocketOp();
        op.StartPoint.Set(new OptionalPoint(new Point3(3, 4, 0)));
        op.MOPTransform.Set(Matrix4.Scale(1, -1));

        Assert.Equal(new Point2(3, -4), op.StartXY);
    }

    [Theory]
    [InlineData(1, -1, 1, 0, 0, 0, 0)]
    [InlineData(-1, 1, 1, 0, 0, 0, 0)]
    [InlineData(1, 1, 1, 30, 5, -7, 2)]
    [InlineData(2, -0.5, 1.5, -120, 0, 40, 0)]
    [InlineData(-1, 1, 1, 90, 10, 0, 0)]
    public void Scale_rotation_and_move_come_back_from_the_matrix(double sx, double sy, double sz, double rz, double mx, double my, double mz)
    {
        var parts = new TransformParts(sx, sy, sz, rz, mx, my, mz);
        var m = parts.ToMatrix();

        var back = TransformParts.From(m);

        Assert.NotNull(back);
        // The same matrix (a mirror can come back written differently, e.g. X -1 rotated 90° as Y -1 rotated -90°).
        var again = back!.Value.ToMatrix().ToArray();
        var expected = m.ToArray();
        for (var i = 0; i < 16; i++) Assert.Equal(expected[i], again[i], 9);
    }

    [Fact]
    public void Plain_mirrors_are_shown_as_a_scale_of_minus_one()
    {
        Assert.Equal(new TransformParts(ScaleY: -1), TransformParts.From(Matrix4.Scale(1, -1)));
        Assert.Equal(new TransformParts(ScaleX: -1), TransformParts.From(Matrix4.Scale(-1, 1)));
        Assert.Equal(TransformParts.None, TransformParts.From(Matrix4.Identity));
    }

    [Fact]
    public void A_matrix_turning_about_Y_or_skewed_has_no_parts()
    {
        Assert.Null(TransformParts.From(Matrix4.RotationY(Math.PI / 4)));
        // Half a turn about Y is the same as mirroring X and Z.
        Assert.Equal(new TransformParts(ScaleX: -1, ScaleZ: -1), TransformParts.From(Matrix4.RotationY(Math.PI)));
        var skew = Matrix4.Identity with { M21 = 0.5 };
        Assert.Null(TransformParts.From(skew));
    }
}
