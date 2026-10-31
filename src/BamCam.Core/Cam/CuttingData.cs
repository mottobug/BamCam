using System.Text.Json;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

/// <summary>
/// Material entry of the speeds and feeds calculator: cutting speed in m/min and the chip load per tooth as a
/// fraction of the tool diameter (fz = <see cref="ChipLoadFactor"/> × D, a common rule for end mills).
/// </summary>
public sealed record CuttingMaterial(string Name, double CuttingSpeed, double ChipLoadFactor)
{
    public override string ToString() => Name;
}

/// <summary>Result of <see cref="CuttingData.Calculate"/>; feeds in drawing units per minute.</summary>
public sealed record CuttingResult(double SpindleSpeed, double CutFeedrate, double PlungeFeedrate, double ChipLoad, double CuttingSpeed);

/// <summary>Speeds and feeds: spindle speed from the cutting speed, feed from the chip load per tooth.</summary>
public static class CuttingData
{
    /// <summary>Starting values for carbide end mills on hobby and small CNC machines.</summary>
    public static IReadOnlyList<CuttingMaterial> BuiltIn { get; } =
    [
        new("Softwood", 400, 0.020),
        new("Hardwood", 300, 0.015),
        new("Plywood", 350, 0.016),
        new("MDF", 350, 0.018),
        new("Foam / Tooling Board", 400, 0.025),
        new("Acrylic (PMMA)", 250, 0.012),
        new("Plastics (POM, PE, PA)", 300, 0.015),
        new("Aluminium", 250, 0.008),
        new("Brass", 150, 0.006),
        new("Copper", 150, 0.006),
        new("Steel", 100, 0.005),
        new("Stainless Steel", 60, 0.004),
    ];

    /// <summary>
    /// The built-in table, extended (or overridden by name) by a JSON file
    /// <c>[{"Name": "...", "CuttingSpeed": 300, "ChipLoadFactor": 0.01}, ...]</c> when it exists.
    /// </summary>
    public static List<CuttingMaterial> Load(string? userFile)
    {
        var list = BuiltIn.ToList();
        if (userFile == null || !File.Exists(userFile)) return list;
        var own = JsonSerializer.Deserialize<List<CuttingMaterial>>(File.ReadAllText(userFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? [];
        foreach (var m in own.Where(m => !string.IsNullOrWhiteSpace(m.Name) && m.CuttingSpeed > 0 && m.ChipLoadFactor > 0))
        {
            var i = list.FindIndex(x => string.Equals(x.Name, m.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) list[i] = m;
            else list.Add(m);
        }
        return list;
    }

    /// <summary>Millimetres per drawing unit.</summary>
    public static double MillimetresPerUnit(DrawingUnits units) => units switch
    {
        DrawingUnits.Inches => 25.4,
        DrawingUnits.Centimeters => 10,
        DrawingUnits.Meters => 1000,
        DrawingUnits.Thousandths => 0.0254,
        _ => 1,
    };

    /// <summary>
    /// Spindle speed n = vc·1000 / (π·D), limited to <paramref name="minRpm"/> … <paramref name="maxRpm"/>
    /// (0 = no limit); feed = n · flutes · fz; plunge = feed · <paramref name="plungeFactor"/>.
    /// <paramref name="diameter"/>, <paramref name="chipLoad"/> and the resulting feeds are in drawing units.
    /// </summary>
    public static CuttingResult Calculate(double diameter, int flutes, double cuttingSpeed, double chipLoad,
        DrawingUnits units, double minRpm = 0, double maxRpm = 0, double plungeFactor = 0.3)
    {
        if (diameter <= 0) throw new ArgumentException("The tool diameter must be greater than 0.");
        var mm = MillimetresPerUnit(units);
        var rpm = cuttingSpeed * 1000 / (Math.PI * diameter * mm);
        if (maxRpm > 0) rpm = Math.Min(rpm, maxRpm);
        if (minRpm > 0) rpm = Math.Max(rpm, minRpm);
        rpm = Math.Round(rpm);
        var feed = rpm * Math.Max(flutes, 1) * chipLoad;
        return new CuttingResult(rpm, Math.Round(feed, 1), Math.Round(feed * Math.Clamp(plungeFactor, 0, 1), 1), chipLoad,
            Math.PI * diameter * mm * rpm / 1000);
    }
}
