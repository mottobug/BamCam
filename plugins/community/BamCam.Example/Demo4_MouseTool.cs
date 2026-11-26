// Demo 4: an interactive tool. Draw ▸ Star (Demo) (also a toolbar button) picks points with the mouse: the first click
// sets the centre, the second the outer radius; a preview follows the mouse. Up / Down change the number of points,
// Esc ends the tool.
//
// A command starts the tool with ctx.StartTool; the tool is a PluginTool, whose methods BamCam calls: Clicked with the
// snapped point, KeyPressed, Preview (outlines drawn while the mouse moves) and Prompt (the text above the drawing).

using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace BamCam.Example;

internal static class Demo4_MouseTool
{
    public static void Register(IPluginHost host, bool german) =>
        host.AddCommand(new PluginCommand
        {
            Header = german ? "_Stern (Demo, 2 Klicks)" : "_Star (Demo, 2 clicks)",
            Menu = "Draw",
            Description = german ? "Mauswerkzeug: Mittelpunkt und Außenradius klicken" : "Mouse tool: click the centre and the outer radius",
            // A toolbar button: SVG path data in a 24 x 24 box, drawn as lines.
            Icon = "M12,2 L14.6,9 L22,9 L16,13.6 L18.2,21 L12,16.6 L5.8,21 L8,13.6 L2,9 L9.4,9 Z",
            Execute = ctx =>
            {
                ctx.StartTool(new StarTool(german));
                return Task.CompletedTask;
            },
        });
}

internal sealed class StarTool(bool german) : PluginTool
{
    private Point2? _center;
    private int _points = 5;

    public override string Prompt => _center == null
        ? (german ? "Stern: Mittelpunkt klicken  [Pfeil hoch/runter = Zacken, Esc = Ende]" : "Star: click the centre  [Up / Down = points, Esc = end]")
        : (german ? $"Stern mit {_points} Zacken: Außenradius klicken" : $"Star with {_points} points: click the outer radius");

    public override void Clicked(IToolContext context, Point2 point, ToolModifiers modifiers)
    {
        if (_center is not { } c)
        {
            _center = point;
            context.SetPrompt(Prompt);
            return;
        }
        if (Point2.Distance(c, point) < 1e-9) return;
        context.AddEntities(german ? "Stern zeichnen" : "Draw star", [new PolylineEntity(Star(c, point))]);
        _center = null; // ready for the next star
        context.SetPrompt(Prompt);
    }

    public override bool KeyPressed(IToolContext context, string key)
    {
        if (key is not ("Up" or "Down")) return false; // not used here: BamCam handles it (Esc ends the tool)
        _points = Math.Clamp(_points + (key == "Up" ? 1 : -1), 3, 24);
        context.SetPrompt(Prompt);
        return true;
    }

    public override IEnumerable<Path2> Preview(IToolContext context) =>
        _center is { } c && Point2.Distance(c, context.Cursor) > 1e-9 ? [Star(c, context.Cursor)] : [];

    private Path2 Star(Point2 center, Point2 tip)
    {
        var r = Point2.Distance(center, tip);
        var start = (tip - center).Angle;
        var pts = Enumerable.Range(0, 2 * _points).Select(i =>
            center + Point2.FromAngle(start + i * Math.PI / _points, i % 2 == 0 ? r : r * 0.45));
        return Path2.FromPoints(pts, closed: true);
    }
}
