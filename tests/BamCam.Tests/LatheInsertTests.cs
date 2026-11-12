using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>Turning with the geometry of the insert: shape, approach angle, grooving blades with corner radii.</summary>
[Collection(GlobalLibrariesCollection.Name)]
public sealed class LatheInsertTests : IDisposable
{
    private readonly List<ToolLibrary> _tools = [.. CamLibraries.ToolLibraries];
    private readonly ToolLibrary _lib = new() { Name = "BamCam-Lathe-mm" };

    public LatheInsertTests()
    {
        CamLibraries.ToolLibraries.Clear();
        CamLibraries.ToolLibraries.Add(_lib);
    }

    public void Dispose()
    {
        CamLibraries.ToolLibraries.Clear();
        CamLibraries.ToolLibraries.AddRange(_tools);
    }

    private ToolDefinition Tool(int index, InsertShape shape, double approach, double diameter = 2, double corner = 0, LatheToolType type = LatheToolType.Turning)
    {
        var tool = new ToolDefinition
        {
            Index = index, Name = $"tool {index}", Diameter = diameter, ToolProfile = ToolProfile.Lathe, LatheToolType = type,
            InsertShape = shape, ApproachAngle = approach, CornerRadius = corner,
        };
        _lib.Tools.Add(tool);
        return tool;
    }

    /// <summary>A shaft of radius 10 that steps down to radius 4 towards the chuck over a flank at <paramref name="flankAngle"/>.</summary>
    private static (CadDocument Doc, LatheOp Op) Shaft(double flankAngle, int toolNumber)
    {
        var doc = new CadDocument();
        doc.Machining.MachiningOrigin = new OptionalPoint(new Point3(40, 0, 0));
        doc.Machining.Stock = new Stock { PMin = new Point3(-40, -12, -12), PMax = new Point3(2, 12, 12) };
        var top = -20 + 6 / Math.Tan(Angles.ToRadians(flankAngle));
        Point2[] latheZX = [new(-40, 4), new(-20, 4), new(top, 10), new(0, 10)];
        var shape = doc.AddEntity(new PolylineEntity(Path2.FromPoints(latheZX.Select(p => new Point2(p.X + 40, -p.Y)), false)));
        var op = new LatheOp { Name = "turn" };
        op.PrimitiveIds.Add(shape.Id);
        op.ToolDiameter.Set(2);
        op.ToolNumber.Set(toolNumber);
        op.StockSurface.Set(12);
        op.ClearancePlane.Set(15);
        op.DepthIncrement.Set(1);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    /// <summary>Whether material is left at the point (inside an outline and in none of its holes).</summary>
    private static bool Material(CadDocument doc, MachineOp op, Toolpath tp, double z, double radius)
    {
        var at = new Point2(40 + z, -radius);
        return Polygons.BuildShapes(TurningSimulation.Remaining(doc, new Dictionary<MachineOp, Toolpath> { [op] = tp }))
            .Any(s => s.Outer.Contains(at) && !s.Holes.Any(h => h.Contains(at)));
    }

    [Fact]
    public void The_insert_outline_has_its_nose_at_the_bottom_and_the_minor_edge_at_the_plunge_angle()
    {
        var tool = new ToolDefinition { InsertShape = InsertShape.V35, ApproachAngle = 93 };
        Assert.Equal(52, LatheInsert.MaxPlungeAngle(tool), 9);
        Assert.Equal(5, LatheInsert.MaxPlungeAngle(new ToolDefinition { InsertShape = InsertShape.C80, ApproachAngle = 95 }), 9);

        var outline = LatheInsert.Outline(tool, 0.4)!;
        var pts = outline.Flatten(0.001);
        // The nose circle around the origin is the lowest part; the insert reaches up and towards +Z.
        Assert.Equal(-0.4, pts.Min(p => p.Y), 2);
        Assert.True(pts.Max(p => p.Y) > 5);
        Assert.True(pts.Average(p => p.X) > 0);
        // Left hand mirrors it along Z.
        Assert.True(LatheInsert.Outline(tool, 0.4, leftHand: true)!.Flatten(0.001).Average(p => p.X) < 0);
        Assert.Null(LatheInsert.Outline(new ToolDefinition { InsertShape = InsertShape.Round }, 0.4));
    }

    [Fact]
    public void A_flank_steeper_than_the_insert_allows_leaves_material_and_says_so()
    {
        Tool(7, InsertShape.V35, 93); // down at most 52°
        var (doc, op) = Shaft(60, 7);

        var tp = ToolpathGenerator.Generate(doc, op);

        var warning = Assert.Single(tp.Warnings, w => w.Contains("cannot follow the profile"));
        Assert.Contains("V35", warning);
        // At the foot of the 60° flank a wedge stays that the nose alone would have cut.
        Assert.True(Material(doc, op, tp, -20.4, 4.3), "the wedge at the foot of the flank stays");
        Assert.False(Material(doc, op, tp, -30, 4.5), "the shaft is turned to radius 4 further on");
        Assert.False(Material(doc, op, tp, -5, 10.5), "the large diameter is turned");
    }

    [Fact]
    public void The_nose_alone_cuts_the_wedge()
    {
        Tool(7, InsertShape.None, 93);
        var (doc, op) = Shaft(60, 7);

        var tp = ToolpathGenerator.Generate(doc, op);

        Assert.DoesNotContain(tp.Warnings, w => w.Contains("cannot follow"));
        Assert.False(Material(doc, op, tp, -20.4, 4.3));
    }

    [Fact]
    public void A_flank_the_insert_can_follow_gives_the_same_contour_as_the_nose()
    {
        Tool(7, InsertShape.V35, 93);
        Tool(8, InsertShape.None, 93);
        var (doc, op) = Shaft(45, 7);
        op.RoughingFinishing.Set(RoughingFinishing.Finishing);

        var withInsert = ToolpathGenerator.Generate(doc, op);
        op.ToolNumber.Set(8);
        var noseOnly = ToolpathGenerator.Generate(doc, op);

        Assert.DoesNotContain(withInsert.Warnings, w => w.Contains("cannot follow"));
        Assert.Equal(noseOnly.Moves.Select(m => (m.Kind, Math.Round(m.To.X, 6), Math.Round(m.To.Y, 6))),
            withInsert.Moves.Select(m => (m.Kind, Math.Round(m.To.X, 6), Math.Round(m.To.Y, 6))));
    }

    [Fact]
    public void Without_an_insert_shape_the_toolpath_is_unchanged()
    {
        Tool(8, InsertShape.None, 93);
        var (doc, op) = Shaft(60, 8);
        var withLibrary = ToolpathGenerator.Generate(doc, op);
        op.ToolNumber.Set(0);
        var withoutLibrary = ToolpathGenerator.Generate(doc, op);

        Assert.Equal(withoutLibrary.Moves, withLibrary.Moves);
    }

    [Fact]
    public void A_blade_with_corner_radii_leaves_round_corners_and_warns_about_ridges()
    {
        Tool(3, InsertShape.None, 93, diameter: 3, corner: 0.5, type: LatheToolType.Grooving);
        var doc = new CadDocument();
        doc.Machining.MachiningOrigin = new OptionalPoint(new Point3(40, 0, 0));
        doc.Machining.Stock = new Stock { PMin = new Point3(-40, -12, -12), PMax = new Point3(2, 12, 12) };
        // A groove from Z -20 to -12, down to radius 6.
        Point2[] latheZX = [new(-20, 10), new(-20, 6), new(-12, 6), new(-12, 10)];
        var shape = doc.AddEntity(new PolylineEntity(Path2.FromPoints(latheZX.Select(p => new Point2(p.X + 40, -p.Y)), false)));
        var op = new LatheGrooveOp { Name = "groove" };
        op.PrimitiveIds.Add(shape.Id);
        op.ToolDiameter.Set(3);
        op.ToolNumber.Set(3);
        op.StockSurface.Set(10);
        op.ClearancePlane.Set(12);
        op.StepOver.Set(0.9); // 2.7 apart, the flat part of the blade is 2
        op.FinishPass.Set(false);
        doc.ActivePart.Operations.Add(op);

        var tp = ToolpathGenerator.Generate(doc, op);

        Assert.Contains(tp.Warnings, w => w.Contains("ridges"));
        // The corner at the bottom of the groove wall keeps the blade's radius; the middle of the bottom is cut.
        Assert.True(Material(doc, op, tp, -19.9, 6.1), "round corner at the wall");
        Assert.False(Material(doc, op, tp, -18.5, 6.1), "bottom under the flat of the blade");
        Assert.False(Material(doc, op, tp, -18.5, 11.5), "up to the stock surface, no lid left");
        Assert.True(Material(doc, op, tp, -17.15, 6.1), "a ridge between two plunges");

        op.StepOver.Set(0.6);
        Assert.DoesNotContain(ToolpathGenerator.Generate(doc, op).Warnings, w => w.Contains("ridges"));
    }

    [Fact]
    public void Insert_values_are_saved_in_the_tool_library()
    {
        var lib = new ToolLibrary { Name = "Inserts" };
        lib.Tools.Add(new ToolDefinition { Index = 1, Name = "VBMT", Diameter = 0.8, LatheToolType = LatheToolType.Turning, InsertShape = InsertShape.V35, ApproachAngle = 107.5, CuttingEdgeLength = 16 });
        lib.Tools.Add(new ToolDefinition { Index = 2, Name = "Blade", Diameter = 3, LatheToolType = LatheToolType.Grooving, CornerRadius = 0.2 });
        var path = Path.Combine(Path.GetTempPath(), $"bamcam-inserts-{Guid.NewGuid():N}.xml");
        try
        {
            LibraryFiles.SaveTools(lib, path);
            var again = LibraryFiles.LoadTools(path);
            Assert.Equal((InsertShape.V35, 107.5, 16.0), (again.Tools[0].InsertShape, again.Tools[0].ApproachAngle, again.Tools[0].CuttingEdgeLength));
            Assert.Equal(0.2, again.Tools[1].CornerRadius);
            Assert.Equal(InsertShape.None, again.Tools[1].InsertShape);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
