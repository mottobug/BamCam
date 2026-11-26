// Demo 1: a simple command. Draw ▸ Regular Polygon (Demo)... asks two numbers, one after the other, and draws a
// polygon around the origin. Enough for one or two values; for more, see Demo 2.

using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace BamCam.Example;

internal static class Demo1_SimpleCommand
{
    public static void Register(IPluginHost host, bool german) =>
        host.AddCommand(new PluginCommand
        {
            Header = german ? "Regelmäßiges _Vieleck (Demo)..." : "Regular _Polygon (Demo)...",
            Menu = "Draw",                    // "Plugins" (default), "Draw", "Edit" or "Machining"
            Description = german ? "Fragt Eckenzahl und Radius ab (AskNumber)" : "Asks the number of sides and the radius (AskNumber)",
            Execute = ctx => Run(ctx, german),
        });

    private static async Task Run(ICommandContext ctx, bool german)
    {
        // AskNumber shows a small dialog; null when the user cancels.
        var sides = await ctx.AskNumber(german ? "Vieleck" : "Polygon", german ? "Anzahl Ecken:" : "Number of sides:", 6);
        if (sides is not { } n || n < 3) return;
        var radius = await ctx.AskNumber(german ? "Vieleck" : "Polygon", german ? "Umkreisradius:" : "Circumradius:", 20);
        if (radius is not { } r || r <= 0) return;

        var count = (int)Math.Round(n);
        var points = Enumerable.Range(0, count).Select(i => Point2.FromAngle(Math.PI / 2 + 2 * Math.PI * i / count, r));
        // AddEntities: onto the active layer, selected, as one undo step.
        ctx.AddEntities(german ? "Vieleck zeichnen" : "Draw polygon", [new PolylineEntity(Path2.FromPoints(points, closed: true))]);
        ctx.Log(german ? $"Vieleck mit {count} Ecken erzeugt." : $"Polygon with {count} sides created.");
    }
}
