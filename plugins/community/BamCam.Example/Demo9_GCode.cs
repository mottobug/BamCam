// Demo 9: G-code.
//
// - Machining ▸ G-code Statistics (Demo): ctx.PostGCode() returns the G-code of the whole drawing as text, with the
//   drawing's post processor, without writing a file. A plugin can read it, change it and save it itself (as a
//   post-post-processor such as the community plugin Wrap G-Code does).
// - A post processor macro: post processor templates can use {$sample.opcount}, here the number of enabled machining
//   operations (e.g. in the header: "(Operations: {$sample.opcount})"). Use your name as prefix of a macro.

using System.Globalization;
using BamCam.Plugins;

namespace BamCam.Example;

internal static class Demo9_GCode
{
    public static void Register(IPluginHost host, bool german)
    {
        host.AddCommand(new PluginCommand
        {
            Header = german ? "G-Code-_Statistik (Demo)" : "G-code _Statistics (Demo)",
            Menu = "Machining",
            Description = german ? "Liest den G-Code der Zeichnung (PostGCode)" : "Reads the G-code of the drawing (PostGCode)",
            Execute = ctx => Statistics(ctx, german),
        });
        host.AddPostMacro("sample.opcount", m =>
            m.Document.Parts.SelectMany(p => p.Operations).Count(o => o.Enabled).ToString(CultureInfo.InvariantCulture));
    }

    private static async Task Statistics(ICommandContext ctx, bool german)
    {
        var gcode = await ctx.PostGCode();   // toolpaths are generated when needed; null: nothing to post
        if (gcode == null)
        {
            ctx.Log(german ? "G-Code-Statistik: keine Bearbeitungen." : "G-code statistics: no machining operations.");
            return;
        }
        var lines = gcode.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        // Lines with a word such as G1 or G01 (G-codes are modal: a following line without one moves the same way).
        int Count(int g) => lines.Count(l => l.Split(' ').Any(w => w.Length >= 2 && char.ToUpperInvariant(w[0]) == 'G'
            && int.TryParse(w[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n == g));
        ctx.Log(german
            ? $"G-Code: {lines.Count} Zeilen; Zeilen mit G0: {Count(0)}, G1: {Count(1)}, G2/G3: {Count(2) + Count(3)}."
            : $"G-code: {lines.Count} lines; lines with G0: {Count(0)}, G1: {Count(1)}, G2/G3: {Count(2) + Count(3)}.");
    }
}
