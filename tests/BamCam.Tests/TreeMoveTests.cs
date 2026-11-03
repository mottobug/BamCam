using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

public class TreeMoveTests
{
    private static (CadDocument Doc, Part A, Part B) TwoParts()
    {
        var doc = new CadDocument();
        var a = doc.ActivePart;
        foreach (var n in new[] { "a1", "a2", "a3", "a4" }) a.Operations.Add(new ProfileOp { Name = n });
        var b = new Part { Name = "B" };
        doc.Parts.Add(b);
        foreach (var n in new[] { "b1", "b2" }) b.Operations.Add(new PocketOp { Name = n });
        return (doc, a, b);
    }

    private static string Names(Part p) => string.Join(",", p.Operations.Select(o => o.Name));

    [Fact]
    public void Operations_move_within_a_part_before_or_after_others()
    {
        var (doc, a, _) = TwoParts();
        doc.MoveOperations([a.Operations[0]], a, 3); // a1 after a3 (index 3 = before a4)
        Assert.Equal("a2,a3,a1,a4", Names(a));
        doc.MoveOperations([a.Operations[3]], a, 0); // a4 to the top
        Assert.Equal("a4,a2,a3,a1", Names(a));
        doc.MoveOperations([a.Operations[1], a.Operations[2]], a, 4); // a2, a3 to the end, in order
        Assert.Equal("a4,a1,a2,a3", Names(a));
    }

    [Fact]
    public void Operations_move_to_another_part()
    {
        var (doc, a, b) = TwoParts();
        doc.MoveOperations([a.Operations[1], a.Operations[3]], b, 1);
        Assert.Equal("a1,a3", Names(a));
        Assert.Equal("b1,a2,a4,b2", Names(b));
        doc.MoveOperations([b.Operations[0]], a, a.Operations.Count);
        Assert.Equal("a1,a3,b1", Names(a));
    }

    [Fact]
    public void Objects_move_to_another_layer()
    {
        var doc = new CadDocument();
        var circle = new CircleEntity { Diameter = 5 };
        doc.AddEntity(circle);
        var other = new Layer("Other");
        doc.Layers.Add(other);
        doc.MoveEntity(circle, other);
        Assert.Same(other, circle.Layer);
        Assert.Contains(circle, other.Entities);
        Assert.DoesNotContain(circle, doc.Layers[0].Entities);
        Assert.Same(circle, doc.FindEntity(circle.Id));
    }
}
