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
    public void V_bit_tool_follows_the_cone()
    {
        var v = new SimTool(5, ToolProfile.VCutter, veeAngle: 90);
        Assert.Equal(0, v.HeightAt(0), 9);
        Assert.Equal(2, v.HeightAt(2), 9);
        var bull = new SimTool(3, ToolProfile.BullNose, cornerRadius: 1);
        Assert.Equal(0, bull.HeightAt(1.5), 9);
        Assert.Equal(1, bull.HeightAt(3), 9);
    }
}
