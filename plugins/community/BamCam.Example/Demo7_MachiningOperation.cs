// Demo 7: a machining operation. Machining ▸ Facing (sample) clears the bounding box of the selected objects in
// parallel rows, level by level from the stock surface down to the target depth.
//
// - The operation is a class derived from MachineOp, or DepthOp for the depth values (stock surface, target depth,
//   depth increment). Its own values are [Param] properties of type CamParam<T>: they appear in the property grid, can
//   come from a CAM style, and are saved in drawings.
// - XmlName is the operation's type name in drawing files (<mop xsi:type="...">): unique, and never changed once used.
// - host.AddOperation registers it with the function that generates the toolpath.

using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Plugins;

namespace BamCam.Example;

public sealed class FacingOp : DepthOp
{
    public override string XmlName => "MOPFacingSample";
    public override string DisplayType => "Facing (sample)";

    [Param(60, "Facing")] public CamParam<double> StepOver { get; set; } = new(0.7);
    [Param(61, "Facing")] public CamParam<double> Margin { get; set; } = new(0);
}

internal static class Demo7_MachiningOperation
{
    public static void Register(IPluginHost host, bool german) => host.AddOperation<FacingOp>(Generate);

    /// <summary>The toolpath: rapid moves and cuts added to <paramref name="tp"/>; problems as warnings.</summary>
    private static void Generate(ToolpathContext ctx, FacingOp op, Toolpath tp)
    {
        var box = BoundingBox.Empty;
        foreach (var (_, path) in ctx.SourcePaths()) box = box.Union(path.Bounds());
        if (box.IsEmpty)
        {
            tp.Warnings.Add($"{op.Name}: no drawing objects.");
            return;
        }
        var r = ctx.ToolRadius;
        var step = Math.Max(0.05, Math.Min(1, op.StepOver.Value)) * ctx.ToolDiameter;
        var margin = op.Margin.Value;
        // Rows of the tool centre: outside the box at both ends, the first and last row reach the box edges.
        var x0 = box.Min.X - margin - r - 1;
        var x1 = box.Max.X + margin + r + 1;
        var y0 = box.Min.Y - margin;
        var y1 = box.Max.Y + margin;
        var rows = Math.Max(1, (int)Math.Ceiling((y1 - y0) / step));
        var clearance = ctx.Clearance;

        tp.Add(Move.Rapid(new Point3(x0, y0, clearance)));
        foreach (var z in ctx.DepthLevels(op))
        {
            tp.Add(Move.Rapid(new Point3(x0, y0, clearance)));
            tp.Add(Move.Line(new Point3(x0, y0, z), ctx.PlungeFeed));
            for (var k = 0; k <= rows; k++)
            {
                var y = y0 + (y1 - y0) * k / rows;
                var (from, to) = k % 2 == 0 ? (x0, x1) : (x1, x0);
                if (k > 0) tp.Add(Move.Line(new Point3(from, y, z), ctx.CutFeed));
                tp.Add(Move.Line(new Point3(to, y, z), ctx.CutFeed));
            }
            tp.Add(Move.Rapid(new Point3(rows % 2 == 0 ? x1 : x0, y1, clearance)));
        }
    }
}
