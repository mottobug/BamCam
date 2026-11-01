using System.ComponentModel;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace BamCam.SamplePlugin;

/// <summary>
/// Entry point: registers a drawing command (regular polygon), an interactive tool (star by two clicks), a machining
/// operation (facing), a file format (XYZ point lists) and a post processor macro ({$sample.opcount}).
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
        host.AddCommand(new PluginCommand
        {
            Header = german ? "_Stern (2 Klicks)" : "_Star (2 clicks)",
            Menu = "Draw",
            Description = german ? "Mausbeispiel: Mittelpunkt und Außenradius klicken" : "Mouse tool example: click centre and outer radius",
            Icon = "M12,2 L14.6,9 L22,9 L16,13.6 L18.2,21 L12,16.6 L5.8,21 L8,13.6 L2,9 L9.4,9 Z", // toolbar button
            Execute = ctx =>
            {
                ctx.StartTool(new StarTool(german));
                return Task.CompletedTask;
            },
        });
        host.AddEntityType<GearEntity>();
        host.AddCommand(new PluginCommand
        {
            Header = german ? "_Zahnrad" : "_Gear",
            Menu = "Draw",
            Description = german ? "Parametrisches Objekt: Zähnezahl und Modul im Eigenschaftsfenster ändern" : "Parametric object: change teeth and module in the properties",
            Execute = ctx =>
            {
                ctx.AddEntities(german ? "Zahnrad zeichnen" : "Draw gear", [new GearEntity()]);
                return Task.CompletedTask;
            },
        });
        host.AddOperation<FacingOp>(FacingGenerator.Generate);
        var xyz = new FileFormat(german ? "XYZ-Punktliste" : "XYZ point list", ".xyz");
        host.AddImporter(xyz, XyzFormat.Read);
        host.AddExporter(xyz, XyzFormat.Write);
        // {$sample.opcount}: number of enabled machining operations, e.g. for a header comment.
        host.AddPostMacro("sample.opcount", m =>
            m.Document.Parts.SelectMany(p => p.Operations).Count(o => o.Enabled).ToString(System.Globalization.CultureInfo.InvariantCulture));
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

/// <summary>
/// Interactive tool: the first click sets the centre, the second the outer radius of a five-pointed star (the
/// preview follows the mouse). Up / Down change the number of points. Esc cancels.
/// </summary>
internal sealed class StarTool(bool german) : PluginTool
{
    private Point2? _center;
    private int _points = 5;

    public override string Prompt => _center == null
        ? (german ? "Stern: Mittelpunkt klicken  [Pfeil hoch/runter = Zacken, Esc = Abbruch]" : "Star: click the centre  [Up / Down = points, Esc = cancel]")
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
        if (key is not ("Up" or "Down")) return false;
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

/// <summary>XYZ point lists: one point per line, "x y" or "x y z", separated by spaces, tabs, commas or semicolons.</summary>
internal static class XyzFormat
{
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    public static CadDocument Read(string path)
    {
        var doc = new CadDocument();
        var list = new PointListEntity();
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || line.TrimStart().StartsWith('#')) continue;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, Inv, out var x) ||
                !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, Inv, out var y)) continue;
            var z = parts.Length > 2 && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, Inv, out var zz) ? zz : 0;
            list.Points.Add(new Point3(x, y, z));
        }
        doc.AddEntity(list);
        return doc;
    }

    /// <summary>Writes the points of all point lists and the vertices of all other objects.</summary>
    public static void Write(CadDocument doc, string path)
    {
        var lines = new List<string> { "# x y z" };
        foreach (var e in doc.AllEntities())
        {
            var points = e is PointListEntity pl ? pl.WorldPoints() : e.ToPaths().SelectMany(p => p.Vertices.Select(v => v.Point));
            lines.AddRange(points.Select(p => string.Format(Inv, "{0} {1} {2}", p.X, p.Y, p.Z)));
        }
        File.WriteAllLines(path, lines);
    }
}
/// <summary>
/// Parametric object: a spur gear outline (trapezoid teeth, simplified) around the origin with a centre bore.
/// Teeth, module and bore are edited in the property grid; the outline follows.
/// </summary>
public sealed class GearEntity : ParametricEntity
{
    public override string ParametricType => "sample.gear";
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