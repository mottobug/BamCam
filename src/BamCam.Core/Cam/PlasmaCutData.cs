using System.ComponentModel;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

/// <summary>
/// One row of a plasma cut data table: the settings for a material, thickness and current. A plasma operation
/// linked to it by <see cref="PlasmaOp.CutData"/> takes these values for every parameter left at its default.
/// </summary>
public sealed class PlasmaCutData
{
    [Category("Cut Data"), Description("Name the plasma operations refer to (Cut Data), e.g. Mild steel 3 mm 45 A.")]
    public string Name { get; set; } = "";

    [Category("Cut Data")] public string Material { get; set; } = "";

    [Category("Cut Data"), Description("Material thickness (for reference).")]
    public double Thickness { get; set; }

    [Category("Cut Data"), Description("Current to set on the plasma cutter, in ampere (for reference and the G-code comment).")]
    public double Current { get; set; }

    [Category("Cut Data"), Description("Nozzle / consumables (for reference).")]
    public string Nozzle { get; set; } = "";

    [Category("Values"), DisplayName("Kerf Width")] public double KerfWidth { get; set; }
    [Category("Values"), DisplayName("Cut Feedrate")] public double CutFeedrate { get; set; }
    [Category("Values"), DisplayName("Pierce Height")] public double PierceHeight { get; set; }
    [Category("Values"), DisplayName("Pierce Delay")] public double PierceDelay { get; set; }
    [Category("Values"), DisplayName("Cut Height")] public double CutHeight { get; set; }

    [Category("Cut Data")] public string Comment { get; set; } = "";

    public override string ToString() => Name.Length > 0 ? Name : $"{Material} {Thickness:0.###}";

    public PlasmaCutData Clone() => (PlasmaCutData)MemberwiseClone();

    /// <summary>
    /// Writes the values into the parameters of <paramref name="op"/> that are in the Default state (values > 0
    /// only). Returns the names of the parameters that changed.
    /// </summary>
    public List<string> ApplyTo(PlasmaOp op)
    {
        var changed = new List<string>();
        void Set(CamParam<double> p, double value, string name)
        {
            if (p.State != ParamState.Default || value <= 0 || p.Value == value) return;
            p.Value = value;
            p.RawXml = null;
            changed.Add(name);
        }
        Set(op.KerfWidth, KerfWidth, nameof(op.KerfWidth));
        Set(op.CutFeedrate, CutFeedrate, nameof(op.CutFeedrate));
        Set(op.PierceHeight, PierceHeight, nameof(op.PierceHeight));
        Set(op.PierceDelay, PierceDelay, nameof(op.PierceDelay));
        Set(op.CutHeight, CutHeight, nameof(op.CutHeight));
        return changed;
    }
}

/// <summary>A plasma cut data table (one file in the system folder's plasma folder).</summary>
public sealed class PlasmaCutTable
{
    public string Name { get; set; } = "";
    public string? SourcePath { get; set; }
    public DrawingUnits Units { get; set; } = DrawingUnits.Millimeters;
    public List<PlasmaCutData> Entries { get; } = [];

    public PlasmaCutData? Find(string name) =>
        Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static string TemplateName(DrawingUnits units) =>
        units == DrawingUnits.Inches ? "BamCam-Plasma-in" : "BamCam-Plasma-mm";

    /// <summary>
    /// Starting values for 30 to 45 A air plasma cutters, to be checked against the cut chart of the machine.
    /// </summary>
    public static PlasmaCutTable Template(bool inches)
    {
        var table = new PlasmaCutTable { Name = TemplateName(inches ? DrawingUnits.Inches : DrawingUnits.Millimeters), Units = inches ? DrawingUnits.Inches : DrawingUnits.Millimeters };
        const string note = "Starting values: check the cut chart of your plasma cutter.";
        void Add(string material, double mm, double inch, double amps, double kerfMm, double feedMm, double pierceMm, double delay)
        {
            var thickness = inches ? inch : mm;
            var thicknessText = inches ? FormattableString.Invariant($"{inch:0.###} in") : FormattableString.Invariant($"{mm:0.#} mm");
            table.Entries.Add(new PlasmaCutData
            {
                Name = FormattableString.Invariant($"{material} {thicknessText} {amps:0} A"), Material = material, Thickness = thickness, Current = amps,
                KerfWidth = inches ? Math.Round(kerfMm / 25.4, 3) : kerfMm,
                CutFeedrate = inches ? Math.Round(feedMm / 25.4) : feedMm,
                PierceHeight = inches ? Math.Round(pierceMm / 25.4, 2) : pierceMm,
                PierceDelay = delay,
                CutHeight = inches ? 0.06 : 1.5,
                Comment = note,
            });
        }
        Add("Mild steel", 1.5, 0.06, 30, 1.2, 4000, 3.8, 0.1);
        Add("Mild steel", 3, 0.125, 45, 1.5, 3000, 3.8, 0.3);
        Add("Mild steel", 6, 0.25, 45, 1.7, 1400, 3.8, 0.5);
        Add("Mild steel", 10, 0.375, 45, 1.9, 700, 5.0, 0.8);
        Add("Stainless steel", 3, 0.125, 45, 1.5, 2700, 3.8, 0.3);
        Add("Aluminium", 3, 0.125, 45, 1.6, 3800, 3.8, 0.2);
        return table;
    }
}
