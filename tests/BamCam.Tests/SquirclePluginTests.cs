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

    private static SquircleEntity Complex(double m = 4, double n1 = 4, double n2 = 4, double n3 = 2, double a = 1, double b = 1.2) =>
        new() { ShapeType = SquircleShape.Complex, M = m, N1 = n1, N2 = n2, N3 = n3, A = a, B = b };

    // ---------------------------------------------------------------- simple

    [Theory]
    [InlineData(4, 25, 25)]
    [InlineData(2, 30, 20)]
    [InlineData(1.5, 25, 25)]
    [InlineData(0.5, 25, 25)]
    [InlineData(10, 40, 20)]
    public void Simple_every_point_lies_on_the_curve_and_size_and_area_are_right(double n, double rx, double ry)
    {
        var s = new SquircleEntity { OrigX = 5, OrigY = -3, RadX = rx, RadY = ry, N1 = n, Angle = 0.5 };

        var path = Outline(s);

        Assert.True(path.Closed);
        foreach (var v in path.Vertices)
            Assert.Equal(1, Math.Pow(Math.Abs((v.Point.X - 5) / rx), n) + Math.Pow(Math.Abs((v.Point.Y + 3) / ry), n), 9);
        var box = path.Bounds();
        Assert.Equal(5 - rx, box.Min.X, 9);
        Assert.Equal(5 + rx, box.Max.X, 9);
        Assert.Equal(-3 - ry, box.Min.Y, 9);
        Assert.Equal(-3 + ry, box.Max.Y, 9);
        var exact = 4 * rx * ry * Math.Pow(Gamma(1 + 1 / n), 2) / Gamma(1 + 2 / n);
        Assert.True(Math.Abs(Math.Abs(path.SignedArea()) - exact) / exact < 1e-3, $"area {path.SignedArea()}, exact {exact}");
    }

    [Fact]
    public void Simple_with_n1_2_is_a_circle()
    {
        var path = Outline(new SquircleEntity { RadX = 10, RadY = 10, N1 = 2 });

        Assert.All(path.Vertices, v => Assert.Equal(10, v.XY.Length, 9));
    }

    // ---------------------------------------------------------------- complex

    [Fact]
    public void Complex_with_the_values_of_the_original_is_an_X_of_the_given_radii()
    {
        // The example in the documentation of the original: an X, narrowest along the axes.
        var path = Outline(Complex());

        var box = path.Bounds();
        Assert.Equal(50, box.Max.X, 1);
        Assert.Equal(50, box.Max.Y, 1);
        var byAngle = path.Vertices.Select(v => v.XY).ToList();
        var atZero = byAngle[0];                     // angle 0
        var atDiagonal = byAngle[byAngle.Count / 8]; // angle 45
        // r(0) = 1, r(45°) = (0.25 + 0.5 / 1.2)^-4 = 5.06. X and Y are scaled apart, so only X is compared.
        Assert.Equal(Math.Pow(0.25 + 0.5 / 1.2, -4) * Math.Cos(Math.PI / 4), atDiagonal.X / atZero.X, 6);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void Complex_m_sets_the_number_of_folds(double m)
    {
        // Same radius every 360° / m with equal n2 and n3 and a = b, as the formula has that period.
        var s = Complex(m, 0.5, 2, 2, 1, 1);
        s.Angle = 0.25;
        var points = Outline(s).Vertices.Select(v => v.XY).ToList();
        var step = (int)Math.Round(points.Count / m);
        var scale = points[0].Length;
        Assert.True(scale > 0);
        Assert.Equal(scale, points[step].Length, 6);
    }

    [Fact]
    public void Complex_scales_to_different_radii()
    {
        var s = Complex();
        s.RadX = 100;
        s.RadY = 20;

        var box = Outline(s).Bounds();

        Assert.Equal(100, box.Max.X, 1);
        Assert.Equal(20, box.Max.Y, 1);
    }

    [Fact]
    public void Complex_values_that_have_no_result_at_some_angles_do_not_break()
    {
        // 0^-1 is infinite where the cosine or sine is zero.
        var s = Complex(4, 1, -1, -1);

        foreach (var path in s.ToPaths())
            Assert.All(path.Vertices, v => Assert.True(double.IsFinite(v.Point.X) && double.IsFinite(v.Point.Y)));
    }

    // ---------------------------------------------------------------- accuracy

    [Theory]
    [InlineData(1, 360)]
    [InlineData(0.5, 720)]
    [InlineData(10, 36)]
    [InlineData(60, 6)]
    public void Angle_sets_the_number_of_points(double angle, int points)
    {
        var s = new SquircleEntity { Angle = angle };

        Assert.Equal(points, s.NumberOfPoints);
        Assert.Equal(points, Outline(s).Count);
    }

    [Fact]
    public void Edge_length_sets_the_average_length_of_the_segments()
    {
        var s = new SquircleEntity { Accuracy = SquircleAccuracy.EdgeLength, EdgeLength = 2 };

        var path = Outline(s);

        Assert.Equal(s.NumberOfPoints, path.Count);
        Assert.Equal(2, path.Length / path.Count, 1);
    }

    [Fact]
    public void Too_many_points_are_limited_with_a_note()
    {
        Assert.Empty(new SquircleEntity().Note);
        var many = new SquircleEntity { Angle = 0.05 };
        Assert.Equal(7200, many.NumberOfPoints);
        Assert.Contains("7200", many.Note);

        var tooMany = new SquircleEntity { Angle = 0.001 };
        Assert.Equal(SquircleEntity.MostPoints, tooMany.NumberOfPoints);
        Assert.Equal(SquircleEntity.MostPoints, Outline(tooMany).Count);
        Assert.Contains("limited", tooMany.Note);
    }

    // ---------------------------------------------------------------- properties

    [Fact]
    public void Only_the_values_of_the_chosen_shape_and_accuracy_are_shown()
    {
        var simple = new SquircleEntity();
        Assert.Superset(new HashSet<string> { "M", "N2", "N3", "A", "B", "EdgeLength", "Note" }, simple.HiddenParameters.ToHashSet());
        Assert.DoesNotContain("N1", simple.HiddenParameters);
        Assert.DoesNotContain("Angle", simple.HiddenParameters);

        var complex = new SquircleEntity { ShapeType = SquircleShape.Complex, Accuracy = SquircleAccuracy.EdgeLength };
        Assert.DoesNotContain("M", complex.HiddenParameters);
        Assert.DoesNotContain("EdgeLength", complex.HiddenParameters);
        Assert.Contains("Angle", complex.HiddenParameters);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Impossible_values_still_give_an_outline(double value)
    {
        var path = Outline(new SquircleEntity { RadX = value, RadY = value, N1 = value, Angle = value });

        Assert.True(path.Count >= 3);
        Assert.All(path.Vertices, v => Assert.True(double.IsFinite(v.Point.X) && double.IsFinite(v.Point.Y)));
    }

    [Fact]
    public void A_changed_value_changes_the_outline()
    {
        var s = new SquircleEntity();
        var round = Math.Abs(Outline(s).SignedArea());

        s.N1 = 20;
        s.Invalidate();

        Assert.True(Math.Abs(Outline(s).SignedArea()) > round);
    }

    [Fact]
    public void It_can_be_moved_and_is_saved_with_all_its_values()
    {
        ParametricEntities.Register("bamcam.squircle", () => new SquircleEntity());
        var doc = CadDocument.CreateNew();
        var s = doc.AddEntity(new SquircleEntity
        {
            ShapeType = SquircleShape.Complex, Accuracy = SquircleAccuracy.EdgeLength, EdgeLength = 2,
            M = 6, N1 = 3, N2 = 5, N3 = 7, A = 2, B = 3, RadX = 40, RadY = 15, OrigX = 1, OrigY = 2,
        });
        s.ApplyTransform(Matrix4.Translation(100, 0));
        var box = s.Bounds();

        var back = Assert.IsType<SquircleEntity>(Assert.Single(CbReader.Parse(CbWriter.ToString(doc)).AllEntities()));

        Assert.Equal(SquircleShape.Complex, back.ShapeType);
        Assert.Equal(SquircleAccuracy.EdgeLength, back.Accuracy);
        Assert.Equal((6.0, 3.0, 5.0, 7.0, 2.0, 3.0), (back.M, back.N1, back.N2, back.N3, back.A, back.B));
        Assert.Equal((40.0, 15.0, 1.0, 2.0, 2.0), (back.RadX, back.RadY, back.OrigX, back.OrigY, back.EdgeLength));
        Assert.Equal(box.Min.X, back.Bounds().Min.X, 6);
    }

    [Fact]
    public void Without_the_plugin_the_drawing_shows_a_polyline()
    {
        var doc = CadDocument.CreateNew();
        doc.AddEntity(new SquircleEntity { RadX = 40 });

        Assert.Contains("<pline", CbWriter.ToString(doc));
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
