using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using BamCam.App.Controls;
using BamCam.App.Drawing;
using BamCam.Core.Geometry;
using BamCam.Core.Localization;
using BamCam.Core.Model;

namespace BamCam.App.Services;

public enum OutFilePrompt { IfExists, Always, Never }

/// <summary>Grid definition for one unit system.</summary>
public sealed class GridInfo
{
    public Point2 Minimum { get; set; }
    public Point2 Maximum { get; set; }
    [DisplayName("Major Scale")] public double MajorScale { get; set; }
    [DisplayName("Minor Scale")] public double MinorScale { get; set; }

    public override string ToString() => $"{MinorScale} / {MajorScale}";
}

/// <summary>
/// Application configuration (Tools ▸ Options), stored as JSON in the system folder.
/// Property categories are shown as groups in the options dialog.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    [Browsable(false)] public int Version { get; set; } = CurrentVersion;

    // ---- General ----
    [Category("General"), DisplayName("Language"), Choices(nameof(LanguageChoices)),
     Description("User interface language. Automatic = language of the operating system (English if there is no translation for it). Further languages: put a {code}.json file into the lang folder of the system folder.")]
    public string Language { get; set; } = "";

    /// <summary>Entries of the language list: automatic, then every available translation in its own name.</summary>
    public static IEnumerable<KeyValuePair<string, string>> LanguageChoices() =>
        new[] { new KeyValuePair<string, string>("", Tr.T("Automatic (system language)")) }
            .Concat(Tr.Available().Select(l => new KeyValuePair<string, string>(l.Code, l.Name)));

    // ---- Display ----
    [Category("Display"), DisplayName("Show Grid")] public bool ShowGrid { get; set; } = true;
    [Category("Display"), DisplayName("Show Axis")] public bool ShowAxis { get; set; } = true;
    [Category("Display"), DisplayName("Show Toolpaths")] public bool ShowToolpaths { get; set; } = true;
    [Category("Display"), DisplayName("Show Direction Arrows")] public bool ShowDirectionArrows { get; set; } = true;
    [Category("Display"), DisplayName("Show Nests")] public bool ShowNests { get; set; } = true;
    [Category("Display"), DisplayName("Select Fade"), Description("Fade unselected objects while something is selected.")]
    public bool SelectFade { get; set; } = true;
    [Category("Display"), DisplayName("Holding Tab Drag Toolpath Refresh"), Description("Regenerate the toolpath while a holding tab is dragged.")]
    public bool TabDragToolpathRefresh { get; set; }

    // ---- Colors ----
    [Category("Colors"), DisplayName("View Background Color")] public RgbColor ViewBackColor { get; set; } = new(50, 50, 60);
    [Category("Colors"), DisplayName("Grid Minor Color")] public RgbColor GridMinorColor { get; set; } = new(58, 58, 68);
    [Category("Colors"), DisplayName("Grid Major Color")] public RgbColor GridMajorColor { get; set; } = new(80, 80, 90);
    [Category("Colors"), DisplayName("Select Color")] public RgbColor SelectColor { get; set; } = new(255, 0, 0);
    [Category("Colors"), DisplayName("Toolpath Line Color")] public RgbColor ToolpathLineColor { get; set; } = new(35, 85, 205);
    [Category("Colors"), DisplayName("Toolpath Arc Color")] public RgbColor ToolpathArcColor { get; set; } = new(0, 220, 0);
    [Category("Colors"), DisplayName("Toolpath Rapid Color")] public RgbColor ToolpathRapidColor { get; set; } = new(255, 69, 0);
    [Category("Colors"), DisplayName("Cut Width Color")] public RgbColor CutWidthColor { get; set; } = new(0, 139, 139);
    [Category("Colors"), DisplayName("Default Layer Color")] public RgbColor DefaultLayerColor { get; set; } = RgbColor.DefaultLayer;

    // ---- Grid ----
    [Category("Grid"), DisplayName("Grid Info (Metric)")]
    public GridInfo GridMetric { get; set; } = new() { Minimum = new(-100, -100), Maximum = new(100, 100), MajorScale = 10, MinorScale = 1 };

    [Category("Grid"), DisplayName("Grid Info (Inches)")]
    public GridInfo GridInches { get; set; } = new() { Minimum = new(-4, -4), Maximum = new(4, 4), MajorScale = 1, MinorScale = 0.0625 };

    // ---- Drawing ----
    [Category("Drawing"), DisplayName("Drawing Units"), Description("Units for new drawings.")]
    public DrawingUnits DrawingUnits { get; set; } = DrawingUnits.Millimeters;
    [Category("Drawing"), DisplayName("Default Font Family"), Description("Font for text objects without a font.")]
    public string DefaultFontFamily { get; set; } = "Arial";
    [Category("Drawing"), DisplayName("Repeat Commands"), Description("Drawing commands restart after finishing an object until Esc.")]
    public bool RepeatCommands { get; set; } = true;
    [Category("Drawing"), DisplayName("Snap To Grid")] public bool SnapToGrid { get; set; } = true;
    [Category("Drawing"), DisplayName("Snap To Points")] public bool SnapToPoints { get; set; }

    // ---- CAM ----
    [Category("CAM"), DisplayName("Default Post Processor"), Description("Post processor used when a drawing does not name one.")]
    public string DefaultPostProcessor { get; set; } = "Default";
    [Category("CAM"), DisplayName("Default GCode Extension")] public string DefaultGCodeExtension { get; set; } = ".nc";
    [Category("CAM"), DisplayName("Out File Prompt"), Description("When to ask for the G-code file name: if the file exists, always, or never.")]
    public OutFilePrompt OutFilePrompt { get; set; } = OutFilePrompt.IfExists;
    [Category("CAM"), DisplayName("GCode Editor"), Description("External editor command for Edit Gcode. Empty = internal editor.")]
    public string GCodeEditor { get; set; } = "";

    // ---- Files ----
    [Category("Files"), DisplayName("File Backups"), Description("Number of name.b1 … name.bN backups kept when saving.")]
    public int FileBackups { get; set; } = 3;
    [Category("Files"), DisplayName("Recent Files Count")] public int MaxRecentFiles { get; set; } = 10;
    [Browsable(false)] public List<string> RecentFiles { get; set; } = [];

    // ---- Window (not shown in the dialog) ----
    [Browsable(false)] public double WindowWidth { get; set; } = 1366;
    [Browsable(false)] public double WindowHeight { get; set; } = 860;
    [Browsable(false)] public bool WindowMaximized { get; set; }
    [Browsable(false)] public double LeftColumnWidth { get; set; } = 310;
    [Browsable(false)] public double MessagesHeight { get; set; } = 110;

    // ------------------------------------------------------------------ persistence

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new Point2Converter(), new RgbColorConverter() },
    };

    /// <summary>Points as "x,y" (geometry structs have computed properties that must not be serialized).</summary>
    private sealed class Point2Converter : JsonConverter<Point2>
    {
        public override Point2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var parts = (reader.GetString() ?? "").Split(',');
            double P(int i) => i < parts.Length && double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            return new Point2(P(0), P(1));
        }

        public override void Write(Utf8JsonWriter writer, Point2 value, JsonSerializerOptions options) =>
            writer.WriteStringValue(FormattableString.Invariant($"{value.X},{value.Y}"));
    }

    /// <summary>Colours as "r,g,b", like in drawing files.</summary>
    private sealed class RgbColorConverter : JsonConverter<RgbColor>
    {
        public override RgbColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var parts = (reader.GetString() ?? "").Split(',');
            byte B(int i) => i < parts.Length && byte.TryParse(parts[i].Trim(), out var v) ? v : (byte)0;
            return new RgbColor(B(0), B(1), B(2));
        }

        public override void Write(Utf8JsonWriter writer, RgbColor value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    public static string DefaultPath => Path.Combine(SystemLibrary.SystemFolder, "settings.json");

    /// <summary>Loads settings; missing or unreadable files give defaults (the error is reported, not thrown).</summary>
    public static AppSettings Load(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
            s.Sanitize();
            return s;
        }
        catch (Exception ex)
        {
            error = Tr.F("Settings file '{0}' could not be read ({1}); using defaults.", path, ex.Message);
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;

    /// <summary>Repairs values that would break the UI (from hand-edited or older files).</summary>
    public void Sanitize()
    {
        GridMetric ??= new AppSettings().GridMetric;
        GridInches ??= new AppSettings().GridInches;
        foreach (var g in new[] { GridMetric, GridInches })
        {
            if (g.MinorScale <= 0) g.MinorScale = 1;
            if (g.MajorScale < g.MinorScale) g.MajorScale = g.MinorScale * 10;
        }
        RecentFiles ??= [];
        Language = (Language ?? "").Trim().ToLowerInvariant();
        MaxRecentFiles = Math.Clamp(MaxRecentFiles, 0, 50);
        FileBackups = Math.Clamp(FileBackups, 0, 20);
        if (WindowWidth < 400) WindowWidth = 1366;
        if (WindowHeight < 300) WindowHeight = 860;
        LeftColumnWidth = Math.Clamp(LeftColumnWidth, 150, 1000);
        MessagesHeight = Math.Clamp(MessagesHeight, 40, 800);
        if (string.IsNullOrWhiteSpace(DefaultGCodeExtension)) DefaultGCodeExtension = ".nc";
        if (!DefaultGCodeExtension.StartsWith('.')) DefaultGCodeExtension = "." + DefaultGCodeExtension;
    }

    public void AddRecentFile(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    // ------------------------------------------------------------------ application

    /// <summary>Copies display settings into the drawing view (colours, grid for the given units, toggles).</summary>
    public void ApplyTo(ViewSettings v, DrawingUnits units)
    {
        static Avalonia.Media.Color C(RgbColor c) => Avalonia.Media.Color.FromRgb(c.R, c.G, c.B);
        v.ShowGrid = ShowGrid;
        v.ShowAxis = ShowAxis;
        v.ShowToolpaths = ShowToolpaths;
        v.ShowDirectionArrows = ShowDirectionArrows;
        v.ShowNests = ShowNests;
        v.SelectFade = SelectFade;
        v.TabDragToolpathRefresh = TabDragToolpathRefresh;
        v.SnapToGrid = SnapToGrid;
        v.SnapToObjects = SnapToPoints;
        v.Background = C(ViewBackColor);
        v.GridMinorColor = C(GridMinorColor);
        v.GridMajorColor = C(GridMajorColor);
        v.SelectColor = C(SelectColor);
        v.ToolpathColor = C(ToolpathLineColor);
        v.ToolpathArcColor = C(ToolpathArcColor);
        v.RapidColor = C(ToolpathRapidColor);
        v.CutWidthColor = C(CutWidthColor);
        ApplyGrid(v, units);
    }

    public void ApplyGrid(ViewSettings v, DrawingUnits units)
    {
        var g = units == DrawingUnits.Inches ? GridInches : GridMetric;
        v.GridMinor = g.MinorScale;
        v.GridMajor = g.MajorScale;
        v.GridMin = g.Minimum;
        v.GridMax = g.Maximum;
    }

    /// <summary>Takes back the toggles the user changed through the View menu.</summary>
    public void CaptureFrom(ViewSettings v)
    {
        ShowGrid = v.ShowGrid;
        ShowAxis = v.ShowAxis;
        ShowToolpaths = v.ShowToolpaths;
        ShowDirectionArrows = v.ShowDirectionArrows;
        ShowNests = v.ShowNests;
        SnapToGrid = v.SnapToGrid;
        SnapToPoints = v.SnapToObjects;
    }
}
