using BamCam.Core.Cam;
using BamCam.Core.Model;

namespace BamCam.Tests;

public class CuttingDataTests
{
    [Fact]
    public void Spindle_speed_and_feeds_from_cutting_speed_and_chip_load()
    {
        // 6 mm, 2 flutes, vc 400 m/min: n = 400000 / (π · 6) = 21221 rpm; feed = n · 2 · 0.12.
        var r = CuttingData.Calculate(6, 2, 400, 0.12, DrawingUnits.Millimeters, plungeFactor: 0.3);
        Assert.Equal(21221, r.SpindleSpeed);
        Assert.Equal(5093.0, r.CutFeedrate, 1);
        Assert.Equal(1527.9, r.PlungeFeedrate, 1);
        Assert.Equal(400, r.CuttingSpeed, 0);
    }

    [Fact]
    public void Spindle_speed_is_limited_to_the_machine_range()
    {
        var r = CuttingData.Calculate(6, 2, 400, 0.12, DrawingUnits.Millimeters, minRpm: 5000, maxRpm: 18000);
        Assert.Equal(18000, r.SpindleSpeed);
        Assert.Equal(4320, r.CutFeedrate, 1);
        Assert.Equal(Math.PI * 6 * 18000 / 1000, r.CuttingSpeed, 6);

        // A large tool in steel would run slower than the spindle can.
        Assert.Equal(5000, CuttingData.Calculate(20, 4, 100, 0.1, DrawingUnits.Millimeters, minRpm: 5000).SpindleSpeed);
    }

    [Fact]
    public void Inch_drawings_use_inch_diameter_and_feeds()
    {
        // 1/4" = 6.35 mm.
        var r = CuttingData.Calculate(0.25, 2, 400, 0.005, DrawingUnits.Inches);
        Assert.Equal(Math.Round(400000 / (Math.PI * 6.35)), r.SpindleSpeed);
        Assert.Equal(Math.Round(r.SpindleSpeed * 2 * 0.005, 1), r.CutFeedrate, 6);
    }

    [Fact]
    public void User_file_overrides_and_extends_the_material_table()
    {
        var file = Path.Combine(Path.GetTempPath(), $"bamcam-materials-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """
            // own values
            [
              { "Name": "aluminium", "CuttingSpeed": 180, "ChipLoadFactor": 0.006 },
              { "Name": "Carbon Fibre", "CuttingSpeed": 200, "ChipLoadFactor": 0.004 },
              { "Name": "", "CuttingSpeed": 1, "ChipLoadFactor": 1 },
            ]
            """);
        try
        {
            var list = CuttingData.Load(file);
            Assert.Equal(CuttingData.BuiltIn.Count + 1, list.Count);
            Assert.Equal(180, list.Single(m => m.Name.Equals("aluminium", StringComparison.OrdinalIgnoreCase)).CuttingSpeed);
            Assert.Contains(list, m => m.Name == "Carbon Fibre");
        }
        finally
        {
            File.Delete(file);
        }
        Assert.Equal(CuttingData.BuiltIn.Count, CuttingData.Load(file).Count);
    }
}
