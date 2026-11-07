using BamCam.App.Services;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;
using InvoluteGear;

namespace BamCam.Tests;

/// <summary>The involute gear, checked against what makes a gear: its dimensions, the rack that cuts it and a second gear.</summary>
public class GearPluginTests
{
    private static GearGeometry Geometry(int teeth = 20, double module = 2, double angle = 20, double shift = 0, double backlash = 0,
        double addendum = 1, double dedendum = 1.25, double tip = 0.38, int points = 16) =>
        new(teeth, module, angle, shift, backlash, addendum, dedendum, tip, points);

    private static double AreaOf(IEnumerable<Path2> paths) => paths.Sum(p => Math.Abs(p.SignedArea()));

    private static Path2 Turned(Path2 path, double angle, double dx = 0) =>
        path.Transformed(Matrix4.RotationZ(angle) * Matrix4.Translation(dx, 0));

    // ---------------------------------------------------------------- dimensions

    [Fact]
    public void A_standard_gear_has_the_dimensions_of_the_book()
    {
        var g = Geometry(teeth: 20, module: 2);

        Assert.Equal(20, g.PitchRadius, 9);
        Assert.Equal(20 * Math.Cos(Math.PI / 9), g.BaseRadius, 9);
        Assert.Equal(22, g.TipRadius, 9);
        Assert.Equal(17.5, g.RootRadius, 9);
        Assert.Equal(Math.PI * 2 / 2, g.ToothThickness, 9);
        Assert.False(g.IsUndercut);
        Assert.False(g.IsPointed);
    }

    [Fact]
    public void The_outline_reaches_the_tip_and_root_circles_and_is_closed_and_counter_clockwise()
    {
        var g = Geometry();
        var path = g.Outline();
        var points = path.Flatten(0.001);

        Assert.True(path.Closed);
        Assert.True(path.SignedArea() > 0);
        Assert.Equal(22, points.Max(p => p.Length), 6);
        Assert.Equal(17.5, points.Min(p => p.Length), 6);
    }

    [Fact]
    public void Each_tooth_is_the_same()
    {
        var path = Geometry(teeth: 17).Outline();

        var turned = Turned(path, 2 * Math.PI / 17);

        Assert.True(AreaOf(Polygons.Xor([path], [turned])) < 1e-3);
    }

    [Theory]
    [InlineData(20, 0.0)]
    [InlineData(40, 0.3)]
    [InlineData(12, 0.5)]
    public void The_flank_is_an_involute_of_the_base_circle(int teeth, double shift)
    {
        var g = Geometry(teeth: teeth, shift: shift);
        var checkedPoints = 0;

        foreach (var p in g.Side.Where(p => p.Length > g.BaseRadius * 1.005 && p.Length > g.PitchRadius * 0.99))
        {
            var roll = Math.Sqrt(p.Length * p.Length / (g.BaseRadius * g.BaseRadius) - 1);
            var expected = g.ToothThickness / (2 * g.PitchRadius) + (Math.Tan(Math.PI / 9) - Math.PI / 9) - (roll - Math.Atan(roll));
            Assert.Equal(expected, p.Angle, 6);
            checkedPoints++;
        }
        Assert.True(checkedPoints >= 3, $"{checkedPoints} points");
    }

    [Fact]
    public void Tooth_thickness_at_the_pitch_circle_is_what_the_flank_shows()
    {
        var g = Geometry(teeth: 25, module: 3, backlash: 0.1);

        Assert.Equal(Math.PI * 3 / 2 - 0.1, g.ToothThickness, 9);
        Assert.Equal(g.ToothThickness / (2 * g.PitchRadius), g.HalfAngle(g.PitchRadius), 9);
    }

    // ---------------------------------------------------------------- the rack that cuts it

    /// <summary>Independent of the plugin: two teeth of the rack as a polygon at a roll angle, reaching far outwards.</summary>
    private static Path2 RackTooth(GearGeometry g, double module, double shift, double hf, double tipRadius, double roll, int side)
    {
        const double alpha = Math.PI / 9;
        var (r, tan, cos, sin) = (g.PitchRadius, Math.Tan(alpha), Math.Cos(alpha), Math.Sin(alpha));
        var half = Math.PI * module / 4 + shift * module * tan;
        var tipV = shift * module - hf * module;
        var centreV = tipV + tipRadius;
        var centreU = half - centreV * tan + tipRadius / cos;
        var middle = Math.PI * module / 2;
        var top = 6 * module;
        // Right half of the tooth of the rack around u = middle, mirrored for its left half.
        var rack = new List<(double U, double V)>();
        rack.Add((half - top * tan, top));
        for (var i = 0; i <= 24; i++)
        {
            // From the flank to the tip line, seen from the centre of the rounding.
            var angle = Math.PI + alpha + (Math.PI / 2 - alpha) * i / 24.0;
            rack.Add((centreU + tipRadius * Math.Cos(angle), centreV + tipRadius * Math.Sin(angle)));
        }
        var outline = new List<(double U, double V)>(rack);
        for (var i = rack.Count - 1; i >= 0; i--) outline.Add((2 * middle - rack[i].U, rack[i].V));
        var offset = side * Math.PI * module;
        var normal = Point2.FromAngle(roll, 1);
        var tangent = new Point2(-normal.Y, normal.X);
        var contact = Point2.FromAngle(roll, r);
        var points = outline.Select(q => contact + tangent * (q.U + offset - r * roll) + normal * q.V).ToList();
        return Path2.FromPoints(points, true);
    }

    public static TheoryData<int, double> Cases() => new()
    {
        { 40, 0.0 }, { 20, 0.0 }, { 17, 0.0 }, { 12, 0.0 }, { 8, 0.0 }, { 6, 0.0 }, { 12, 0.5 }, { 20, 0.5 }, { 30, -0.3 },
    };

    private static List<Path2> CutByRack(GearGeometry g, double shift)
    {
        // What is left of a wedge of one tooth when the rack has rolled past it.
        const double module = 2;
        var wedgeAngle = Math.PI / (g.PitchRadius * 2 / module);
        var wedge = new List<Point2> { Point2.Zero };
        for (var i = -40; i <= 40; i++) wedge.Add(Point2.FromAngle(wedgeAngle * i / 40, g.TipRadius));
        var left = new List<Path2> { Path2.FromPoints(wedge, true) };
        var range = 5 * module / g.PitchRadius + wedgeAngle;
        for (var i = -300; i <= 300; i++)
        {
            var roll = range * i / 300;
            foreach (var side in new[] { -2, -1, 0, 1 })
                left = Polygons.Difference(left, [RackTooth(g, module, shift, 1.25, 0.38 * module, roll, side)]);
        }
        return left;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void One_tooth_is_what_the_rack_leaves_of_the_material(int teeth, double shift)
    {
        var g = Geometry(teeth: teeth, module: 2, shift: shift, points: 40);
        var outlineArea = g.Outline().SignedArea();

        var cut = CutByRack(g, shift);

        // The area of one tooth of the gear: a share of the outline.
        var perTooth = outlineArea / teeth;
        var area = AreaOf(cut);
        Assert.True(Math.Abs(area - perTooth) < 0.002 * perTooth + 0.02, $"rack leaves {area:0.0000}, outline has {perTooth:0.0000}");

        // And the shapes lie on each other: nothing of the gear is more than a sliver away.
        var between = AreaOf(Polygons.Xor(cut, Polygons.Intersection([g.Outline()], [WedgeOf(g, teeth)])));
        Assert.True(between < 0.012 * perTooth + 0.05, $"{between:0.0000} of {perTooth:0.0000}");
    }

    private static Path2 WedgeOf(GearGeometry g, int teeth)
    {
        var angle = Math.PI / teeth;
        var points = new List<Point2> { Point2.Zero };
        for (var i = -40; i <= 40; i++) points.Add(Point2.FromAngle(angle * i / 40, g.TipRadius * 1.01));
        return Path2.FromPoints(points, true);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(14, true)]
    [InlineData(18, false)]
    [InlineData(60, false)]
    public void Few_teeth_are_undercut(int teeth, bool expected)
    {
        var g = Geometry(teeth: teeth);

        Assert.Equal(expected, g.IsUndercut);
        Assert.Equal(expected, g.Note.Contains("Undercut"));
    }

    [Fact]
    public void A_profile_shift_avoids_the_undercut()
    {
        Assert.True(Geometry(teeth: 12).IsUndercut);
        Assert.False(Geometry(teeth: 12, shift: 0.35).IsUndercut);
    }

    // ---------------------------------------------------------------- two gears

    /// <summary>Largest overlap of two gears over a whole tooth of the first one, in square drawing units.</summary>
    private static double WorstOverlap(GearGeometry a, GearGeometry b, double distance)
    {
        int z1 = (int)Math.Round(2 * a.PitchRadius / 2), z2 = (int)Math.Round(2 * b.PitchRadius / 2);
        var first = a.Outline();
        var second = b.Outline();
        var worst = 0.0;
        for (var i = 0; i < 40; i++)
        {
            var turn = 2 * Math.PI / z1 * i / 40;
            // On the line of the centres, a tooth of the first faces a gap of the second.
            var other = Turned(second, Math.PI + Math.PI / z2 - turn * z1 / z2, distance);
            worst = Math.Max(worst, AreaOf(Polygons.Intersection([Turned(first, turn)], [other])));
        }
        return worst;
    }

    [Theory]
    [InlineData(20, 33)]
    [InlineData(17, 17)]
    [InlineData(18, 90)]
    public void Two_gears_run_together_at_their_centre_distance(int z1, int z2)
    {
        var a = Geometry(teeth: z1, points: 32);
        var b = Geometry(teeth: z2, points: 32);

        var overlap = WorstOverlap(a, b, a.PitchRadius + b.PitchRadius);
        Assert.True(overlap < 0.02, $"{overlap}");
    }

    [Fact]
    public void Too_close_they_collide()
    {
        var a = Geometry(teeth: 20, points: 32);
        var b = Geometry(teeth: 33, points: 32);

        var overlap = WorstOverlap(a, b, a.PitchRadius + b.PitchRadius - 0.5);
        Assert.True(overlap > 0.3, $"{overlap}");
    }

    [Fact]
    public void Backlash_leaves_play_between_the_flanks()
    {
        var a = Geometry(teeth: 20, backlash: 0.1, points: 32);
        var b = Geometry(teeth: 33, backlash: 0.1, points: 32);

        Assert.Equal(0, WorstOverlap(a, b, a.PitchRadius + b.PitchRadius), 6);
    }

    [Fact]
    public void Shifted_gears_run_together_when_the_shifts_add_up_to_zero()
    {
        var a = Geometry(teeth: 15, shift: 0.4, points: 32);
        var b = Geometry(teeth: 40, shift: -0.4, points: 32);

        var overlap = WorstOverlap(a, b, a.PitchRadius + b.PitchRadius);
        Assert.True(overlap < 0.02, $"{overlap}");
    }

    // ---------------------------------------------------------------- the tool

    [Theory]
    [InlineData(20, 2.0)]
    [InlineData(40, 1.0)]
    [InlineData(12, 3.0)]
    public void A_tool_of_the_largest_diameter_reaches_the_outline_and_a_larger_one_does_not(int teeth, double module)
    {
        var g = Geometry(teeth: teeth, module: module, points: 40);
        var path = g.Outline();
        double Left(double diameter)
        {
            // How far material stays that a tool of this size cannot remove: the outline grown and shrunk again.
            var closed = Polygons.Offset(Polygons.Offset([path], diameter / 2), -diameter / 2);
            // One tooth is enough: all look the same, and this takes much longer over the whole gear.
            return closed.SelectMany(p => p.Flatten()).Where(p => Math.Abs(p.Angle) < 2 * Math.PI / teeth).Max(p => path.DistanceTo(p));
        }

        var d = g.LargestToolDiameter;
        Assert.True(d > 0.5 * module && d < 2 * module, $"{d}");
        Assert.True(Left(d * 0.95) < 0.01, $"{Left(d * 0.95)}");
        Assert.True(Left(d * 1.3) > 0.01, $"{Left(d * 1.3)} {d}");
    }

    // ---------------------------------------------------------------- limits

    [Fact]
    public void Too_strong_a_shift_gives_pointed_teeth_and_says_so()
    {
        var g = Geometry(teeth: 8, shift: 1.1);

        Assert.True(g.IsPointed);
        Assert.Contains("Pointed", g.Note);
        Assert.True(g.TipRadius < g.PitchRadius + 2.1 * 2);
    }

    [Theory]
    [InlineData(0, 0, 0, -5, -5, 0, 0, -1, 0)]
    [InlineData(3, -2, 90, 9, 50, 9, 9, 99, 1000)]
    [InlineData(1000, 0.001, 5, -9, 0, 0.01, 0.01, 0, 2)]
    [InlineData(4, 1, 40, 5, 0, 3, 3, 5, 3)]
    [InlineData(5, double.NaN, double.NaN, double.PositiveInfinity, double.NaN, double.NaN, double.NaN, double.NaN, 16)]
    public void Impossible_values_still_give_an_outline(int teeth, double module, double angle, double shift, double backlash, double addendum, double dedendum, double tip, int points)
    {
        var g = new GearGeometry(teeth, module, angle, shift, backlash, addendum, dedendum, tip, points);
        var path = g.Outline();

        Assert.True(path.Count >= 12);
        Assert.All(path.Vertices, v => Assert.True(double.IsFinite(v.Point.X) && double.IsFinite(v.Point.Y) && double.IsFinite(v.Bulge)));
        Assert.True(g.RootRadius > 0);
        Assert.True(g.TipRadius > g.RootRadius);
    }

    // ---------------------------------------------------------------- the object

    [Fact]
    public void The_object_has_an_outline_and_a_bore()
    {
        var gear = new GearEntity { BoreDiameter = 10 };

        var paths = gear.ToPaths().ToList();

        Assert.Equal(2, paths.Count);
        Assert.Equal(10, paths[1].Bounds().Width, 6);
        Assert.Equal(44, gear.TipDiameter, 6);
        Assert.Equal(40, gear.PitchDiameter, 6);
    }

    [Fact]
    public void A_bore_larger_than_the_root_is_left_out()
    {
        Assert.Single(new GearEntity { BoreDiameter = 100 }.ToPaths());
        Assert.Single(new GearEntity { BoreDiameter = 0 }.ToPaths());
    }

    [Fact]
    public void A_changed_value_changes_the_outline()
    {
        var gear = new GearEntity();
        var before = gear.Bounds().Width;

        gear.Teeth = 40;
        gear.Invalidate();

        Assert.True(gear.Bounds().Width > before * 1.5);
    }

    [Fact]
    public void The_note_is_only_shown_when_there_is_one()
    {
        Assert.Contains("Note", new GearEntity().HiddenParameters);
        Assert.DoesNotContain("Note", new GearEntity { Teeth = 8 }.HiddenParameters);
    }

    [Fact]
    public void It_can_be_moved_and_is_saved_with_all_its_values()
    {
        ParametricEntities.Register("bamcam.involutegear", () => new GearEntity());
        var doc = CadDocument.CreateNew();
        var gear = doc.AddEntity(new GearEntity
        {
            Teeth = 31, Module = 1.5, PressureAngle = 14.5, ProfileShift = 0.2, Backlash = 0.05, BoreDiameter = 12,
            Addendum = 0.9, Dedendum = 1.2, CutterTipRadius = 0.25, PointsPerFlank = 12,
        });
        gear.ApplyTransform(Matrix4.Translation(100, 0));
        var box = gear.Bounds();

        var back = Assert.IsType<GearEntity>(Assert.Single(CbReader.Parse(CbWriter.ToString(doc)).AllEntities()));

        Assert.Equal((31, 12), (back.Teeth, back.PointsPerFlank));
        Assert.Equal((1.5, 14.5, 0.2, 0.05, 12.0), (back.Module, back.PressureAngle, back.ProfileShift, back.Backlash, back.BoreDiameter));
        Assert.Equal((0.9, 1.2, 0.25), (back.Addendum, back.Dedendum, back.CutterTipRadius));
        Assert.Equal(box.Min.X, back.Bounds().Min.X, 6);
    }

    [Fact]
    public void Without_the_plugin_the_drawing_shows_a_polyline()
    {
        var doc = CadDocument.CreateNew();
        doc.AddEntity(new GearEntity());

        Assert.Contains("<pline", CbWriter.ToString(doc));
    }

    [Fact]
    public void The_plugin_adds_its_command_to_the_draw_menu()
    {
        var manager = new PluginManager();
        var info = PluginManager.Inspect(typeof(GearPlugin).Assembly.Location)!;
        Assert.Equal("Involute Gear", info.Name);
        manager.Plugins.Add(info);

        manager.Load(info, new Version(1, 0), _ => { });

        Assert.Equal(PluginState.Loaded, info.State);
        Assert.Equal("Draw", Assert.Single(manager.Commands).Command.Menu);
    }
}
