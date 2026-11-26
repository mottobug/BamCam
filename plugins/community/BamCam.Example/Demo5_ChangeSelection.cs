// Demo 5: changing the selected objects. Edit ▸ Raise Selection (Demo) and Lower Selection (Demo) move the selected
// objects up or down in Z by a step.
//
// - RequiresSelection = true: BamCam asks to select objects first instead of running the command with none.
// - ctx.Selection: the selected objects. Change them only inside ctx.Change: that makes one undo step and redraws.
// - Both commands share one toolbar button (ToolbarGroup) with a small arrow that opens a menu of them.

using BamCam.Core.Geometry;
using BamCam.Plugins;

namespace BamCam.Example;

internal static class Demo5_ChangeSelection
{
    private static double _step = 1;

    public static void Register(IPluginHost host, bool german)
    {
        host.AddCommand(new PluginCommand
        {
            Header = german ? "Auswahl _anheben (Demo)..." : "_Raise Selection (Demo)...",
            Menu = "Edit",
            Description = german ? "Verschiebt die Auswahl in Z nach oben" : "Moves the selection up in Z",
            RequiresSelection = true,
            Icon = "M12,3 L12,21 M6,9 L12,3 L18,9",   // an arrow up
            ToolbarGroup = "demo.z",
            Execute = ctx => MoveZ(ctx, german, up: true),
        });
        host.AddCommand(new PluginCommand
        {
            Header = german ? "Auswahl _absenken (Demo)..." : "_Lower Selection (Demo)...",
            Menu = "Edit",
            Description = german ? "Verschiebt die Auswahl in Z nach unten" : "Moves the selection down in Z",
            RequiresSelection = true,
            ToolbarGroup = "demo.z",                  // same group: in the menu of the button above
            Execute = ctx => MoveZ(ctx, german, up: false),
        });
    }

    private static async Task MoveZ(ICommandContext ctx, bool german, bool up)
    {
        var step = await ctx.AskNumber(german ? "Z verschieben" : "Move in Z", german ? "Schritt:" : "Step:", _step);
        if (step is not { } s || s <= 0) return;
        _step = s;
        var selected = ctx.Selection.ToList();
        var dz = up ? s : -s;
        ctx.Change(german ? "Auswahl in Z verschieben" : "Move selection in Z", () =>
        {
            foreach (var entity in selected) entity.ApplyTransform(Matrix4.Translation(0, 0, dz));
        });
        ctx.Log(german ? $"{selected.Count} Objekt(e) um {dz} in Z verschoben." : $"{selected.Count} object(s) moved by {dz} in Z.");
    }
}
