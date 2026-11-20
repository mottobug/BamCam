using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace BamCam.Example;

/// <summary>
/// Example community plugin: Draw ▸ Square (Example) draws a square of a given side length around the origin, as one
/// undoable step. It shows the parts of a plugin; docs/plugins.md describes everything a plugin can add (commands,
/// drawing tools, machining operations, file formats, post processor macros).
/// </summary>
public sealed class Plugin : IBamCamPlugin
{
    public void Initialize(IPluginHost host)
    {
        var german = host.Language == "de";
        host.AddCommand(new PluginCommand
        {
            Header = german ? "_Quadrat (Beispiel)..." : "_Square (Example)...",
            Menu = "Draw",
            Description = german ? "Zeichnet ein Quadrat um den Nullpunkt." : "Draws a square around the origin.",
            Execute = async ctx =>
            {
                var size = await ctx.AskNumber(german ? "Quadrat" : "Square", german ? "Seitenlänge:" : "Side length:", 10);
                if (size is not > 0) return;
                var h = size.Value / 2;
                ctx.AddEntities(german ? "Quadrat zeichnen" : "Draw square",
                    [new PolylineEntity(Path2.FromPoints([new(-h, -h), new(h, -h), new(h, h), new(-h, h)], closed: true))]);
            },
        });
    }
}
