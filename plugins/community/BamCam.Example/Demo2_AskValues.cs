// Demo 2: many values in one window, without designing it. Draw ▸ Bolt Circle (Demo)... asks for a hole pattern.
//
// 1. A class with one property per value (BoltCircleValues below). Each public property with get and set becomes a
//    field: double and int a number, bool a check box, an enum a list, string a text. [DisplayName] is the label,
//    [Description] the help text, [Category] groups the fields.
// 2. One object of it in a static field: it keeps the values from one use to the next (for this session).
// 3. await ctx.AskValues(title, values, preview): shows the window and changes the object as the user types. True on
//    OK; on Cancel the object is put back as it was. The preview is optional: a function that returns the outlines for
//    the current values; BamCam draws them beside the fields after every change.
// 4. After OK, read the values from the object like any other properties.

using System.ComponentModel;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace BamCam.Example;

/// <summary>How each hole is drawn.</summary>
public enum HoleShape { Circle, Point }

/// <summary>The values of the bolt circle: one property per field of the window.</summary>
public sealed class BoltCircleValues
{
    [Category("Pattern"), DisplayName("Number of Holes"), Description("How many holes, evenly spaced on the pitch circle (2 or more).")]
    public int Holes { get; set; } = 6;

    [Category("Pattern"), DisplayName("Pitch Diameter"), Description("Diameter of the circle through the hole centres.")]
    public double PitchDiameter { get; set; } = 80;

    [Category("Pattern"), DisplayName("Start Angle"), Description("Angle of the first hole in degrees, counter-clockwise from +X.")]
    public double StartAngle { get; set; } = 90;

    [Category("Holes"), DisplayName("Hole Shape"), Description("Circle: a circle of the hole diameter. Point: a point list for drilling.")]
    public HoleShape Shape { get; set; } = HoleShape.Circle;

    [Category("Holes"), DisplayName("Hole Diameter"), Description("Diameter of each hole (for Circle).")]
    public double HoleDiameter { get; set; } = 8;

    [Category("Options"), DisplayName("Draw Pitch Circle"), Description("Also draw the pitch circle, e.g. as a construction line.")]
    public bool DrawPitchCircle { get; set; }

    [Category("Options"), DisplayName("Tag"), Description("Text stored with each new object (its Tag), e.g. a note for later.")]
    public string Tag { get; set; } = "";

    /// <summary>The hole centres for the current values.</summary>
    public List<Point2> Centres()
    {
        var count = Math.Max(Holes, 2);
        var start = StartAngle * Math.PI / 180;
        return Enumerable.Range(0, count).Select(i => Point2.FromAngle(start + 2 * Math.PI * i / count, PitchDiameter / 2)).ToList();
    }

    /// <summary>The outlines for the preview: each hole as a circle (a small cross for points), and the pitch circle.</summary>
    public IEnumerable<Path2> Outlines()
    {
        var r = Shape == HoleShape.Circle ? Math.Max(HoleDiameter, 0.01) / 2 : PitchDiameter / 40;
        foreach (var c in Centres())
        {
            if (Shape == HoleShape.Circle) yield return Circle(c, r);
            else
            {
                yield return Path2.FromPoints([c - new Point2(r, 0), c + new Point2(r, 0)], closed: false);
                yield return Path2.FromPoints([c - new Point2(0, r), c + new Point2(0, r)], closed: false);
            }
        }
        if (DrawPitchCircle) yield return Circle(new Point2(0, 0), PitchDiameter / 2);
    }

    // A full circle as two half arcs (bulge 1 = 180°).
    private static Path2 Circle(Point2 c, double r) =>
        new([new Vertex(new Point3(c.X + r, c.Y, 0), 1), new Vertex(new Point3(c.X - r, c.Y, 0), 1)], closed: true);
}

internal static class Demo2_AskValues
{
    // Kept for the session: the window opens with the values of the last use.
    private static readonly BoltCircleValues Last = new();

    public static void Register(IPluginHost host, bool german) =>
        host.AddCommand(new PluginCommand
        {
            Header = german ? "_Lochkreis (Demo)..." : "_Bolt Circle (Demo)...",
            Menu = "Draw",
            Description = german ? "Viele Werte in einem Fenster mit Vorschau (AskValues)" : "Many values in one window with a preview (AskValues)",
            Execute = ctx => Run(ctx, german),
        });

    private static async Task Run(ICommandContext ctx, bool german)
    {
        var values = Last;
        if (!await ctx.AskValues(german ? "Lochkreis" : "Bolt Circle", values, preview: values.Outlines)) return;   // Cancel

        // From here on the values are ordinary properties.
        if (values.Holes < 2 || values.PitchDiameter <= 0)
        {
            ctx.Log(german ? "Lochkreis: mindestens 2 Löcher und ein Teilkreis größer als 0." : "Bolt circle: at least 2 holes and a pitch diameter above 0.");
            return;
        }
        var entities = new List<Entity>();
        if (values.Shape == HoleShape.Circle)
            entities.AddRange(values.Centres().Select(c => new CircleEntity { Center = new Point3(c.X, c.Y, 0), Diameter = values.HoleDiameter }));
        else
        {
            var points = new PointListEntity();
            points.Points.AddRange(values.Centres().Select(c => new Point3(c.X, c.Y, 0)));
            entities.Add(points);
        }
        if (values.DrawPitchCircle) entities.Add(new CircleEntity { Diameter = values.PitchDiameter });
        if (values.Tag.Length > 0)
            foreach (var e in entities) e.Tag = values.Tag;
        ctx.AddEntities(german ? "Lochkreis zeichnen" : "Draw bolt circle", entities);
    }
}
