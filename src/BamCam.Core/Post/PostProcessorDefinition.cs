using System.ComponentModel;

namespace BamCam.Core.Post;

public enum ArcOutputMode { Normal, ConvertToLines, HelixConvertToLines }

/// <summary>
/// A post processor: text templates with {$macro} expressions plus output options.
/// Property names match the element names of .cbpp files; unset properties keep these built-in defaults.
/// </summary>
public sealed class PostProcessorDefinition
{
    public string Name { get; set; } = "Default";

    // ---- Main sections ----
    [Category("(Main)")] public string PostFile { get; set; } = "{$header}\r\n{$mops}\r\n{$footer}";
    [Category("(Main)")] public string Header { get; set; } =
        "{$comment} {$cbfile.name} {$date} {$endcomment}\r\n" +
        "{$tooltable}\r\n" +
        "{$cbfile.header}\r\n" +
        "{$units} {$distancemode} {$velocitymode} {$cuttercomp(off)}\r\n" +
        "{$toolchange(first)}\r\n" +
        "{$clearance}";
    [Category("(Main)")] public string Footer { get; set; } = "{$clearance}\r\n{$spindle(off)}\r\n{$cbfile.footer}\r\n{$endrewind}";
    [Category("(Main)")] public string MOP { get; set; } =
        "{$comment} {$mop.name} {$endcomment}\r\n" +
        "{$toolchange}\r\n" +
        "{$velocitymode} {$workplane}\r\n" +
        "{$mop.header}\r\n" +
        "{$spindle} {$s}\r\n" +
        "{$blocks}\r\n" +
        "{$mop.footer}";
    [Category("(Main)")] public string StartCut { get; set; } = "";
    [Category("(Main)")] public string EndCut { get; set; } = "";
    [Category("(Main)")] public string PostProcessorMacros { get; set; } = "";

    // ---- Tools ----
    [Category("Tools")] public string ToolTableItem { get; set; } = "{$comment} T{$tool.index} : {$tool.diameter} {$endcomment}";
    [Category("Tools")] public string ToolChange { get; set; } =
        "{$clearance}\r\n{$comment} T{$tool.index} : {$tool.diameter} {$endcomment}\r\nT{$tool.index} M6";

    // ---- G codes ----
    [Category("G Codes")] public string G0 { get; set; } = "G0";
    [Category("G Codes")] public string G1 { get; set; } = "G1";
    [Category("G Codes")] public string G2 { get; set; } = "G2";
    [Category("G Codes")] public string G3 { get; set; } = "G3";
    [Category("G Codes")] public string G81 { get; set; } = "G81";
    [Category("G Codes")] public string G82 { get; set; } = "G82";
    [Category("G Codes")] public string G83 { get; set; } = "G83";
    [Category("G Codes")] public string ArcCenterAbsolute { get; set; } = "";
    [Category("G Codes")] public string ArcCenterIncremental { get; set; } = "";
    [Category("G Codes")] public string CannedCycleStart { get; set; } = "G98";
    [Category("G Codes")] public string CannedCycleEnd { get; set; } = "G80";
    [Category("G Codes")] public string CutterCompOff { get; set; } = "G40";
    [Category("G Codes")] public string CutterCompLeft { get; set; } = "G41";
    [Category("G Codes")] public string CutterCompRight { get; set; } = "G42";
    [Category("G Codes")] public string DistanceAbsolute { get; set; } = "G90";
    [Category("G Codes")] public string DistanceIncremental { get; set; } = "G91";
    [Category("G Codes")] public string UnitsInches { get; set; } = "G20";
    [Category("G Codes")] public string UnitsMetric { get; set; } = "G21";
    [Category("G Codes")] public string VelocityModeExactStop { get; set; } = "G61";
    [Category("G Codes")] public string VelocityModeConstantVelocity { get; set; } = "G64";
    [Category("G Codes")] public string WorkplaneXY { get; set; } = "G17";
    [Category("G Codes")] public string WorkplaneXZ { get; set; } = "G18";
    [Category("G Codes")] public string WorkplaneYZ { get; set; } = "G19";
    [Category("G Codes")] public string XModeDiameter { get; set; } = "G7";
    [Category("G Codes")] public string XModeRadius { get; set; } = "G8";

    // ---- M codes ----
    [Category("M Codes")] public string EndRewind { get; set; } = "M30";
    [Category("M Codes")] public string Repeat { get; set; } = "M47";
    [Category("M Codes")] public string SpindleCW { get; set; } = "M3";
    [Category("M Codes")] public string SpindleCCW { get; set; } = "M4";
    [Category("M Codes")] public string SpindleOff { get; set; } = "M5";
    [Category("M Codes")] public string Stop { get; set; } = "M0";

    // ---- Moves ----
    [Category("Moves")] public string Rapid { get; set; } = "{$g0} {$_f} {$_x} {$_y} {$_z} {$_a} {$_b} {$_c}";
    [Category("Moves")] public string FeedMove { get; set; } = "{$g1} {$_f} {$_x} {$_y} {$_z} {$_a} {$_b} {$_c}";
    [Category("Moves")] public string ArcCW { get; set; } = "{$g2} {$_f} {$_x} {$_y} {$_z} {$i} {$j}";
    [Category("Moves")] public string ArcCCW { get; set; } = "{$g3} {$_f} {$_x} {$_y} {$_z} {$i} {$j}";

    // ---- Canned cycles ----
    [Category("Canned Cycles")] public string Drill { get; set; } = "{$g81} {$_x} {$_y} {$_z} {$_r} {$_f}";
    [Category("Canned Cycles")] public string DrillDwell { get; set; } = "{$g82} {$_x} {$_y} {$_z} {$p} {$_r} {$_f}";
    [Category("Canned Cycles")] public string DrillPeck { get; set; } = "{$g83} {$_x} {$_y} {$_z} {$_q} {$_r} {$_f}";

    // ---- Line numbering ----
    [Category("Line Numbering")] public bool AddLineNumbers { get; set; }
    [Category("Line Numbering")] public string LineNumberFormat { get; set; } = "0";
    [Category("Line Numbering")] public int LineNumberIncrement { get; set; } = 10;
    [Category("Line Numbering")] public string LineNumberPrefix { get; set; } = "N";
    [Category("Line Numbering")] public string LineNumberSkip { get; set; } = "(%";
    [Category("Line Numbering")] public bool LineNumberSpaceAfter { get; set; } = true;
    [Category("Line Numbering")] public int LineNumberStart { get; set; } = 10;

    // ---- Options ----
    [Category("Options")] public ArcOutputMode ArcOutput { get; set; } = ArcOutputMode.Normal;
    [Category("Options")] public double ArcToLinesTolerance { get; set; } = 0.01;
    [Category("Options")] public string ClearancePlaneAxis { get; set; } = "Z";
    [Category("Options")] public string Comment { get; set; } = "(";
    [Category("Options")] public string EndComment { get; set; } = ")";
    [Category("Options")] public string EndOfLine { get; set; } = "\\r\\n";
    [Category("Options")] public bool InvertArcs { get; set; }
    /// <summary>Lathe X words as radius or diameter ({$lathexmode} prints XModeRadius / XModeDiameter).</summary>
    [Category("Lathe"), DisplayName("Lathe X Mode")] public LatheXMode LatheXMode { get; set; } = LatheXMode.Radius;
    /// <summary>Output the virtual tool point instead of the nose radius centre.</summary>
    [Category("Lathe"), DisplayName("Lathe Tool Radius Offset")] public bool LatheToolRadiusOffset { get; set; }
    /// <summary>
    /// Threading cycle template (empty = single passes with <see cref="ThreadPass"/>). Registers: Z thread end,
    /// P pitch, I peak offset from the drive line (radius, negative outside), J first cut, K full depth, R degression,
    /// Q infeed angle, H spring passes. Example (LinuxCNC): G76 {$_z} {$p} {$i} {$j} {$k} {$r} {$q} {$h}
    /// </summary>
    [Category("Lathe"), DisplayName("Thread Cycle")] public string ThreadCycle { get; set; } = "";
    /// <summary>One synchronised threading pass to Z (K = pitch), used when there is no thread cycle.</summary>
    [Category("Lathe"), DisplayName("Thread Pass")] public string ThreadPass { get; set; } = "G33 {$_z} {$k}";
    /// <summary>
    /// Constant cutting speed at the start of a turning operation with Surface Speed. Registers: S cutting speed
    /// (m/min, ft/min in inch drawings), D maximum spindle speed (only when the operation sets one).
    /// </summary>
    [Category("Lathe"), DisplayName("Constant Surface Speed")] public string ConstantSurfaceSpeed { get; set; } = "G96 {$s}";
    /// <summary>Spindle speed limit before G96 (Fanuc: G50 S…); empty when the limit is part of the G96 line. Register: S.</summary>
    [Category("Lathe"), DisplayName("Spindle Speed Limit")] public string SpindleSpeedLimit { get; set; } = "G50 {$s}";
    /// <summary>Back to constant spindle speed after the operation. Register: S spindle speed of the operation.</summary>
    [Category("Lathe"), DisplayName("Constant Spindle Speed")] public string ConstantSpindleSpeed { get; set; } = "G97 {$s}";
    // ---- Plasma (used by plasma operations only) ----
    [Category("Plasma"), DisplayName("Torch On"), Description("Switches the torch on at the pierce point.")]
    public string TorchOn { get; set; } = "M3";
    [Category("Plasma"), DisplayName("Torch Off"), Description("Switches the torch off at the end of a cut.")]
    public string TorchOff { get; set; } = "M5";
    [Category("Plasma"), DisplayName("Pierce Delay"), Description("Wait after Torch On. Register: P pierce delay in seconds.")]
    public string PierceDelay { get; set; } = "G4 {$p}";
    [Category("Plasma"), DisplayName("Torch Probe"),
     Description("Probing move down to the material until the torch switch triggers. Registers: Z lowest Z, F probe feedrate.")]
    public string TorchProbe { get; set; } = "G38.2 {$z} {$f}";
    [Category("Plasma"), DisplayName("Torch Probe Set Z"),
     Description("Sets the Z coordinate at the probed position (stock surface minus switch offset). Register: Z.")]
    public string TorchProbeSetZ { get; set; } = "G92 {$z}";
    [Category("Plasma"), DisplayName("Torch Height Control On"), Description("Switches the THC on when the torch has reached cut height (empty when the THC switches itself).")]
    public string ThcOn { get; set; } = "";
    [Category("Plasma"), DisplayName("Torch Height Control Off"), Description("Switches the THC off before the torch is switched off.")]
    public string ThcOff { get; set; } = "";

    [Category("Options")] public double MinimumArcLength { get; set; } = 1e-4;
    [Category("Options")] public double MaximumArcRadius { get; set; } = 1e6;
    [Category("Options")] public string NumberFormat { get; set; } = "0.0###";
    [Category("Options")] public bool RapidDownToClearance { get; set; } = true;
    [Category("Options")] public bool SuppressParserErrors { get; set; }
    [Category("Options")] public bool UpperCase { get; set; }

    // ---- Post build ----
    [Category("Post Build"), DisplayName("Post-Build Command"),
     Description("Program run after the G-code file has been written, e.g. to add line numbers. BamCam asks once before it runs a command.")]
    public string PostBuildCommand { get; set; } = "";
    [Category("Post Build"), DisplayName("Post-Build Command Args"),
     Description("Arguments of the post-build command: {$outfile} = the G-code file, {$cbfile.name} = the drawing name. Put file names in double quotes.")]
    public string PostBuildCommandArgs { get; set; } = "";

    /// <summary>Properties read from a file that this version does not model (name → raw text).</summary>
    [Browsable(false)]
    public Dictionary<string, string> Unknown { get; } = [];

    /// <summary>Names of properties explicitly present in the source file (written back on save).</summary>
    [Browsable(false)]
    public HashSet<string> Explicit { get; } = [];

    public string EndOfLineText => EndOfLine.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");

    /// <summary>Built-in posts shipped with BamCam (own templates, CamBam-compatible macro language).</summary>
    public static IEnumerable<PostProcessorDefinition> BuiltIn()
    {
        yield return new PostProcessorDefinition { Name = "Default" };
        yield return new PostProcessorDefinition
        {
            Name = "Mach3",
            Header = "{$comment} {$cbfile.name} {$date} {$endcomment}\r\n{$tooltable}\r\n{$cbfile.header}\r\n" +
                     "{$units} {$distancemode} {$velocitymode} {$cuttercomp(off)}\r\n{$arccentermode}\r\n{$toolchange(first)}\r\n{$clearance}",
            ArcCenterAbsolute = "G90.1",
            ArcCenterIncremental = "G91.1",
        };
        yield return new PostProcessorDefinition
        {
            Name = "LinuxCNC",
            Header = "%\r\n{$comment} {$cbfile.name} {$date} {$endcomment}\r\n{$tooltable}\r\n{$cbfile.header}\r\n" +
                     "{$units} {$distancemode} {$velocitymode} {$cuttercomp(off)}\r\n{$arccentermode}\r\n{$toolchange(first)}\r\n{$clearance}",
            Footer = "{$clearance}\r\n{$spindle(off)}\r\n{$cbfile.footer}\r\n{$endrewind}\r\n%",
            ArcCenterAbsolute = "G90.1",
            ArcCenterIncremental = "G91.1",
            NumberFormat = "0.0####",
        };
        yield return new PostProcessorDefinition
        {
            Name = "LinuxCNC Turn",
            Header = "%\r\n{$comment} {$cbfile.name} {$date} {$endcomment}\r\n{$tooltable}\r\n{$cbfile.header}\r\n" +
                     "{$units} {$distancemode} {$lathexmode} {$velocitymode} {$cuttercomp(off)}\r\n{$arccentermode}\r\n{$toolchange(first)}\r\n{$clearance}",
            Footer = "{$clearance}\r\n{$spindle(off)}\r\n{$cbfile.footer}\r\n{$endrewind}\r\n%",
            Rapid = "{$g0} {$_f} {$_x} {$_z}",
            FeedMove = "{$g1} {$_f} {$_x} {$_z}",
            ArcCW = "{$g2} {$_f} {$_x} {$_z} {$i} {$k}",
            ArcCCW = "{$g3} {$_f} {$_x} {$_z} {$i} {$k}",
            ArcCenterAbsolute = "G90.1",
            ArcCenterIncremental = "G91.1",
            ClearancePlaneAxis = "X",
            LatheXMode = LatheXMode.Diameter,
            ThreadCycle = "G76 {$_z} {$p} {$i} {$j} {$k} {$r} {$q} {$h}",
            // LinuxCNC limits the spindle speed with the D word of G96.
            ConstantSurfaceSpeed = "G96 {$d} {$s}",
            SpindleSpeedLimit = "",
            NumberFormat = "0.0####",
        };
        yield return new PostProcessorDefinition
        {
            Name = "GRBL",
            ToolChange = "",
            CannedCycleStart = "",
            CannedCycleEnd = "",
            DrillPeck = "",
            VelocityModeExactStop = "",
            VelocityModeConstantVelocity = "",
            NumberFormat = "0.0##",
        };
        yield return new PostProcessorDefinition
        {
            Name = "GRBL Plasma",
            Header = "{$comment} {$cbfile.name} {$date} {$endcomment}\r\n{$cbfile.header}\r\n{$units} {$distancemode} G17\r\n{$clearance}",
            // G92 from probing is cleared again at the end, so that the work coordinates are as before.
            Footer = "{$clearance}\r\n{$spindle(off)}\r\nG92.1\r\n{$cbfile.footer}\r\n{$endrewind}",
            // No spindle codes: the torch is switched per cut.
            MOP = "{$comment} {$mop.name} {$mop.cutdata} {$endcomment}\r\n{$mop.header}\r\n{$blocks}\r\n{$mop.footer}",
            ToolChange = "",
            ToolTableItem = "",
            CannedCycleStart = "",
            CannedCycleEnd = "",
            DrillPeck = "",
            VelocityModeExactStop = "",
            VelocityModeConstantVelocity = "",
            TorchOn = "M3 S1000",
            NumberFormat = "0.0##",
        };
    }

    public PostProcessorDefinition Clone()
    {
        var c = (PostProcessorDefinition)MemberwiseClone();
        typeof(PostProcessorDefinition).GetField("<Unknown>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(c, new Dictionary<string, string>(Unknown));
        typeof(PostProcessorDefinition).GetField("<Explicit>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(c, new HashSet<string>(Explicit));
        return c;
    }
}

/// <summary>How lathe X coordinates are written.</summary>
public enum LatheXMode { Radius, Diameter }
