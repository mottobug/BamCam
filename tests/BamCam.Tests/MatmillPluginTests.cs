using BamCam.App.Services;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Simulation;
using BamCam.IO.Cb;
using MatmillPlugin;

namespace BamCam.Tests;

/// <summary>Trochoidal pocket and profile (the port of matmill), checked with BamCam's own milling simulation.</summary>
[Collection(GlobalLibrariesCollection.Name)]
public class MatmillPluginTests
{
    private const double Tool = 6;

    /// <summary>
    /// What BamCam does when it loads the plugin: its operations and generators are registered. Done here with the
    /// types of this test assembly (the plugin manager loads the plugin in a context of its own).
    /// </summary>
    private sealed class Host : BamCam.Plugins.IPluginHost
    {
        public Version HostVersion => new(1, 0);
        public string Language => "en";
        public void AddCommand(BamCam.Plugins.PluginCommand command) { }
        public void Log(string message) { }
        public void AddImporter(BamCam.Plugins.FileFormat format, Func<string, CadDocument> read) { }
        public void AddExporter(BamCam.Plugins.FileFormat format, Action<CadDocument, string> write) { }
        public void AddPostMacro(string name, Func<BamCam.Plugins.PostMacroContext, string> evaluate) { }
        public void AddEntityType<TEntity>() where TEntity : ParametricEntity, new() { }

        public void AddOperation<TOp>(Action<ToolpathContext, TOp, Toolpath> generator) where TOp : MachineOp, new()
        {
            MachineOpFactory.Register(new TOp().XmlName, () => new TOp());
            ToolpathGenerator.RegisterGenerator(generator);
        }
    }

    public MatmillPluginTests() => new Plugin().Initialize(new Host());

    private static Path2 Rect(double x0, double y0, double x1, double y1) =>
        Path2.FromPoints([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], closed: true);

    private static (CadDocument Doc, T Op) Operation<T>(params Path2[] shapes) where T : TrochoOp, new()
    {
        var doc = new CadDocument();
        var op = new T { Name = "troch" };
        foreach (var s in shapes) op.PrimitiveIds.Add(doc.AddEntity(new PolylineEntity(s)).Id);
        op.ToolDiameter.Set(Tool);
        op.StepOver.Set(0.2);
        op.TargetDepth.Set(-3);
        op.DepthIncrement.Set(3);
        op.CutFeedrate.Set(1000);
        op.PlungeFeedrate.Set(200);
        op.ChordFeedrate.Set(3000);
        op.SpiralFeedrate.Set(500);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    private static Toolpath Generate(CadDocument doc, MachineOp op)
    {
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.True(tp.Warnings.Count == 0, string.Join("\n", tp.Warnings));
        Assert.True(tp.Moves.Count > 10, $"{tp.Moves.Count} moves");
        return tp;
    }

    /// <summary>The material after the toolpath, on cells of 0.1 over the given area.</summary>
    private static HeightField Simulate(CadDocument doc, MachineOp op, Toolpath tp, double x0, double y0, double x1, double y1)
    {
        var field = new HeightField(x0, y0, 0.1, (int)Math.Round((x1 - x0) / 0.1), (int)Math.Round((y1 - y0) / 0.1), 0, -10);
        foreach (var move in MillingSimulation.Moves(doc, [(op, tp)], 0.01)) MillingSimulation.Cut(field, move);
        return field;
    }

    private static IEnumerable<(Point2 Centre, float Z)> Cells(HeightField f)
    {
        for (var j = 0; j < f.Ny; j++)
        for (var i = 0; i < f.Nx; i++)
            yield return (f.CellCentre(i, j), f[i, j]);
    }

    // ---------------------------------------------------------------- pocket

    [Fact]
    public void A_pocket_is_cleared_and_the_walls_are_not_touched()
    {
        var outline = Rect(0, 0, 50, 30);
        var (doc, op) = Operation<TrochoPocketOp>(outline);

        var tp = Generate(doc, op);
        var f = Simulate(doc, op, tp, -5, -5, 55, 35);

        // Nothing is cut outside (a cell beyond the wall), everything well inside is down at the target depth.
        var outside = Cells(f).Where(c => !outline.Contains(c.Centre) && outline.DistanceTo(c.Centre) > 0.15).ToList();
        Assert.All(outside, c => Assert.Equal(0, c.Z, 3));
        var inner = Cells(f).Where(c => outline.Contains(c.Centre) && outline.DistanceTo(c.Centre) > 0.6 &&
                                        new[] { new Point2(0, 0), new(50, 0), new(50, 30), new(0, 30) }.All(k => Point2.Distance(k, c.Centre) > Tool)).ToList();
        var cut = inner.Count(c => c.Z < -2.99);
        Assert.True(cut > 0.98 * inner.Count, $"{cut} of {inner.Count} cells cut");
    }

    [Fact]
    public void An_island_stays()
    {
        var outline = Rect(0, 0, 60, 40);
        var island = Rect(25, 15, 35, 25);
        var (doc, op) = Operation<TrochoPocketOp>(outline, island);

        var tp = Generate(doc, op);
        var f = Simulate(doc, op, tp, -5, -5, 65, 45);

        var onIsland = Cells(f).Where(c => island.Contains(c.Centre) && island.DistanceTo(c.Centre) > 0.15).ToList();
        Assert.NotEmpty(onIsland);
        Assert.All(onIsland, c => Assert.Equal(0, c.Z, 3));
        // Around it the material is gone.
        Assert.True(f[(int)((20 + 5) / 0.1), (int)((20 + 5) / 0.1)] < -2.99);
        Assert.True(f[(int)((40 + 5) / 0.1), (int)((20 + 5) / 0.1)] < -2.99);
    }

    [Fact]
    public void A_round_pocket_is_cut_and_stays_inside()
    {
        var circle = new Path2([new Vertex(new Point3(20, 0, 0), 1), new Vertex(new Point3(-20, 0, 0), 1)], true);
        var (doc, op) = Operation<TrochoPocketOp>(circle);

        var tp = Generate(doc, op);

        foreach (var m in tp.Moves.Where(m => m.Kind != MoveKind.Rapid && m.To.Z < -0.01))
            Assert.True(m.To.XY.Length <= 20 - Tool / 2 + 0.01, $"{m.To} outside");
    }

    /// <summary>The arcs that cut (cut feed rate), each turning one way or the other.</summary>
    private static List<Move> Slices(Toolpath tp) =>
        [.. tp.Moves.Where(m => m.Kind is MoveKind.ArcCW or MoveKind.ArcCCW && Math.Abs(m.Feed - 1000) < 1e-9)];

    [Theory]
    [InlineData(MillingDirection.Climb, SpindleDirection.CW, MoveKind.ArcCCW)]
    [InlineData(MillingDirection.Conventional, SpindleDirection.CW, MoveKind.ArcCW)]
    [InlineData(MillingDirection.Climb, SpindleDirection.CCW, MoveKind.ArcCW)]
    public void Slices_turn_with_the_milling_direction(MillingDirection direction, SpindleDirection spindle, MoveKind expected)
    {
        var (doc, op) = Operation<TrochoPocketOp>(Rect(0, 0, 50, 30));
        op.MillingDirection.Set(direction);
        op.SpindleDirection.Set(spindle);

        var slices = Slices(Generate(doc, op));

        Assert.NotEmpty(slices);
        Assert.All(slices, m => Assert.Equal(expected, m.Kind));
    }

    [Fact]
    public void Mixed_cuts_both_ways_and_needs_fewer_chords()
    {
        var (doc, op) = Operation<TrochoPocketOp>(Rect(0, 0, 50, 30));
        op.MillingDirection.Set(MillingDirection.Climb);
        var oneWay = Generate(doc, op);
        op.MillingDirection.Set(MillingDirection.Mixed);
        var mixed = Generate(doc, op);

        var kinds = Slices(mixed).Select(m => m.Kind).Distinct().ToList();
        Assert.Equal(2, kinds.Count);
        double ChordLength(Toolpath tp)
        {
            var length = 0.0;
            for (var i = 1; i < tp.Moves.Count; i++)
                if (Math.Abs(tp.Moves[i].Feed - 3000) < 1e-9) length += Point3.Distance(tp.Moves[i - 1].To, tp.Moves[i].To);
            return length;
        }
        Assert.True(ChordLength(mixed) < 0.7 * ChordLength(oneWay), $"mixed {ChordLength(mixed):0.0}, one way {ChordLength(oneWay):0.0}");
    }

    [Fact]
    public void Chords_spiral_and_slices_get_their_feed_rates()
    {
        var (doc, op) = Operation<TrochoPocketOp>(Rect(0, 0, 50, 30));

        var feeds = Generate(doc, op).Moves.Where(m => m.Kind != MoveKind.Rapid).Select(m => m.Feed).Distinct().ToList();

        Assert.Contains(1000.0, feeds);
        Assert.Contains(3000.0, feeds);
        Assert.Contains(500.0, feeds);
        Assert.Contains(200.0, feeds);
    }

    [Fact]
    public void Smooth_chords_join_the_slices_with_arcs()
    {
        var (doc, op) = Operation<TrochoPocketOp>(Rect(0, 0, 50, 30));
        op.SmoothChords.Set(true);

        var tp = Generate(doc, op);

        Assert.Contains(tp.Moves, m => m.Kind is MoveKind.ArcCW or MoveKind.ArcCCW && Math.Abs(m.Feed - 3000) < 1e-9);
    }

    [Fact]
    public void Each_depth_level_is_cut_and_level_first_goes_from_pocket_to_pocket()
    {
        var (doc, op) = Operation<TrochoPocketOp>(Rect(0, 0, 30, 20), Rect(50, 0, 80, 20));
        op.DepthIncrement.Set(1);
        op.CutOrdering.Set(CutOrdering.LevelFirst);

        var tp = Generate(doc, op);

        var cutting = tp.Moves.Where(m => m.Kind != MoveKind.Rapid && Math.Abs(m.Feed - 1000) < 1e-9).ToList();
        var levels = cutting.Select(m => Math.Round(m.To.Z, 6)).Distinct().ToList();
        Assert.Equal([-1.0, -2.0, -3.0], levels);
        // Level first: both pockets at -1 before either goes to -2.
        var firstAtTwo = cutting.FindIndex(m => m.To.Z < -1.5);
        Assert.Contains(cutting.Take(firstAtTwo), m => m.To.X < 30);
        Assert.Contains(cutting.Take(firstAtTwo), m => m.To.X > 50);
    }

    [Fact]
    public void A_spiral_lead_in_goes_down_on_a_helix()
    {
        var (doc, op) = Operation<TrochoPocketOp>(Rect(0, 0, 50, 30));
        op.LeadInMove.Set(new LeadMove(LeadMoveType.Spiral, SpiralAngle: 5));

        var tp = Generate(doc, op);

        var descending = tp.Moves.Where((m, i) => i > 0 && m.Kind is MoveKind.ArcCW or MoveKind.ArcCCW && m.To.Z < tp.Moves[i - 1].To.Z - 1e-9).ToList();
        Assert.NotEmpty(descending);
        Assert.DoesNotContain(tp.Moves, m => m.Kind == MoveKind.Feed && Math.Abs(m.Feed - 200) < 1e-9 && m.To.Z < -2.9);
    }

    [Fact]
    public void A_tool_too_large_for_the_pocket_says_so()
    {
        var (doc, op) = Operation<TrochoPocketOp>(Rect(0, 0, 4, 4));

        var tp = ToolpathGenerator.Generate(doc, op);

        Assert.NotEmpty(tp.Warnings);
        Assert.DoesNotContain(tp.Moves, m => m.To.Z < -0.01);
    }

    // ---------------------------------------------------------------- profile

    [Theory]
    [InlineData(InsideOutside.Outside)]
    [InlineData(InsideOutside.Inside)]
    public void A_profile_cuts_a_slot_of_the_cut_width_beside_the_shape(InsideOutside side)
    {
        var shape = Rect(0, 0, 50, 30);
        var (doc, op) = Operation<TrochoProfileOp>(shape);
        op.InsideOutside.Set(side);
        op.CutWidth.Set(10);

        var tp = Generate(doc, op);
        var f = Simulate(doc, op, tp, -20, -20, 70, 50);

        bool OnSide(Point2 p) => side == InsideOutside.Outside ? !shape.Contains(p) : shape.Contains(p);
        foreach (var (centre, z) in Cells(f))
        {
            var d = shape.DistanceTo(centre);
            if (!OnSide(centre) && d > 0.15) Assert.True(Math.Abs(z) < 1e-3, $"cut on the wrong side at {centre}");
            if (OnSide(centre) && d > 10 + 0.15) Assert.True(Math.Abs(z) < 1e-3, $"cut wider than 10 at {centre}");
        }
        // The middle of the slot is cut all the way round (away from the corners).
        foreach (var p in new[] { new Point2(25, 0), new(50, 15), new(25, 30), new(0, 15) })
        {
            var normal = side == InsideOutside.Outside ? p - new Point2(25, 15) : new Point2(25, 15) - p;
            var probe = p + normal.Normalized() * 5;
            Assert.True(f[(int)((probe.X + 20) / 0.1), (int)((probe.Y + 20) / 0.1)] < -2.99, $"not cut at {probe}");
        }
    }

    [Theory]
    [InlineData(InsideOutside.Outside, -1)]
    [InlineData(InsideOutside.Inside, 1)]
    public void An_open_line_is_cut_on_the_side_of_its_point_order(InsideOutside side, int expectedSign)
    {
        // Along +X: right of it is -Y (Outside), left +Y (Inside).
        var (doc, op) = Operation<TrochoProfileOp>(Path2.FromPoints([new(0, 0), new(40, 0)], closed: false));
        op.InsideOutside.Set(side);

        var tp = Generate(doc, op);

        var cutting = tp.Moves.Where(m => m.Kind != MoveKind.Rapid && m.To.Z < -0.01).ToList();
        Assert.All(cutting, m => Assert.True(Math.Sign(m.To.Y) == expectedSign || Math.Abs(m.To.Y) < Tool / 2, $"{m.To}"));
        Assert.Contains(cutting, m => Math.Abs(m.To.Y) > Tool);
    }

    [Fact]
    public void Clearing_inside_corners_reaches_into_the_corner()
    {
        // An L shape with an inside corner at (20, 20), cut inside.
        var shape = Path2.FromPoints([new(0, 0), new(40, 0), new(40, 20), new(20, 20), new(20, 40), new(0, 40)], closed: true);
        var (doc, op) = Operation<TrochoProfileOp>(shape);
        op.InsideOutside.Set(InsideOutside.Inside);
        op.CutWidth.Set(12);
        op.ClearInsideCorners.Set(true);

        var tp = Generate(doc, op);
        var f = Simulate(doc, op, tp, -5, -5, 45, 45);

        Assert.All(Cells(f).Where(c => !shape.Contains(c.Centre) && shape.DistanceTo(c.Centre) > 0.15), c => Assert.Equal(0, c.Z, 3));
        Assert.True(f[(int)((19 + 5) / 0.1), (int)((19 + 5) / 0.1)] < -2.99, "the inside corner is cleared");
    }

    [Fact]
    public void A_cut_width_below_the_tool_is_refused()
    {
        var (doc, op) = Operation<TrochoProfileOp>(Rect(0, 0, 30, 20));
        op.CutWidth.Set(Tool);

        var tp = ToolpathGenerator.Generate(doc, op);

        Assert.Contains(tp.Warnings, w => w.Contains("cut width is too small"));
    }

    // ---------------------------------------------------------------- files and plugin

    [Fact]
    public void Operations_are_saved_with_the_names_of_matmill_and_read_back()
    {
        var (doc, op) = Operation<TrochoProfileOp>(Rect(0, 0, 30, 20));
        op.ClearInsideCorners.Set(true);
        op.MinStepover.Set(0.7);
        op.SmoothChords.Set(true);
        op.CutWidth.Set(14);

        var text = CbWriter.ToString(doc);

        Assert.Contains("xsi:type=\"MOPTrochoprof\"", text);
        foreach (var name in new[] { "Chord_feedrate", "Spiral_feedrate", "Should_smooth_chords", "Clear_inside_corners", "Min_stepover", "CutWidth", "StepOver" })
            Assert.Contains("<" + name, text);
        var back = Assert.IsType<TrochoProfileOp>(Assert.Single(CbReader.Parse(text).Parts.SelectMany(p => p.Operations)));
        Assert.True(back.ClearInsideCorners.Value);
        Assert.Equal(0.7, back.MinStepover.Value);
        Assert.Equal(3000, back.ChordFeedrate.Value);
        Assert.Equal(14, back.CutWidth.Value);
    }

    [Fact]
    public void A_pocket_written_as_matmill_writes_it_is_read()
    {
        // As matmill's operations appear in a CamBam drawing: plain values without a state for its own parameters.
        const string cb = """
            <?xml version="1.0" encoding="utf-8"?>
            <CADFile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" Version="0.9.8.0" Name="t">
              <layers><layer name="Default"><objects>
                <pline id="1" Closed="true"><pts><p>0,0,0</p><p>40,0,0</p><p>40,30,0</p><p>0,30,0</p></pts></pline>
              </objects></layer></layers>
              <parts><part Name="Part1"><machineops>
                <mop xsi:type="MOPTrochopock">
                  <Name>TrochoPock1</Name>
                  <TargetDepth state="Value">-2</TargetDepth>
                  <ToolDiameter state="Value">6</ToolDiameter>
                  <StepOver state="Value">0.15</StepOver>
                  <MillingDirection state="Value">Mixed</MillingDirection>
                  <Chord_feedrate>2500</Chord_feedrate>
                  <Spiral_feedrate>0</Spiral_feedrate>
                  <Should_smooth_chords>false</Should_smooth_chords>
                  <Should_draw_chords>false</Should_draw_chords>
                  <Min_stepover>0.8</Min_stepover>
                  <primitive><prim>1</prim></primitive>
                </mop>
              </machineops></part></parts>
            </CADFile>
            """;

        var doc = CbReader.Parse(cb);

        var op = Assert.IsType<TrochoPocketOp>(Assert.Single(doc.Parts.SelectMany(p => p.Operations)));
        Assert.Equal((0.15, MillingDirection.Mixed, 2500.0, 0.8), (op.StepOver.Value, op.MillingDirection.Value, op.ChordFeedrate.Value, op.MinStepover.Value));
        Generate(doc, op);
    }

    [Fact]
    public void The_plugin_adds_both_operations_to_the_machining_menu()
    {
        var manager = new PluginManager();
        var info = PluginManager.Inspect(typeof(Plugin).Assembly.Location)!;
        Assert.Equal("Trochoidal Milling (matmill)", info.Name);
        manager.Plugins.Add(info);

        manager.Load(info, new Version(1, 0), _ => { });

        Assert.Equal(PluginState.Loaded, info.State);
        Assert.Equal(["Trochoidal Pocket (matmill)", "Trochoidal Profile (matmill)"], manager.Operations.Select(o => o.DisplayType).Order());
    }

    [Fact]
    public void The_license_of_matmill_goes_with_the_plugin()
    {
        var license = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "LICENSE-matmill.txt");
        Assert.True(File.Exists(license));
        Assert.Contains("Copyright (c) 2016 jkmnt", File.ReadAllText(license));
    }
}
