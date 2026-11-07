using BamCam.App.Services;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;
using Squircle;

namespace BamCam.Tests;

public class SquirclePluginTests
{
    private static Path2 Outline(SquircleEntity s) => Assert.Single(s.ToPaths());

    private static double Gamma(double x)
    {
        // Lanczos approximation, for the exact area of a superellipse.
        double[] g = [76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5];
        var y = x;
        var t = x + 5.5;
        t -= (x + 0.5) * Math.Log(t);
        var s = 1.000000000190015;
        foreach (var c in g) s += c / ++y;
        return Math.Exp(-t + Math.Log(2.5066282746310005 * s / x));
    }

    [Theory]
    [InlineData(4, 25, 25)]
    [InlineData(2, 30, 20)]
    [InlineData(1.5, 25, 25)]
    [InlineData(0.5, 25, 25)]
    [InlineData(10, 40, 20)]
    public void Every_point_lies_on_the_curve_and_size_and_area_are_right(double power, double rx, double ry)
    {
        var s = new SquircleEntity { CenterX = 5, CenterY = -3, HalfWidth = rx, HalfHeight = ry, Power = power };

        var path = Outline(s);

        Assert.True(path.Closed);
        foreach (var v in path.Vertices)
            Assert.Equal(1, Math.Pow(Math.Abs((v.Point.X - 5) / rx), power) + Math.Pow(Math.Abs((v.Point.Y + 3) / ry), power), 9);
        var box = path.Bounds();
        Assert.Equal(5 - rx, box.Min.X, 9);
        Assert.Equal(5 + rx, box.Max.X, 9);
        Assert.Equal(-3 - ry, box.Min.Y, 9);
        Assert.Equal(-3 + ry, box.Max.Y, 9);
        var exact = 4 * rx * ry * Math.Pow(Gamma(1 + 1 / power), 2) / Gamma(1 + 2 / power);
        Assert.True(Math.Abs(Math.Abs(path.SignedArea()) - exact) / exact < 1e-3, $"area {path.SignedArea()}, exact {exact}");
    }

    [Fact]
    public void Power_2_gives_a_circle()
    {
        var path = Outline(new SquircleEntity { HalfWidth = 10, HalfHeight = 10, Power = 2 });

        Assert.All(path.Vertices, v => Assert.Equal(10, v.XY.Length, 9));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Impossible_values_still_give_an_outline(double value)
    {
        var path = Outline(new SquircleEntity { HalfWidth = value, HalfHeight = value, Power = value, Segments = (int)value });

        Assert.True(path.Count >= 16);
        Assert.All(path.Vertices, v => Assert.False(double.IsNaN(v.Point.X) || double.IsNaN(v.Point.Y)));
    }

    [Fact]
    public void A_changed_value_changes_the_outline()
    {
        var s = new SquircleEntity();
        var round = Math.Abs(Outline(s).SignedArea());

        s.Power = 20;
        s.Invalidate();

        Assert.True(Math.Abs(Outline(s).SignedArea()) > round);
    }

    [Fact]
    public void It_can_be_moved_and_is_saved_with_its_values()
    {
        ParametricEntities.Register("bamcam.squircle", () => new SquircleEntity());
        var doc = CadDocument.CreateNew();
        var s = doc.AddEntity(new SquircleEntity { HalfWidth = 40, HalfHeight = 15, Power = 3 });
        s.ApplyTransform(Matrix4.Translation(100, 0));
        Assert.Equal(60, s.Bounds().Min.X, 9);

        var back = Assert.IsType<SquircleEntity>(Assert.Single(CbReader.Parse(CbWriter.ToString(doc)).AllEntities()));

        Assert.Equal(40, back.HalfWidth);
        Assert.Equal(15, back.HalfHeight);
        Assert.Equal(3, back.Power);
        Assert.Equal(60, back.Bounds().Min.X, 6);
    }

    [Fact]
    public void Without_the_plugin_the_drawing_shows_a_polyline()
    {
        // As in CamBam or BamCam without the plugin: the file has the outline as an ordinary polyline.
        var doc = CadDocument.CreateNew();
        doc.AddEntity(new SquircleEntity { HalfWidth = 40 });

        var xml = CbWriter.ToString(doc);

        Assert.Contains("<pline", xml);
    }

    [Fact]
    public void The_plugin_adds_its_command_to_the_draw_menu()
    {
        var manager = new PluginManager();
        var info = PluginManager.Inspect(typeof(SquirclePlugin).Assembly.Location)!;
        Assert.Equal("Squircle", info.Name);
        manager.Plugins.Add(info);

        manager.Load(info, new Version(1, 0), _ => { });

        Assert.Equal(PluginState.Loaded, info.State);
        Assert.Equal("Draw", Assert.Single(manager.Commands).Command.Menu);
    }
}
