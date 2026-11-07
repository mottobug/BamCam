using System.ComponentModel;

namespace BamCam.App.Services;

/// <summary>A script file or a folder of them, for the Scripts menu.</summary>
public sealed record ScriptEntry(
    [property: Browsable(false)] string Name,
    [property: Browsable(false)] string Path,
    [property: Browsable(false)] IReadOnlyList<ScriptEntry>? Children);

/// <summary>The scripts of the Scripts menu: .csx files in the "scripts" folder of the system folder.</summary>
public static class ScriptLibrary
{
    public const string Extension = ".csx";

    public static string Folder => Path.Combine(SystemLibrary.SystemFolder, "scripts");

    /// <summary>Folders first, then scripts, each by name. Folders without scripts are left out.</summary>
    public static IReadOnlyList<ScriptEntry> List(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var folders = Directory.GetDirectories(folder)
            .Select(d => new ScriptEntry(Path.GetFileName(d), d, List(d)))
            .Where(e => e.Children!.Count > 0)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
        var files = Directory.GetFiles(folder, "*" + Extension)
            .Select(f => new ScriptEntry(Path.GetFileNameWithoutExtension(f), f, null))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
        return [.. folders, .. files];
    }

    /// <summary>Writes the examples when the folder does not exist yet, so that deleted examples stay deleted.</summary>
    public static void EnsureExamples(string folder)
    {
        if (Directory.Exists(folder)) return;
        var examples = Path.Combine(folder, "Examples");
        Directory.CreateDirectory(examples);
        foreach (var (name, text) in Examples) File.WriteAllText(Path.Combine(examples, name + Extension), text);
    }

    /// <summary>Copies a script file into the folder; false when one of that name exists and may not be replaced.</summary>
    public static bool Add(string source, string folder, bool overwrite, out string target)
    {
        target = Path.Combine(folder, Path.GetFileName(source));
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return true;
        if (File.Exists(target) && !overwrite) return false;
        Directory.CreateDirectory(folder);
        File.Copy(source, target, overwrite);
        return true;
    }

    /// <summary>Text of a new script.</summary>
    public const string NewScript =
        """
        // C# script of the Scripts menu. Lengths in drawing units, angles in degrees.
        // Add(...) adds outlines: Circle(x, y, r), Rect(x, y, w, h), Line(x1, y1, x2, y2), Arc(x, y, r, start, sweep).
        // Also: Document, Selection, Change("name", () => ...), Select(...), Log("text"), await AskNumber("title", "label", 10).
        var radius = await AskNumber("New script", "Radius:", 10);
        if (radius is { } r) Add(Circle(0, 0, r));
        """;

    /// <summary>Example scripts (name, text).</summary>
    public static readonly IReadOnlyList<(string Name, string Text)> Examples =
    [
        ("Bolt circle",
            """
            // Asks for the values and draws a circle of holes around the origin.
            var holes = await AskNumber("Bolt circle", "Number of holes:", 6);
            var pitch = await AskNumber("Bolt circle", "Pitch circle radius:", 30);
            var radius = await AskNumber("Bolt circle", "Hole radius:", 3);
            if (holes is not { } n || pitch is not { } p || radius is not { } r) return;
            for (var i = 0; i < (int)n; i++)
            {
                var a = i * 2 * Math.PI / (int)n;
                Add(Circle(p * Math.Cos(a), p * Math.Sin(a), r));
            }
            """),
        ("Rectangle around selection",
            """
            // Draws a rectangle around the selected objects, with a margin.
            var selected = Selection;
            if (selected.Count == 0)
            {
                Log("Select drawing objects first.");
                return;
            }
            var margin = await AskNumber("Rectangle around selection", "Margin:", 5);
            if (margin is not { } m) return;
            var box = selected.Select(e => e.Bounds()).Aggregate((a, b) => a.Union(b));
            Add(Rect(box.Min.X - m, box.Min.Y - m, box.Width + 2 * m, box.Height + 2 * m));
            """),
        ("List operations",
            """
            // Writes the parts and machining operations of the drawing to the message window.
            foreach (var part in Document.Parts)
            {
                Log(part.Name);
                foreach (var op in part.Operations)
                    Log($"  {op.Name}: {op.DisplayType}, {(op.Enabled ? "enabled" : "disabled")}");
            }
            """),
        ("Squircle", Resource("Squircle.csx")),
    ];

    private static string Resource(string name)
    {
        using var reader = new StreamReader(typeof(ScriptLibrary).Assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }
}
