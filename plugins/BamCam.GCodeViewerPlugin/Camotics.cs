using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BamCam.Core.Cam;
using BamCam.Core.Model;
using BamCam.Core.Simulation;
using BamCam.Plugins;

namespace BamCam.GCodeViewerPlugin;

/// <summary>
/// Machining ▸ Open in CAMotics: writes the G-code and a CAMotics project (.camotics: units, tool table, workpiece and
/// the G-code file) and starts CAMotics with it. CAMotics is a separate open source program (camotics.org) that
/// simulates 3-axis milling from the G-code; it has to be installed. Its project format is plain JSON; only that file is
/// written, nothing of CAMotics is used inside BamCam.
/// </summary>
public static class Camotics
{
    /// <summary>The CAMotics tool of an operation: number, shape, diameter and length in drawing units.</summary>
    public sealed record Tool(int Number, string Shape, double Diameter, double Length, string Description, double SnubDiameter = 0);

    /// <summary>
    /// The tools of the enabled milling operations, by tool number (the first operation with a number decides).
    /// CAMotics knows cylindrical, ball nose, conical and snub nose tools: a bull nose becomes a ball nose when its corner radius is
    /// at least half the tool radius, else an end mill; V-bits and drills become cones of their angle.
    /// </summary>
    public static List<Tool> Tools(CadDocument doc)
    {
        var tools = new List<Tool>();
        foreach (var part in doc.Parts.Where(p => p.Enabled))
        foreach (var op in part.Operations.Where(o => o.Enabled && MillingSimulation.IsMilling(o)))
        {
            var number = op.EffectiveToolNumber(doc);
            if (tools.Any(t => t.Number == number)) continue;
            var library = CamLibraries.FindTool(doc, part, number);
            var diameter = op is VEngraveOp v ? VBitDiameter(v) : op.EffectiveToolDiameter(doc);
            if (diameter <= 0) continue;
            var profile = op.ToolProfile.Value is ToolProfile.Unspecified ? library?.ToolProfile ?? ToolProfile.EndMill : op.ToolProfile.Value;
            if (op is VEngraveOp) profile = ToolProfile.VCutter;
            var unit = doc.Units == DrawingUnits.Inches ? 1 / 25.4 : 1;
            var length = library is { FluteLength: > 0 } ? library.FluteLength : library is { Length: > 0 } ? library.Length : Math.Max(4 * diameter, 10 * unit);
            string shape;
            var snub = 0.0;
            switch (profile)
            {
                case ToolProfile.BallNose:
                    shape = "ballnose";
                    break;
                case ToolProfile.BullNose:
                    var corner = op is Profile3DOp p3 ? p3.CornerRadius.Value : diameter / 4;
                    shape = corner >= diameter / 4 ? "ballnose" : "cylindrical";
                    break;
                case ToolProfile.VCutter or ToolProfile.Drill:
                {
                    // CAMotics derives the cone angle from diameter and length: the cone ends at the full diameter.
                    var angle = op is VEngraveOp ve ? ve.VAngle.Value
                        : profile == ToolProfile.Drill ? 118
                        : library is { VeeAngle: > 0 } ? library.VeeAngle : 90;
                    // A V-bit with a flat tip is a snub nose (a cone cut off at the tip diameter).
                    snub = op is VEngraveOp { TipDiameter.Value: > 0 } tipped ? Math.Min(tipped.TipDiameter.Value, diameter) : 0;
                    shape = snub > 0 ? "snubnose" : "conical";
                    length = (diameter - snub) / 2 / Math.Tan(Math.Clamp(angle, 1, 179) / 2 * Math.PI / 180);
                    break;
                }
                default:
                    shape = "cylindrical";
                    break;
            }
            tools.Add(new Tool(number, shape, diameter, length, library?.Name is { Length: > 0 } name ? name : op.Name, snub));
        }
        return tools;
    }

    /// <summary>A V-bit as wide as its cone at the deepest cut (as in BamCam's own simulation).</summary>
    private static double VBitDiameter(VEngraveOp v) =>
        2 * (v.TipDiameter.Value / 2 + Math.Abs(v.StockSurface.Value - v.TargetDepth.Value)
            * Math.Tan(Math.Clamp(v.VAngle.Value, 1, 179) / 2 * Math.PI / 180));

    /// <summary>
    /// The workpiece in G-code coordinates: the stock of the first enabled part with operations (else of the machining
    /// options) moved by the machining origin. Null when there is no stock, or nested copies: CAMotics then sizes the
    /// workpiece around the toolpaths itself.
    /// </summary>
    public static (double[] Min, double[] Max)? Workpiece(CadDocument doc)
    {
        var part = doc.Parts.FirstOrDefault(p => p.Enabled && p.Operations.Any(o => o.Enabled)) ?? doc.Parts.FirstOrDefault();
        if (part == null || part.Nesting.NestMethod != NestMethod.None && part.NestingItems.Count > 0) return null;
        var stock = part.Stock is { IsEmpty: false } ? part.Stock : doc.Machining.Stock;
        if (stock.IsEmpty) return null;
        var origin = part.MachiningOrigin.Point?.XY ?? doc.Machining.MachiningOrigin.Point?.XY ?? Core.Geometry.Point2.Zero;
        return ([stock.PMin.X - origin.X, stock.PMin.Y - origin.Y, stock.PMin.Z],
                [stock.PMax.X - origin.X, stock.PMax.Y - origin.Y, stock.PMax.Z]);
    }

    /// <summary>The CAMotics project (JSON) for the G-code file <paramref name="ncFile"/> next to it.</summary>
    public static string Project(CadDocument doc, string ncFile)
    {
        var units = doc.Units == DrawingUnits.Inches ? "imperial" : "metric";
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("units", units);
            w.WriteStartObject("tools");
            foreach (var t in Tools(doc))
            {
                w.WriteStartObject(t.Number.ToString(CultureInfo.InvariantCulture));
                w.WriteNumber("number", t.Number);
                w.WriteString("units", units);
                w.WriteString("shape", t.Shape);
                w.WriteNumber("length", Math.Round(t.Length, 6));
                w.WriteNumber("diameter", Math.Round(t.Diameter, 6));
                if (t.SnubDiameter > 0) w.WriteNumber("snub_diameter", Math.Round(t.SnubDiameter, 6));
                w.WriteString("description", t.Description);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartObject("workpiece");
            if (Workpiece(doc) is { } box)
            {
                w.WriteBoolean("automatic", false);
                w.WriteStartObject("bounds");
                w.WriteStartArray("min");
                foreach (var v in box.Min) w.WriteNumberValue(Math.Round(v, 6));
                w.WriteEndArray();
                w.WriteStartArray("max");
                foreach (var v in box.Max) w.WriteNumberValue(Math.Round(v, 6));
                w.WriteEndArray();
                w.WriteEndObject();
            }
            else
            {
                w.WriteBoolean("automatic", true);
                w.WriteNumber("margin", 5);
            }
            w.WriteEndObject();
            w.WriteStartArray("files");
            w.WriteStringValue(Path.GetFileName(ncFile));
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ------------------------------------------------------------------ the program

    /// <summary>File remembering the CAMotics program chosen by the user.</summary>
    public static string PathFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BamCam", "camotics-path.txt");

    /// <summary>Where CAMotics is installed by default.</summary>
    public static IEnumerable<string> DefaultLocations()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                if (Environment.GetFolderPath(folder) is { Length: > 0 } pf) yield return Path.Combine(pf, "CAMotics", "camotics.exe");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/CAMotics.app/Contents/MacOS/camotics";
            yield return "/Applications/CAMotics.app/Contents/MacOS/CAMotics";
        }
        else
        {
            yield return "/usr/bin/camotics";
            yield return "/usr/local/bin/camotics";
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, OperatingSystem.IsWindows() ? "camotics.exe" : "camotics");
    }

    /// <summary>The CAMotics program: the one chosen before, else the first default location that exists; null if none.</summary>
    public static string? FindProgram(string? pathFile = null)
    {
        pathFile ??= PathFile;
        try
        {
            if (File.Exists(pathFile) && File.ReadAllText(pathFile).Trim() is { Length: > 0 } chosen && File.Exists(chosen)) return chosen;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return DefaultLocations().FirstOrDefault(File.Exists);
    }

    /// <param name="start">Starts a program; tests replace it.</param>
    /// <param name="folder">Where the files go; by default <see cref="ViewerPlugin.TempFolder"/>.</param>
    /// <param name="pathFile">Where the chosen program is remembered; by default <see cref="PathFile"/>.</param>
    public static async Task Open(ICommandContext context, bool german, Action<ProcessStartInfo>? start = null, string? folder = null, string? pathFile = null)
    {
        pathFile ??= PathFile;
        var program = FindProgram(pathFile);
        if (program == null)
        {
            var typed = await context.AskText("CAMotics",
                german ? "CAMotics wurde nicht gefunden. Pfad zum Programm (camotics.exe), installieren von camotics.org:"
                       : "CAMotics was not found. Path to the program (camotics.exe), install it from camotics.org:");
            if (string.IsNullOrWhiteSpace(typed)) return;
            typed = typed.Trim().Trim('"');
            if (!File.Exists(typed))
            {
                context.Log(german ? $"CAMotics: {typed} gibt es nicht." : $"CAMotics: {typed} does not exist.");
                return;
            }
            program = typed;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pathFile)!);
                File.WriteAllText(pathFile, program);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var text = await context.PostGCode();
        if (text == null) return;
        start ??= info => Process.Start(info)?.Dispose();
        try
        {
            var nc = ViewerPlugin.WriteFile(text, context.Document.Name, folder ?? ViewerPlugin.TempFolder);
            var project = Path.ChangeExtension(nc, ".camotics");
            File.WriteAllText(project, Project(context.Document, nc));
            start(new ProcessStartInfo(program, [project]) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(project)! });
            var tools = Tools(context.Document);
            context.Log(german
                ? $"CAMotics: {project} geöffnet ({tools.Count} Werkzeug(e): {string.Join(", ", tools.Select(t => $"T{t.Number} {t.Shape} Ø{t.Diameter:0.###}"))})."
                : $"CAMotics: opened {project} ({tools.Count} tool(s): {string.Join(", ", tools.Select(t => $"T{t.Number} {t.Shape} Ø{t.Diameter:0.###}"))}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            context.Log(german ? $"CAMotics konnte nicht gestartet werden: {ex.Message}" : $"Could not start CAMotics: {ex.Message}");
        }
    }
}
