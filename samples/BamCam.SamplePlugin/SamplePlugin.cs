using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace BamCam.SamplePlugin;

/// <summary>
/// Entry point: registers a drawing command (regular polygon) and a machining operation (facing).
/// </summary>
public sealed class SamplePlugin : IBamCamPlugin
{
    public void Initialize(IPluginHost host)
    {
        var german = host.Language == "de";
        host.AddCommand(new PluginCommand
        {
            Header = german ? "Regelmäßiges _Vieleck..." : "Regular _Polygon...",
            Menu = "Draw",
            Description = german ? "Vieleck um den Nullpunkt (Beispiel-Plugin)" : "Polygon around the origin (sample plugin)",
            Execute = ctx => DrawPolygon(ctx, german),
        });
        host.AddOperation<FacingOp>(FacingGenerator.Generate);
    }

    private static async Task DrawPolygon(ICommandContext ctx, bool german)
    {
        var sides = await ctx.AskNumber(german ? "Vieleck" : "Polygon", german ? "Anzahl Ecken:" : "Number of sides:", 6);
        if (sides is not { } n || n < 3) return;
        var radius = await ctx.AskNumber(german ? "Vieleck" : "Polygon", german ? "Umkreisradius:" : "Circumradius:", 20);
        if (radius is not { } r || r <= 0) return;
        var count = (int)Math.Round(n);
        var points = Enumerable.Range(0, count).Select(i => Point2.FromAngle(Math.PI / 2 + 2 * Math.PI * i / count, r));
        ctx.AddEntities(german ? "Vieleck zeichnen" : "Draw polygon", [new PolylineEntity(Path2.FromPoints(points, closed: true))]);
        ctx.Log(german ? $"Vieleck mit {count} Ecken erzeugt." : $"Polygon with {count} sides created.");
    }
}

/// <summary>
/// Facing (face milling): clears the bounding box of the selected objects in parallel rows, level by level from the
/// stock surface down to the target depth. Depth parameters come from <see cref="DepthOp"/>.
/// </summary>
public sealed class FacingOp : DepthOp
{
    /// <summary>Unique type name in drawing files (stored as &lt;mop xsi:type="MOPFacingSample"&gt;).</summary>
    public override string XmlName => "MOPFacingSample";
    public override string DisplayType => "Facing (sample)";

    [Param(60, "Facing")] public CamParam<double> StepOver { get; set; } = new(0.7);
    [Param(61, "Facing")] public CamParam<double> Margin { get; set; } = new(0);
}

internal static class FacingGenerator
{
    public static void Generate(ToolpathContext ctx, FacingOp op, Toolpath tp)
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
