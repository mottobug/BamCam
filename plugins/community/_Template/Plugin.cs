using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace Author.PluginName;

/// <summary>
/// Template for a community plugin: one command in the Draw menu that draws a square around the origin. Replace it with
/// your own; docs/plugins.md describes everything a plugin can add (commands, drawing tools, machining operations, file
/// formats, post processor macros).
/// </summary>
public sealed class Plugin : IBamCamPlugin
{
    public void Initialize(IPluginHost host)
    {
        var german = host.Language == "de";
        host.AddCommand(new PluginCommand
        {
            Header = german ? "_Quadrat (Vorlage)..." : "_Square (Template)...",
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
