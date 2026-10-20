using BamCam.App.Drawing;
using BamCam.App.Services;
using BamCam.Core.Model;

namespace BamCam.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamcam-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Settings_survive_save_and_load()
    {
        var s = new AppSettings
        {
            DefaultPostProcessor = "Mach3",
            DrawingUnits = DrawingUnits.Inches,
            ViewBackColor = new RgbColor(1, 2, 3),
            RepeatCommands = false,
            OutFilePrompt = OutFilePrompt.Never,
            FileBackups = 5,
            GCodeEditor = "notepad",
            WindowWidth = 1500,
            LeftColumnWidth = 280,
        };
        s.GridMetric.MinorScale = 0.5;
        s.GridMetric.Maximum = new(250, 120);
        s.AddRecentFile(@"C:\work\a.cb");
        s.Save(FilePath);

        var back = AppSettings.Load(FilePath, out var error);
        Assert.Null(error);
        Assert.Equal("Mach3", back.DefaultPostProcessor);
        Assert.Equal(DrawingUnits.Inches, back.DrawingUnits);
        Assert.Equal(new RgbColor(1, 2, 3), back.ViewBackColor);
        Assert.False(back.RepeatCommands);
        Assert.Equal(OutFilePrompt.Never, back.OutFilePrompt);
        Assert.Equal(5, back.FileBackups);
        Assert.Equal("notepad", back.GCodeEditor);
        Assert.Equal(1500, back.WindowWidth);
        Assert.Equal(280, back.LeftColumnWidth);
        Assert.Equal(0.5, back.GridMetric.MinorScale);
        Assert.Equal(250, back.GridMetric.Maximum.X);
        Assert.Equal([@"C:\work\a.cb"], back.RecentFiles);
        // Enums are stored by name so the file stays readable and editable.
        Assert.Contains("\"Inches\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Missing_file_gives_defaults_without_error()
    {
        var s = AppSettings.Load(FilePath, out var error);
        Assert.Null(error);
        Assert.Equal("Default", s.DefaultPostProcessor);
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_reports()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var s = AppSettings.Load(FilePath, out var error);
        Assert.NotNull(error);
        Assert.Equal(3, s.FileBackups);
    }

    [Fact]
    public void Hand_edited_values_are_sanitized()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "FileBackups": 999, "WindowWidth": 10, "DefaultGCodeExtension": "tap", "GridMetric": { "MinorScale": 0, "MajorScale": 0 } }""");
        var s = AppSettings.Load(FilePath, out var error);
        Assert.Null(error);
        Assert.Equal(20, s.FileBackups);
        Assert.Equal(1366, s.WindowWidth);
        Assert.Equal(".tap", s.DefaultGCodeExtension);
        Assert.True(s.GridMetric.MinorScale > 0 && s.GridMetric.MajorScale >= s.GridMetric.MinorScale);
        // Missing sections keep their defaults.
        Assert.Equal(0.0625, s.GridInches.MinorScale);
    }

    [Fact]
    public void Recent_files_are_unique_newest_first_and_limited()
    {
        var s = new AppSettings { MaxRecentFiles = 3 };
        foreach (var f in new[] { "a", "b", "c", "b", "d" }) s.AddRecentFile(f);
        Assert.Equal(["d", "b", "c"], s.RecentFiles);
    }

    [Fact]
    public void Clone_is_independent()
    {
        var s = new AppSettings();
        var c = s.Clone();
        c.GridMetric.MinorScale = 7;
        c.RecentFiles.Add("x");
        Assert.Equal(1, s.GridMetric.MinorScale);
        Assert.Empty(s.RecentFiles);
    }

    [Fact]
    public void Grid_and_colours_are_applied_per_unit_system()
    {
        var s = new AppSettings { ToolpathLineColor = new RgbColor(10, 20, 30), SnapToGrid = false };
        var v = new ViewSettings();
        s.ApplyTo(v, DrawingUnits.Millimeters);
        Assert.Equal(1, v.GridMinor);
        Assert.Equal(10, v.GridMajor);
        Assert.Equal(Avalonia.Media.Color.FromRgb(10, 20, 30), v.ToolpathColor);
        Assert.False(v.SnapToGrid);
        s.ApplyGrid(v, DrawingUnits.Inches);
        Assert.Equal(0.0625, v.GridMinor);
        Assert.Equal(1, v.GridMajor);

        v.ShowGrid = false;
        s.CaptureFrom(v);
        Assert.False(s.ShowGrid);
    }
}
