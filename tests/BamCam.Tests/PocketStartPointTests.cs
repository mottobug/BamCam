using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

/// <summary>Discussion 31: with a start point the pocket nearest to it is cut first, then always the nearest next one.</summary>
[Collection(GlobalLibrariesCollection.Name)]
public class PocketStartPointTests
{
    private static (CadDocument Doc, PocketOp Op) ThreePockets()
    {
        var doc = new CadDocument();
        var op = new PocketOp { Name = "Pocket1" };
        // Left, middle, right: 20 x 20 squares at x = 0, 40 and 80.
        foreach (var x in new[] { 0.0, 40, 80 })
        {
            var e = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(x, 0), new(x + 20, 0), new(x + 20, 20), new(x, 20)], closed: true)));
            op.PrimitiveIds.Add(e.Id);
        }
        op.ToolDiameter.Set(6);
        op.TargetDepth.Set(-1);
        op.DepthIncrement.Set(1);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    /// <summary>Order of the pockets by the X of the first cutting move in each.</summary>
    private static List<int> PocketOrder(Toolpath tp)
    {
        var order = new List<int>();
        foreach (var m in tp.Moves.Where(m => m.Kind != MoveKind.Rapid && m.To.Z < -0.5))
        {
            var pocket = (int)Math.Floor(m.To.X / 40);
            if (order.Count == 0 || order[^1] != pocket) order.Add(pocket);
        }
        return order;
    }

    [Fact]
    public void Without_a_start_point_from_left_to_right()
    {
        var (doc, op) = ThreePockets();
        Assert.Equal([0, 1, 2], PocketOrder(ToolpathGenerator.Generate(doc, op)));
    }

    [Fact]
    public void The_pocket_nearest_to_the_start_point_comes_first()
    {
        var (doc, op) = ThreePockets();
        op.StartPoint.Set(new OptionalPoint(new Point3(100, 10, 0)));
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Equal([2, 1, 0], PocketOrder(tp));

        // Starting in the middle: the middle pocket first, then the nearer of the other two.
        op.StartPoint.Set(new OptionalPoint(new Point3(55, 10, 0)));
        Assert.Equal(1, PocketOrder(ToolpathGenerator.Generate(doc, op))[0]);
    }
}
