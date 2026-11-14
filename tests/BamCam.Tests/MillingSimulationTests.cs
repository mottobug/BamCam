using BamCam.Core.Cam;
using BamCam.Core.Simulation;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

public class MillingSimulationTests
{
    private static float At(HeightField f, double x, double y) =>
        f[(int)((x - f.X0) / f.Cell), (int)((y - f.Y0) / f.Cell)];

    private static float TargetAt(HeightField f, double x, double y) =>
        f.Target[(int)((y - f.Y0) / f.Cell) * f.Nx + (int)((x - f.X0) / f.Cell)];

    [Fact]
    public void End_mill_and_ball_nose_cut_their_shape()
    {
        var f = new HeightField(0, 0, 0.05, 400, 400, 0, -10);
        var flat = new SimTool(2, ToolProfile.EndMill);
        MillingSimulation.Cut(f, new MillingSimulation.Step(new(2, 5, -1), new(18, 5, -1), flat, null!));
        Assert.Equal(-1, At(f, 10, 5), 3);
        Assert.Equal(-1, At(f, 10, 6.9), 3);
        Assert.Equal(0, At(f, 10, 7.1), 3);

        var ball = new SimTool(2, ToolProfile.BallNose);
        MillingSimulation.Cut(f, new MillingSimulation.Step(new(2, 12, -2), new(18, 12, -2), ball, null!));
        Assert.Equal(-2, At(f, 10, 12.025), 2);
        // Beside the axis (cell centre 1.025 away) the ball is 2 - sqrt(4 - 1.025²) higher than its tip.
        Assert.Equal(-2 + (2 - Math.Sqrt(4 - 1.025 * 1.025)), At(f, 10, 13.0), 3);

        // A plunge (sloped move) cuts at its lowest point.
        MillingSimulation.Cut(f, new MillingSimulation.Step(new(5, 17, 0), new(5, 17, -3), flat, null!));
        Assert.Equal(-3, At(f, 5, 17), 3);
    }

    [Fact]
    public void Pocket_leaves_rest_material_only_in_the_corners()
    {
        var doc = new CadDocument();
        var square = new PolylineEntity(Path2.FromPoints([new(0, 0), new(30, 0), new(30, 20), new(0, 20)], closed: true));
        doc.AddEntity(square);
        var op = new PocketOp { Name = "pocket" };
        op.PrimitiveIds.Add(square.Id);
        op.ToolDiameter.Set(6);
        op.TargetDepth.Set(-2);
        op.DepthIncrement.Set(2);
        doc.ActivePart.Operations.Add(op);
        var tp = ToolpathGenerator.Generate(doc, op);
        var jobs = new List<(MachineOp, Toolpath)> { (op, tp) };
        var f = MillingSimulation.Create(doc, jobs, 0.1);
        MillingSimulation.SetTargets(f, doc, [op]);
        var moves = MillingSimulation.Moves(doc, jobs, 0.05);
        // One simulation step per toolpath move (G0 / G1 / G2 / G3), numbered from 1.
        Assert.Equal(tp.Moves.Count(m => m.IsMotion), moves.Count);
        Assert.Equal(Enumerable.Range(1, moves.Count), moves.Select(m => m.Index));
        foreach (var m in moves) MillingSimulation.Cut(f, m);

        Assert.Equal(-2, TargetAt(f, 15, 10), 6);
        Assert.True(float.IsNaN(TargetAt(f, -1, 10)), "no target outside the pocket");
        Assert.Equal(-2, At(f, 15, 10), 3);
        Assert.Equal(-2, At(f, 1, 10), 3); // along the wall
        // The corner the round tool cannot reach keeps material.
        Assert.True(At(f, 0.3, 0.3) > -2 + 0.5, $"corner {At(f, 0.3, 0.3)}");
    }

    [Fact]
    public void Plasma_cuts_through_the_sheet_as_wide_as_the_kerf()
    {
        var doc = new CadDocument();
        var outline = new PolylineEntity(Path2.FromPoints([new(0, 0), new(40, 0), new(40, 40), new(0, 40)], closed: true));
        var hole = new PolylineEntity(Path2.FromPoints([new(15, 15), new(25, 15), new(25, 25), new(15, 25)], closed: true));
        doc.AddEntity(outline);
        doc.AddEntity(hole);
        doc.Machining.Stock = new Stock { PMin = new Point3(-10, -10, -3), PMax = new Point3(50, 50, 0) };
        var op = new PlasmaOp { Name = "plasma" };
        op.PrimitiveIds.Add(outline.Id);
        op.PrimitiveIds.Add(hole.Id);
        op.KerfWidth.Set(2);
        doc.ActivePart.Operations.Add(op);
        Assert.True(MillingSimulation.CanSimulate(op));
        var tp = ToolpathGenerator.Generate(doc, op);
        var jobs = new List<(MachineOp, Toolpath)> { (op, tp) };
        var f = MillingSimulation.Create(doc, jobs, 0.1);
        foreach (var m in MillingSimulation.Moves(doc, jobs, 0.05)) MillingSimulation.Cut(f, m);

        // The torch runs above the sheet, but cuts through it: outside the outline, inside the hole.
        Assert.Equal(-3, At(f, -1, 20), 3);
        Assert.Equal(-3, At(f, 16, 20), 3);
        // Only the kerf: the part and the scrap keep their material.
        Assert.Equal(0, At(f, 8, 20), 3);
        Assert.Equal(0, At(f, 20, 20), 3);
        Assert.Equal(0, At(f, -5, 20), 3);
    }

    [Fact]
    public void V_bit_tool_follows_the_cone()
    {
        var v = new SimTool(5, ToolProfile.VCutter, veeAngle: 90);
        Assert.Equal(0, v.HeightAt(0), 9);
        Assert.Equal(2, v.HeightAt(2), 9);
        var bull = new SimTool(3, ToolProfile.BullNose, cornerRadius: 1);
        Assert.Equal(0, bull.HeightAt(1.5), 9);
        Assert.Equal(1, bull.HeightAt(3), 9);
    }

    // ---------------------------------------------------------------- issue 5: cell size, tool shape, export

    private static (CadDocument Doc, List<(MachineOp, Toolpath)> Jobs) Pocket()
    {
        var doc = new CadDocument();
        var square = new PolylineEntity(Path2.FromPoints([new(0, 0), new(30, 0), new(30, 20), new(0, 20)], closed: true));
        doc.AddEntity(square);
        var op = new PocketOp { Name = "pocket" };
        op.PrimitiveIds.Add(square.Id);
        op.ToolDiameter.Set(6);
        op.TargetDepth.Set(-2);
        op.DepthIncrement.Set(2);
        doc.ActivePart.Operations.Add(op);
        return (doc, [(op, ToolpathGenerator.Generate(doc, op))]);
    }

    [Fact]
    public void A_cell_size_too_small_is_enlarged_so_that_the_field_covers_everything()
    {
        var (doc, jobs) = Pocket();

        var f = MillingSimulation.Create(doc, jobs, 0.005);

        Assert.True(f.Cell > 0.005);
        Assert.True(f.Nx <= MillingSimulation.MaxCells && f.Ny <= MillingSimulation.MaxCells, $"{f.Nx} x {f.Ny}");
        Assert.True(f.X0 <= 0 && f.X0 + f.Nx * f.Cell >= 30, $"{f.X0} + {f.Nx} x {f.Cell}");
        Assert.True(f.Y0 <= 0 && f.Y0 + f.Ny * f.Cell >= 20);
        Assert.Equal(0.1, MillingSimulation.Create(doc, jobs, 0.1).Cell, 9); // a size that fits stays
    }

    /// <summary>Every edge of a closed mesh is used twice, once in each direction.</summary>
    private static void AssertClosed(SurfaceEntity s)
    {
        var edges = new Dictionary<(int, int), int>();
        for (var t = 0; t < s.Faces.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                var e = (s.Faces[t + k], s.Faces[t + (k + 1) % 3]);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        Assert.All(edges, e => Assert.True(e.Value == 1 && edges.GetValueOrDefault((e.Key.Item2, e.Key.Item1)) == 1, $"edge {e.Key}"));
    }

    /// <summary>Volume of a closed mesh; positive when its triangles face outwards.</summary>
    private static double Volume(SurfaceEntity s)
    {
        var volume = 0.0;
        for (var t = 0; t < s.Faces.Count; t += 3)
        {
            var (a, b, c) = (s.Vertices[s.Faces[t]], s.Vertices[s.Faces[t + 1]], s.Vertices[s.Faces[t + 2]]);
            volume += Point3.Dot(a, Point3.Cross(b, c)) / 6;
        }
        return volume;
    }

    [Fact]
    public void The_material_becomes_a_closed_mesh_with_its_volume()
    {
        var f = new HeightField(0, 0, 1, 11, 6, 0, -5);
        var surface = MillingSimulation.ToSurface(f);

        AssertClosed(surface);
        // Through the centres of the cells: 10 x 5 wide, 5 high.
        Assert.Equal(10 * 5 * 5, Volume(surface), 6);

        // A cut 2 deep over a quarter of the cells takes its share away.
        var cut = new HeightField(0, 0, 1, 11, 6, 0, -5);
        MillingSimulation.Cut(cut, new MillingSimulation.Step(new(0, 3, -2), new(20, 3, -2), new SimTool(10, ToolProfile.EndMill), null!));
        var after = MillingSimulation.ToSurface(cut);
        AssertClosed(after);
        Assert.True(Volume(after) < 250 - 10 && Volume(after) > 0, $"{Volume(after)}");
    }

    [Fact]
    public void A_cut_through_to_the_bottom_still_gives_a_closed_mesh()
    {
        var f = new HeightField(0, 0, 1, 8, 8, 0, -3);
        MillingSimulation.Cut(f, new MillingSimulation.Step(new(-5, 0, -3), new(20, 0, -3), new SimTool(2, ToolProfile.EndMill), null!));

        var surface = MillingSimulation.ToSurface(f);

        Assert.True(surface.Faces.Count > 0);
        Assert.True(Volume(surface) > 0);
        Assert.All(surface.Vertices, v => Assert.True(v.Z >= -3 - 1e-6));
    }

    [Fact]
    public void A_large_field_is_thinned_to_the_limit_of_triangles()
    {
        var f = new HeightField(0, 0, 0.1, 1500, 1000, 0, -5);

        var surface = MillingSimulation.ToSurface(f, maxTriangles: 200_000);

        Assert.True(surface.Faces.Count / 3 <= 200_000, $"{surface.Faces.Count / 3}");
        AssertClosed(surface);
        var box = surface.Bounds();
        Assert.Equal(0.05, box.Min.X, 6); // the outer cells stay
        Assert.Equal(149.95, box.Max.X, 6);
        Assert.Equal(99.95, box.Max.Y, 6);
    }

    [Fact]
    public void An_stl_file_gives_back_the_mesh()
    {
        var f = new HeightField(10, 20, 0.5, 30, 20, 4, -1);
        MillingSimulation.Cut(f, new MillingSimulation.Step(new(10, 25, 1), new(30, 25, 1), new SimTool(2, ToolProfile.BallNose), null!));
        var surface = MillingSimulation.ToSurface(f);
        var path = Path.Combine(Path.GetTempPath(), "bamcam-sim-" + Guid.NewGuid().ToString("N") + ".stl");
        try
        {
            BamCam.IO.Stl.StlWriter.Save(surface, path);
            Assert.Equal(84 + 50 * (surface.Faces.Count / 3), new FileInfo(path).Length);

            var back = BamCam.IO.Stl.StlReader.Load(path);

            Assert.Equal(surface.Faces.Count, back.Faces.Count);
            Assert.Equal(Volume(surface), Volume(back), 2);
            Assert.Equal(surface.Bounds().Max.Z, back.Bounds().Max.Z, 4);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ToolProfile.EndMill)]
    [InlineData(ToolProfile.BallNose)]
    [InlineData(ToolProfile.BullNose)]
    [InlineData(ToolProfile.VCutter)]
    public void The_tool_is_drawn_with_the_shape_it_cuts_with(ToolProfile profile)
    {
        var tool = new SimTool(3, profile, cornerRadius: 1, veeAngle: 90);

        var lines = BamCam.App.Controls.SimulationView.ToolOutline(tool, 20, (1, 0));

        // The profile across the view: at the tip the tool is at height 0, at its edge at the height of its shape.
        var profilePoints = lines.Select(l => l.From).Where(p => Math.Abs(p.Y) < 1e-9).ToList();
        Assert.Contains(profilePoints, p => Math.Abs(p.X) < 1e-9 && Math.Abs(p.Z) < 1e-9);
        foreach (var d in new[] { 0.0, 1, 1.5, 2.5, 3 })
        {
            var expected = tool.HeightAt(d);
            Assert.Contains(lines, l => Math.Abs(l.From.X - d) < 1e-9 && Math.Abs(l.From.Y) < 1e-9 && Math.Abs(l.From.Z - expected) < 1e-9
                                     || Math.Abs(l.To.X - d) < 1e-9 && Math.Abs(l.To.Y) < 1e-9 && Math.Abs(l.To.Z - expected) < 1e-9);
        }
        var heights = profilePoints.Select(p => p.Z).ToList();
        if (profile == ToolProfile.EndMill) Assert.True(heights.Where(z => z < 1).All(z => Math.Abs(z) < 1e-9));
        else Assert.True(tool.HeightAt(3) > 0.5, $"{profile}: {tool.HeightAt(3)}");
        Assert.True(lines.Max(l => Math.Max(l.From.Z, l.To.Z)) >= 20 - 1e-9); // the shank reaches up
    }
}
