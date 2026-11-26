// Demo 6: an object type computed from values. Draw ▸ Gear (Demo) inserts a gear outline that stays a gear: its
// values show in the property grid, and a double-click on it (in the drawing or the Drawing tab) opens a window with
// them. Drawings save the values with it, and as plain polylines too, so CamBam and BamCam without this plugin still
// show and machine the outline.
//
// - host.AddEntityType<GearEntity>(): the type; its ParametricType is stored in drawings (keep it unique).
// - Generate(): the outlines for the current values; BamCam calls it again after a value changed (Invalidate).
// - host.AddEntityEditor<GearEntity>(...): what a double-click opens. Here AskValues (Demo 2) on a copy of the values,
//   then ctx.Change to put them into the gear as one undo step.

using System.ComponentModel;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace BamCam.Example;

/// <summary>
/// A spur gear outline (trapezoid teeth, simplified) around the origin with a centre bore. The public properties with
/// get and set are its parameters.
/// </summary>
public sealed class GearEntity : ParametricEntity
{
    public override string ParametricType => "sample.gear";   // stored in drawings: never change it once used
    public override string TypeName => "Gear";

    [Category("Gear")] public int Teeth { get; set; } = 20;
    [Category("Gear"), Description("Tooth size: pitch diameter = module x teeth.")] public double Module { get; set; } = 2;
    [Category("Gear"), DisplayName("Bore Diameter")] public double Bore { get; set; } = 8;

    protected override IEnumerable<Path2>? Generate()
    {
        var teeth = Math.Clamp(Teeth, 6, 200);
        var m = Math.Max(Module, 0.01);
        var pitch = m * teeth / 2;
        var outer = pitch + m;
        var root = pitch - 1.25 * m;
        var points = new List<Point2>();
        var step = 2 * Math.PI / teeth;
        for (var i = 0; i < teeth; i++)
        {
            var a = i * step;
            // root, flank up, tip, flank down
            points.Add(Point2.FromAngle(a, root));
            points.Add(Point2.FromAngle(a + step * 0.15, root));
            points.Add(Point2.FromAngle(a + step * 0.3, outer));
            points.Add(Point2.FromAngle(a + step * 0.5, outer));
            points.Add(Point2.FromAngle(a + step * 0.65, root));
        }
        yield return Path2.FromPoints(points, closed: true);
        if (Bore > 0 && Bore / 2 < root)
            yield return new Path2([new Vertex(new Point3(Bore / 2, 0, 0), 1), new Vertex(new Point3(-Bore / 2, 0, 0), 1)], true);
    }
}

/// <summary>The values of a gear for the double-click window (AskValues shows every property of what it gets).</summary>
public sealed class GearValues
{
    [Category("Gear")] public int Teeth { get; set; }
    [Category("Gear"), Description("Tooth size: pitch diameter = module x teeth.")] public double Module { get; set; }
    [Category("Gear"), DisplayName("Bore Diameter")] public double Bore { get; set; }

    /// <summary>The outline for these values, for the preview: a gear that is not in the drawing.</summary>
    public IEnumerable<Path2> Outlines() => new GearEntity { Teeth = Teeth, Module = Module, Bore = Bore }.ToPaths();
}

internal static class Demo6_ParametricObject
{
    public static void Register(IPluginHost host, bool german)
    {
        host.AddEntityType<GearEntity>();
        host.AddCommand(new PluginCommand
        {
            Header = german ? "_Zahnrad (Demo)" : "_Gear (Demo)",
            Menu = "Draw",
            Description = german ? "Parametrisches Objekt: Werte im Eigenschaftenfenster oder per Doppelklick ändern" : "Parametric object: change its values in the properties or by double-click",
            Execute = ctx =>
            {
                ctx.AddEntities(german ? "Zahnrad zeichnen" : "Draw gear", [new GearEntity()]);
                return Task.CompletedTask;
            },
        });
        host.AddEntityEditor<GearEntity>(async (ctx, gear) =>
        {
            var values = new GearValues { Teeth = gear.Teeth, Module = gear.Module, Bore = gear.Bore };
            if (!await ctx.AskValues(german ? "Zahnrad" : "Gear", values, values.Outlines)) return;
            ctx.Change(german ? "Zahnrad ändern" : "Change gear", () =>
            {
                gear.Teeth = values.Teeth;
                gear.Module = values.Module;
                gear.Bore = values.Bore;
                gear.Invalidate();   // computes the outline again
            });
        });
    }
}
