//======================================================================
// WrapGCode: a plugin for BamCam to wrap a GCode file for a 4th axis
// Assumptions:
// (1) The Z origin is to be placed at the centre of rotation of the A-axis
// (2) The objects used to define the surface to be machined must be created
//     in the X,Y plane at a height (Z) to match their distance from the centre of rotation.    
// (3) The MOPs are then created normally, and then the GCode file is saved (unwrapped form)
// (4) The plugin is is then run< first choosing the unwrapped file from the file selector.
// (5) Three paramters values are then asked for:
//     (a) The rotation angle to match the minimum X value of the cut paths in the CGode file.
//         Typically 0.0, but other values are possible.
//     (b) The rotation angle to match the maximum X value of the cut paths in the GCode file.
//         Typically 360.0 (for a full circle, but can be less, but needs to be greater than the
//         minimum.
//     (c) Create the wrap for Clockwise, or AntiClockwise rotations.
// (6) The new wrapped GCode file is then generated with a name: "<origial name>_wrapped.nc"
//
// Copyright: MIT license, by Geoffrey G Roy (geoff@cadplan.com,au)
//========================================================================


using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BamCam.Plugins;

namespace BamCam.WrapGCodePlugin;

/// <summary>
/// Machining ▸ Wrap G-Code...: reads a prepared G-code (.nc) file, finds Xmin and Xmax, asks for the rotation angles
/// Rmin and Rmax (degrees) and writes "&lt;name&gt;_wrapped&lt;ext&gt;" in which every X word is replaced by a rotary A word:
///
///     d = Rmin + (x - Xmin) * (Rmax - Rmin) / (Xmax - Xmin)        X&lt;x&gt;  becomes  A&lt;d&gt;
///
/// Y, Z, comments and all other lines stay as they are.
/// </summary>
public sealed class WrapGCodePlugin : IBamCamPlugin
{
    
    public void Initialize(IPluginHost host)
    {
        var german = host.Language == "de";
        host.AddCommand(new PluginCommand
        {
            Header = german ? "G-Code auf A-Achse _wickeln..." : "_Wrap G-Code (X to A)...",
            Menu = "Plugins",
            Description = german
                ? "Liest eine G-Code-Datei und schreibt eine neue Datei, in der X-Werte durch A-Winkel ersetzt sind."
                : "Reads a G-code file and writes a new file in which the X values are replaced by A (rotary) angles.",
            Execute = ctx => Run(ctx, german),
        });
    }

    /// <summary>
    /// Shows an open file dialog on BamCam's main window. Returns the chosen path, "" when the user cancelled and
    /// null when there is no window to show it on. BamCam's Avalonia assemblies are shared with plugins.
    /// </summary>
    private static async Task<string?> PickFile(bool german)
    {
        return await Dispatcher.UIThread.InvokeAsync<string?>(async () =>
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
                return null;

            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = german ? "G-Code-Datei wählen" : "Select the G-code file to wrap",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(german ? "G-Code" : "G-code files") { Patterns = ["*.nc", "*.ngc", "*.tap", "*.gcode", "*.txt"] },
                    FilePickerFileTypes.All,
                ],
            });
            return files.Count == 0 ? "" : files[0].TryGetLocalPath() ?? "";
        });
    }

    private static async Task Run(ICommandContext ctx, bool german)
    {
        
        // (1) Read a prepared .nc file, chosen in an open file dialog.
        var inputPath = await PickFile(german);
        if (inputPath == null)
        {
            // No window to show the dialog on (should not happen in the desktop app): ask for the path instead.
            var typed = await ctx.AskText(
                "Wrap G-Code",
                german ? "Pfad der G-Code-Datei (.nc):" : "Path of the G-code file (.nc):");
            if (string.IsNullOrWhiteSpace(typed)) return;
            inputPath = typed.Trim().Trim('"');
        }
        if (inputPath.Length == 0) return; // dialog cancelled
        if (!File.Exists(inputPath))
        {
            ctx.Log(german ? $"Wrap G-Code: Datei nicht gefunden: {inputPath}" : $"Wrap G-Code: file not found: {inputPath}");
            return;
        }
        
        string text;
        try { text = File.ReadAllText(inputPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Log($"Wrap G-Code: {ex.Message}");
            return;
        }

        // (2) Xmin and Xmax of the file.
        if (!WrapGCode.TryFindXRange(ctx, text, out var xMin, out var xMax, out var valid, german))
        {
            ctx.Log(german ? "Wrap G-Code: Die Datei enthält keine X-Koordinaten." : "Wrap G-Code: the file contains no X coordinates.");
            return;

        }
        if (!valid) return;

        if (xMax == xMin)
        {
            ctx.Log(german
                ? $"Wrap G-Code: Xmin und Xmax sind gleich ({WrapGCode.Format(xMin)}); es gibt nichts zu wickeln."
                : $"Wrap G-Code: Xmin and Xmax are equal ({WrapGCode.Format(xMin)}); nothing to wrap.");
            return;
        }
        ctx.Log($"Wrap G-Code: Xmin = {WrapGCode.Format(xMin)}, Xmax = {WrapGCode.Format(xMax)}");

        // (3) Rotation angles.
        var rMin = await ctx.AskNumber("Wrap G-Code",
            (german ? "Rmin in Grad" : "Rmin in degrees") + $" (Xmin = {WrapGCode.Format(xMin)}):", 0);
        if (rMin == null) return;
        var rMax = await ctx.AskNumber("Wrap G-Code",
            (german ? "Rmax in Grad" : "Rmax in degrees") + $" (Xmax = {WrapGCode.Format(xMax)}):", 360);
        if (rMax == null) return;
        
        var rotateDir = await ctx.AskNumber("Wrap G-Code",
            (german ? "Drehrichtung: im UZS(0), GUZS(1)" : "Rotate direction: CW(0), ACW(1)"), 0);
        if (rotateDir == null) return;
        bool reverse = false;
        if (rotateDir == 1) reverse = true;

        if (rMax <= rMin) ctx.Log(german ? "WARNUNG: Inkonsistente Winkel": "WARNING: Inconsistent angles");
       
        // (4) Output name: the input name with "wrapped" added.
        var outputPath = WrapGCode.OutputPath(inputPath);

        // (5)-(7) Replace the X words, leave everything else alone.
        var wrapped = WrapGCode.Wrap(text, xMin, xMax, rMin.Value, rMax.Value, out var count, reverse);

        // (8) Save.
        try { File.WriteAllText(outputPath, wrapped, new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Log($"Wrap G-Code: {ex.Message}");
            return;
        }

        ctx.Log(german
            ? $"Wrap G-Code: {count} X-Werte ersetzt, gespeichert als {outputPath}"
            : $"Wrap G-Code: {count} X words replaced, saved as {outputPath}");
    }
}

/// <summary>The G-code processing, separate from the user interface so that it can be tested.</summary>
public static class WrapGCode
{
    // An X word: X not preceded by a letter, then a signed decimal number.
    private static readonly Regex XWord = new(@"(?<![A-Za-z])[Xx]\s*([-+]?(?:\d+\.?\d*|\.\d+))", RegexOptions.Compiled);

    // An ArcWord (G2/G3)
    private static readonly Regex ArcWord = new Regex(@"^G[23] ");

    /// <summary>The smallest and largest X value in the code (comments are ignored).</summary>
    public static bool TryFindXRange(ICommandContext ctx, string text, out double xMin, out double xMax, out bool valid, bool german)
    {
        xMin = double.MaxValue;
        xMax = double.MinValue;
        valid = true;

        var found = false;
        foreach (var line in text.Split('\n'))
        foreach (var (segment, isComment) in Split(line))
        {
            if (isComment) continue;

            if(ArcWord.IsMatch(segment) || ArcWord.IsMatch(segment))
                {
                    //found an Arc
                    ctx.Log(german ? "FEHLER in Datei, G2/G3 Code gefunden, Arcs entfernen und erneut versuchen" : "ERROR in file, G2/G3 code found, remove Arcs and try again");
                    valid = false;
                    return true;
                }
            foreach (Match m in XWord.Matches(segment))
            {
                var x = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                xMin = Math.Min(xMin, x);
                xMax = Math.Max(xMax, x);
                found = true;
            }
        }
        return found;
    }

    /// <summary>
    /// Replaces every X word by "A" + d, d = (x - xMin) * (rMax - rMin) / (xMax - xMin). Line endings and every other
    /// character of the text stay as they were.
    /// </summary>
    public static string Wrap(string text, double xMin, double xMax, double rMin, double rMax, out int count, bool reverse)
    {
        var replaced = 0;
        var lines = text.Split('\n'); // a trailing '\r' stays with its line
        int numLines = lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            var sb = new StringBuilder();
            foreach (var (segment, isComment) in Split(lines[i]))
            {
                if (isComment) { sb.Append(segment); continue; }
                sb.Append(XWord.Replace(segment, m =>
                {
                    var x = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    var d = rMin+ (x - xMin) * (rMax - rMin) / (xMax - xMin);
                    if(reverse)
                    {
                       d = rMax - (d - rMin);
                    }
                    replaced++;
                    return "A" + Format(d);
                }));
            }
            lines[i] = sb.ToString();
        }
        count = replaced;
       
        return string.Join('\n', lines);
    }

    /// <summary>part.nc becomes part_wrapped.nc, in the same folder.</summary>
    public static string OutputPath(string inputPath)
    {
        var folder = Path.GetDirectoryName(inputPath) ?? "";
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(inputPath) + "_wrapped" + Path.GetExtension(inputPath));
    }

    public static string Format(double value)
    {
        var s = value.ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>Splits a line into code and comment parts, so that "(X axis)" and "; X test" are never touched.</summary>
    private static List<(string Text, bool IsComment)> Split(string line)
    {
        var parts = new List<(string, bool)>();
        var code = new StringBuilder();
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == ';')
            {
                if (code.Length > 0) parts.Add((code.ToString(), false));
                parts.Add((line[i..], true));
                return parts;
            }
            if (c == '(')
            {
                if (code.Length > 0) { parts.Add((code.ToString(), false)); code.Clear(); }
                var end = line.IndexOf(')', i);
                if (end < 0) end = line.Length - 1;
                parts.Add((line.Substring(i, end - i + 1), true));
                i = end + 1;
                continue;
            }
            code.Append(c);
            i++;
        }
        if (code.Length > 0) parts.Add((code.ToString(), false));
        return parts;
    }
}
