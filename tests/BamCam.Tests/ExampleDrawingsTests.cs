using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;
using MatmillPlugin;

namespace BamCam.Tests;

/// <summary>
/// The example drawings in <c>examples/</c>, one per kind of operation, each showing as many of its options as fit in
/// one drawing. Everything in them is made here (shapes, texts, the G-code of the NC file), so they carry no
/// third-party content. Set BAMCAM_WRITE_EXAMPLES=1 to write them again; the test checks that every operation in
/// them makes its toolpath without a warning. The 3D profile example is <see cref="PebbleExampleTests"/>.
/// </summary>
[Collection(GlobalLibrariesCollection.Name)]
public class ExampleDrawingsTests
{
    private static readonly string Dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples"));

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

    public ExampleDrawingsTests() => new Plugin().Initialize(new Host());

    // ---------------------------------------------------------------- helpers

    private static CadDocument New(string name, Point3 stockMin, Point3 stockMax, string material = "Aluminium")
    {
        var doc = CadDocument.CreateNew();
        doc.Name = name;
        doc.Machining.Stock = new Stock { PMin = stockMin, PMax = stockMax, Material = material };
        doc.ActivePart.Name = name;
        return doc;
    }

    private static int Add(CadDocument doc, Entity e) => doc.AddEntity(e).Id;

    private static int Poly(CadDocument doc, bool closed, params Point2[] points) => Add(doc, new PolylineEntity(Path2.FromPoints(points, closed)));

    private static int Circle(CadDocument doc, double x, double y, double diameter) =>
        Add(doc, new CircleEntity { Center = new Point3(x, y, 0), Diameter = diameter });

    private static int Rect(CadDocument doc, double x, double y, double w, double h, double corner = 0) =>
        Add(doc, new RectEntity { LowerLeft = new Point3(x, y, 0), Width = w, Height = h, CornerRadius = corner });

    private static int Text(CadDocument doc, string text, double x, double y, double height, bool bold = false) =>
        Add(doc, new TextEntity { Text = text, Position = new Point3(x, y, 0), Height = height, Bold = bold });

    /// <summary>A star with sharp inner corners.</summary>
    private static int Star(CadDocument doc, double cx, double cy, double outer, double inner, int points = 5) =>
        Poly(doc, true, Enumerable.Range(0, 2 * points).Select(k =>
        {
            var a = Math.PI / 2 + Math.PI * k / points;
            var r = k % 2 == 0 ? outer : inner;
            return new Point2(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }).ToArray());

    private static T Op<T>(CadDocument doc, string name, double tool, IEnumerable<int> objects, Action<T>? set = null) where T : MachineOp, new()
    {
        var op = new T { Name = name };
        op.PrimitiveIds.AddRange(objects);
        op.ToolDiameter.Set(tool);
        op.ClearancePlane.Set(10);
        op.StockSurface.Set(0);
        op.SpindleSpeed.Set(18000);
        op.CutFeedrate.Set(800);
        op.PlungeFeedrate.Set(200);
        set?.Invoke(op);
        doc.ActivePart.Operations.Add(op);
        return op;
    }

    private static void Depth(DepthOp op, double target, double increment)
    {
        op.TargetDepth.Set(target);
        op.DepthIncrement.Set(increment);
    }

    // ---------------------------------------------------------------- the examples

    /// <summary>Profile: outside with holding tabs and a tangent lead-in, roughing and finishing, inside with corner overcut, a wide slot, a chamfer.</summary>
    private static CadDocument Profile()
    {
        var doc = New("Profile", new Point3(-5, -5, -6), new Point3(125, 85, 0), "Plywood");
        var plate = Rect(doc, 0, 0, 120, 80, 8);
        var star = Star(doc, 30, 40, 18, 8);
        var slot = Poly(doc, false, new(60, 15), new(75, 25), new(90, 20), new(105, 30));
        var hole = Circle(doc, 90, 58, 20);

        Op<ProfileOp>(doc, "Outline roughing (tabs, tangent lead-in)", 6, [plate], op =>
        {
            Depth(op, -6, 2);
            op.InsideOutside.Set(InsideOutside.Outside);
            op.RoughingClearance.Set(0.3);
            op.HoldingTabs.Set(new HoldingTabSettings(TabMethod.Automatic, 6, 1.5, 4, 4, 0, 0, false, TabStyle.Triangle));
            op.LeadInMove.Set(new LeadMove(LeadMoveType.Tangent, 0, 4));
        });
        Op<ProfileOp>(doc, "Outline finishing (climb, one pass)", 6, [plate], op =>
        {
            Depth(op, -6, 6);
            op.InsideOutside.Set(InsideOutside.Outside);
            op.MillingDirection.Set(MillingDirection.Climb);
            op.HoldingTabs.Set(new HoldingTabSettings(TabMethod.Automatic, 6, 1.5, 4, 4, 0, 0, false, TabStyle.Triangle));
            op.CutFeedrate.Set(1200);
        });
        Op<ProfileOp>(doc, "Star inside (corner overcut, spiral lead-in)", 3, [star], op =>
        {
            Depth(op, -6, 1.5);
            op.InsideOutside.Set(InsideOutside.Inside);
            op.CornerOvercut.Set(true);
            op.LeadInMove.Set(new LeadMove(LeadMoveType.Spiral, 5));
        });
        Op<ProfileOp>(doc, "Slot along an open line (cut width 10)", 6, [slot], op =>
        {
            Depth(op, -3, 1.5);
            op.CutWidth.Set(10);
            op.StepOver.Set(0.5);
        });
        Op<ProfileOp>(doc, "Hole with a 30 degree chamfer (side profile)", 6, [hole], op =>
        {
            Depth(op, -6, 1);
            op.InsideOutside.Set(InsideOutside.Inside);
            op.SideProfile.Set(new SideProfile(SideProfileMethod.Slope, 30));
        });
        return doc;
    }

    /// <summary>Pocket: islands with a spiral lead-in and finish stepover, hatching, several pockets from a start point, inside offsets.</summary>
    private static CadDocument Pocket()
    {
        var doc = New("Pocket", new Point3(-5, -5, -10), new Point3(165, 105, 0));
        var outer = Rect(doc, 0, 0, 90, 60, 6);
        var round = Circle(doc, 25, 30, 16);
        var block = Rect(doc, 55, 20, 20, 20, 3);
        var oval = Poly(doc, true, Enumerable.Range(0, 48).Select(k => new Point2(125 + 25 * Math.Cos(k * Math.PI / 24), 30 + 15 * Math.Sin(k * Math.PI / 24))).ToArray());
        var small = new[] { Circle(doc, 15, 85, 14), Circle(doc, 45, 85, 14), Circle(doc, 75, 85, 14) };
        var frame = Rect(doc, 100, 65, 50, 35, 4);
        var core = Rect(doc, 115, 75, 20, 15);

        Op<PocketOp>(doc, "Pocket round two islands (spiral lead-in, finish stepover)", 6, [outer, round, block], op =>
        {
            Depth(op, -8, 2);
            op.StepOver.Set(0.45);
            op.FinishStepover.Set(0.1);
            op.FinishStepoverAtTargetDepth.Set(true);
            op.MillingDirection.Set(MillingDirection.Climb);
            op.LeadInMove.Set(new LeadMove(LeadMoveType.Spiral, 3));
        });
        Op<PocketOp>(doc, "Oval, horizontal hatching", 6, [oval], op =>
        {
            Depth(op, -4, 2);
            op.RegionFillStyle.Set(RegionFillStyle.HorizontalHatch);
        });
        Op<PocketOp>(doc, "Three pockets, nearest to the start point first", 6, small, op =>
        {
            Depth(op, -5, 2.5);
            op.StartPoint.Set(new OptionalPoint(new Point3(90, 85, 0)));
        });
        Op<PocketOp>(doc, "Frame round a core, inside offsets", 4, [frame, core], op =>
        {
            Depth(op, -3, 1.5);
            op.RegionFillStyle.Set(RegionFillStyle.InsideOffsets);
            op.StepOver.Set(0.5);
        });
        return doc;
    }

    /// <summary>Engrave: text and an open decorative curve.</summary>
    private static CadDocument Engrave()
    {
        var doc = New("Engrave", new Point3(-5, -5, -5), new Point3(125, 55, 0), "Brass");
        var text = Text(doc, "BamCam", 10, 25, 18);
        var wave = Poly(doc, false, Enumerable.Range(0, 61).Select(k => new Point2(5 + 110.0 * k / 60, 10 + 3 * Math.Sin(k * Math.PI / 6))).ToArray());
        Op<EngraveOp>(doc, "Text", 1, [text], op => Depth(op, -0.3, 0.3));
        Op<EngraveOp>(doc, "Wave line, two passes", 1, [wave], op => Depth(op, -0.6, 0.3));
        return doc;
    }

    /// <summary>V-engrave: lettering and a star with a flat bottom, cleared with the flat stepover.</summary>
    private static CadDocument VEngrave()
    {
        var doc = New("V-Engrave", new Point3(-5, -5, -10), new Point3(165, 65, 0), "Oak");
        var text = Text(doc, "BamCam", 5, 15, 20, bold: true);
        var star = Star(doc, 140, 30, 22, 9);
        Op<VEngraveOp>(doc, "Lettering, 60 degree bit", 6, [text], op =>
        {
            op.TargetDepth.Set(-3);
            op.VAngle.Set(60);
            op.Resolution.Set(0.1);
        });
        Op<VEngraveOp>(doc, "Star, flat bottom at 2 mm cleared", 6, [star], op =>
        {
            op.TargetDepth.Set(-2);
            op.VAngle.Set(90);
            op.TipDiameter.Set(0.2);
            op.ClearFlatAreas.Set(true);
            op.FlatStepover.Set(0.3);
        });
        return doc;
    }

    /// <summary>Drilling: canned cycle with pecking, a dwell at the bottom, spiral milled holes both ways.</summary>
    private static CadDocument Drilling()
    {
        var doc = New("Drilling", new Point3(-10, -10, -15), new Point3(110, 60, 0));
        var points = new PointListEntity();
        for (var k = 0; k < 5; k++) points.Points.Add(new Point3(10 + 20 * k, 10, 0));
        var row = Add(doc, points);
        var spots = new PointListEntity();
        for (var k = 0; k < 5; k++) spots.Points.Add(new Point3(10 + 20 * k, 25, 0));
        var spotRow = Add(doc, spots);
        var big = new[] { Circle(doc, 20, 45, 12), Circle(doc, 50, 45, 12) };
        var counterbore = new[] { Circle(doc, 80, 45, 16) };

        Op<DrillOp>(doc, "Peck drilling 5 mm (G83)", 5, [row], op =>
        {
            Depth(op, -12, 12);
            op.PeckDistance.Set(3);
            op.RetractHeight.Set(2);
            op.CutFeedrate.Set(150);
        });
        Op<DrillOp>(doc, "Spot drilling with a dwell (G82)", 6, [spotRow], op =>
        {
            Depth(op, -1.5, 1.5);
            op.Dwell.Set(0.5);
            op.CutFeedrate.Set(150);
        });
        Op<DrillOp>(doc, "12 mm holes, spiral milled clockwise, flat base", 6, big, op =>
        {
            Depth(op, -10, 10);
            op.DrillingMethod.Set(DrillingMethod.SpiralMill_CW);
            op.HoleDiameter.Set(12);
            op.SpiralFlatBase.Set(true);
            op.DrillLeadOut.Set(true);
            op.LeadOutLength.Set(1);
        });
        Op<DrillOp>(doc, "16 mm counterbore, spiral milled counter-clockwise", 6, counterbore, op =>
        {
            Depth(op, -4, 4);
            op.DrillingMethod.Set(DrillingMethod.SpiralMill_CCW);
            op.HoleDiameter.Set(16);
        });
        return doc;
    }

    /// <summary>Thread milling inside and outside, right and left hand, and tapping.</summary>
    private static CadDocument Threads()
    {
        var doc = New("Thread milling and tapping", new Point3(-10, -10, -20), new Point3(110, 40, 0));
        var core = Circle(doc, 15, 15, 8.5);
        var leftCore = Circle(doc, 40, 15, 10.2);
        var boss = Circle(doc, 70, 15, 16);
        var tapHoles = new PointListEntity();
        tapHoles.Points.Add(new Point3(95, 8, 0));
        tapHoles.Points.Add(new Point3(95, 22, 0));
        var taps = Add(doc, tapHoles);

        Op<ThreadMillOp>(doc, "M10 internal, bottom up", 6, [core], op =>
        {
            op.TargetDepth.Set(-12);
            op.ThreadType.Set(ThreadType.Internal);
            op.ThreadDiameter.Set(10);
            op.Pitch.Set(1.5);
            op.CutDirection.Set(ThreadMillDirection.BottomUp);
            op.SpindleSpeed.Set(8000);
        });
        Op<ThreadMillOp>(doc, "M12 left hand, top down, two radial passes", 8, [leftCore], op =>
        {
            op.TargetDepth.Set(-10);
            op.ThreadType.Set(ThreadType.Internal);
            op.ThreadDiameter.Set(12);
            op.Pitch.Set(1.75);
            op.ThreadHand.Set(ThreadHand.Left);
            op.CutDirection.Set(ThreadMillDirection.TopDown);
            op.RadialPasses.Set(2);
            op.SpindleSpeed.Set(8000);
        });
        Op<ThreadMillOp>(doc, "M16 external thread on a boss", 8, [boss], op =>
        {
            op.TargetDepth.Set(-8);
            op.ThreadType.Set(ThreadType.External);
            op.ThreadDiameter.Set(16);
            op.Pitch.Set(2);
            op.SpindleSpeed.Set(8000);
        });
        Op<TapOp>(doc, "M6 tapping (rigid, G84)", 6, [taps], op =>
        {
            op.TargetDepth.Set(-10);
            op.Pitch.Set(1);
            op.RetractHeight.Set(3);
            op.SpindleSpeed.Set(300);
        });
        return doc;
    }

    /// <summary>Drag knife: lettering, a star with sharp corners and a circle, with knife offset, swivel and overcut.</summary>
    private static CadDocument DragKnife()
    {
        var doc = New("Drag knife", new Point3(-5, -5, -0.5), new Point3(165, 55, 0), "Vinyl");
        var text = Text(doc, "BamCam", 5, 10, 30, bold: true);
        var star = Star(doc, 130, 25, 20, 8);
        var ring = Circle(doc, 130, 25, 50);
        Op<DragKnifeOp>(doc, "Lettering", 0, [text], op => SetKnife(op));
        Op<DragKnifeOp>(doc, "Star and ring", 0, [star, ring], op =>
        {
            SetKnife(op);
            op.SwivelAngle.Set(5);
            op.Overcut.Set(1);
        });
        return doc;

        static void SetKnife(DragKnifeOp op)
        {
            op.TargetDepth.Set(-0.1);
            op.DepthIncrement.Set(0.1);
            op.KnifeOffset.Set(0.25);
            op.SpindleSpeed.Set(0);
            op.SpindleDirection.Set(SpindleDirection.Off);
            op.CutFeedrate.Set(1500);
        }
    }

    /// <summary>Plasma: a bracket with holes, slots and a small hole, arc and line leads, a start point per shape.</summary>
    private static CadDocument Plasma()
    {
        var doc = New("Plasma", new Point3(-10, -10, -3), new Point3(170, 90, 0), "Mild steel");
        doc.Machining.PostProcessor = "GRBL Plasma";
        var outline = Poly(doc, true, new(0, 0), new(160, 0), new(160, 30), new(60, 80), new(0, 80));
        var holes = new[] { Circle(doc, 20, 20, 16), Circle(doc, 20, 60, 16), Circle(doc, 130, 15, 10) };
        var slot = Rect(doc, 60, 10, 40, 10, 5);
        var tiny = Circle(doc, 45, 40, 5);
        var plasma = Op<PlasmaOp>(doc, "Bracket (arc leads, probing, THC)", 0, [outline, .. holes, slot, tiny], op =>
        {
            op.SpindleSpeed.Set(0);
            op.SpindleDirection.Set(SpindleDirection.Off);
            // Entered for 3 mm mild steel at 45 A, as no cut data is chosen.
            op.KerfWidth.Set(1.5);
            op.CutFeedrate.Set(2200);
            op.PierceHeight.Set(3.8);
            op.PierceDelay.Set(0.4);
            op.CutHeight.Set(1.5);
            op.HeightControl.Set(TorchHeightControl.Probe);
            op.UseThc.Set(true);
            op.LeadIn.Set(PlasmaLead.Arc);
            op.LeadInLength.Set(5);
            op.LeadOut.Set(PlasmaLead.Line);
            op.LeadOutLength.Set(2);
            op.Overcut.Set(1);
            op.SmallHoleSize.Set(8);
            op.SmallHoleFeedrate.Set(1200);
        });
        // The outline starts at the bottom edge, the slot at its left end.
        plasma.WriteShapeStarts([(outline, new Point2(100, 0)), (slot, new Point2(60, 15))]);
        return doc;
    }

    /// <summary>Turning: facing, roughing with constant surface speed, a relief groove, a thread, a centre hole and boring.</summary>
    private static CadDocument Turning()
    {
        // Lathe drawings: X along the spindle axis (Z of the lathe, 0 at the face), Y = minus the radius.
        var doc = New("Turning", new Point3(-62, -15, -15), new Point3(2, 15, 15), "Steel");
        doc.Machining.PostProcessor = "LinuxCNC Turn";
        doc.Machining.FastPlungeHeight = 0.2;
        var face = Poly(doc, false, new(0, 0), new(0, -15));
        var outline = Poly(doc, false, new(0, -7), new(-1, -8), new(-22, -8), new(-22, -11), new(-34, -11), new(-37, -14), new(-56, -14), new(-56, -15));
        var groove = Poly(doc, false, new(-22, -8), new(-22, -6.5), new(-18, -6.5), new(-18, -8));
        var thread = Poly(doc, false, new(-1, -8), new(-18, -8));
        var axis = Poly(doc, false, new(0, 0), new(-20, 0));
        var bore = Poly(doc, false, new(0, -6.5), new(-12, -6.5), new(-12, -5.5), new(-18, -5.5));

        TurningOp Lathe<T>(string name, double tool, int obj, Action<T> set) where T : TurningOp, new()
        {
            var op = Op<T>(doc, name, tool, [obj], o =>
            {
                o.StockSurface.Set(15);
                o.ClearancePlane.Set(2);
                o.SpindleSpeed.Set(1200);
                o.CutFeedrate.Set(0.15 * 1200);
                o.PlungeFeedrate.Set(0.05 * 1200);
            });
            set(op);
            return op;
        }
        Lathe<LatheFacingOp>("Facing", 0.8, face, op =>
        {
            op.DepthIncrement.Set(0.5);
            op.SurfaceSpeed.Set(180);
            op.MaxSpindleSpeed.Set(3000);
        });
        Lathe<LatheOp>("Roughing at 180 m/min (G96)", 0.8, outline, op =>
        {
            op.DepthIncrement.Set(1);
            op.RoughingClearance.Set(0.2);
            op.SurfaceSpeed.Set(180);
            op.MaxSpindleSpeed.Set(3000);
            op.LatheLeadInLength.Set(1);
        });
        Lathe<LatheOp>("Finishing", 0.4, outline, op =>
        {
            op.DepthIncrement.Set(2);
            op.SurfaceSpeed.Set(220);
            op.MaxSpindleSpeed.Set(3500);
            op.NoseCompensation.Set(NoseCompensation.Computed);
        });
        Lathe<LatheGrooveOp>("Relief groove, 3 mm blade", 3, groove, op =>
        {
            op.DepthIncrement.Set(1);
            op.PeckRetract.Set(0.3);
            op.StepOver.Set(0.8);
            op.FinishPass.Set(true);
            op.SurfaceSpeed.Set(80);
            op.MaxSpindleSpeed.Set(1500);
        });
        Lathe<LatheThreadOp>("Thread M16 x 2", 0.2, thread, op =>
        {
            op.Pitch.Set(2);
            op.ThreadType.Set(ThreadType.External);
            op.FirstCutDepth.Set(0.3);
            op.SpringPasses.Set(2);
            op.SpindleSpeed.Set(400);
        });
        Lathe<LatheDrillOp>("Drilling 10 mm, pecking", 10, axis, op =>
        {
            op.PeckDistance.Set(4);
            op.SpindleSpeed.Set(800);
            op.CutFeedrate.Set(0.1 * 800);
        });
        Lathe<LatheOp>("Boring to 13 mm diameter (internal)", 0.4, bore, op =>
        {
            op.TurningSide.Set(TurningSide.Internal);
            op.StockSurface.Set(5); // the radius of the drilled hole
            op.ClearancePlane.Set(4);
            op.DepthIncrement.Set(0.5);
            op.SpindleSpeed.Set(1500);
        });
        return doc;
    }

    /// <summary>Trochoidal pocket and profile (matmill plugin): a pocket with an island, slots outside and inside, inside corners cleared.</summary>
    private static CadDocument Trochoidal()
    {
        var doc = New("Trochoidal milling (matmill)", new Point3(-25, -25, -12), new Point3(165, 85, 0));
        var pocket = Rect(doc, 0, 0, 70, 60, 4);
        var island = Circle(doc, 35, 30, 20);
        var part = Rect(doc, 95, 5, 50, 50, 0);
        var window = Rect(doc, 110, 20, 20, 20, 0);

        Op<TrochoPocketOp>(doc, "Pocket round an island (mixed direction, spiral lead-in)", 6, [pocket, island], op =>
        {
            Depth(op, -10, 5);
            op.StepOver.Set(0.15);
            op.MillingDirection.Set(MillingDirection.Mixed);
            op.LeadInMove.Set(new LeadMove(LeadMoveType.Spiral, 3));
            op.ChordFeedrate.Set(2000);
            op.SpiralFeedrate.Set(500);
        });
        Op<TrochoProfileOp>(doc, "Outside slot, smooth chords", 6, [part], op =>
        {
            Depth(op, -10, 10);
            op.InsideOutside.Set(InsideOutside.Outside);
            op.CutWidth.Set(12);
            op.StepOver.Set(0.2);
            op.MillingDirection.Set(MillingDirection.Climb);
            op.SmoothChords.Set(true);
        });
        Op<TrochoProfileOp>(doc, "Inside, inside corners cleared", 6, [window], op =>
        {
            Depth(op, -10, 10);
            op.InsideOutside.Set(InsideOutside.Inside);
            op.CutWidth.Set(8);
            op.StepOver.Set(0.2);
            op.ClearInsideCorners.Set(true);
        });
        return doc;
    }

    /// <summary>The G-code of the NC file example: a spiral, written here.</summary>
    private static string SpiralGCode()
    {
        var lines = new List<string> { "(Spiral, made by BamCam's tests)", "G21 G90", "G0 Z5", "G0 X0 Y0", "G1 Z-0.5 F200" };
        for (var k = 1; k <= 120; k++)
        {
            var a = k * Math.PI / 12;
            var r = 0.25 * k;
            lines.Add(FormattableString.Invariant($"G1 X{r * Math.Cos(a):0.###} Y{r * Math.Sin(a):0.###} F800"));
        }
        lines.Add("G0 Z5");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>NC file: G-code from elsewhere put into the job between BamCam's own operations.</summary>
    private static CadDocument NcFile()
    {
        var doc = New("NC file", new Point3(-40, -40, -5), new Point3(40, 40, 0), "MDF");
        var border = Rect(doc, -35, -35, 70, 70, 5);
        Op<NcFileOp>(doc, "Spiral from a G-code file", 3, [], op => op.SourceFile.Set("NC file (spiral).nc"));
        Op<ProfileOp>(doc, "Border, cut out afterwards", 3, [border], op =>
        {
            Depth(op, -5, 2.5);
            op.InsideOutside.Set(InsideOutside.Outside);
        });
        return doc;
    }

    /// <summary>
    /// Sketches: a mounting plate whose size, corner radius and holes are driving dimensions of variables. The outline
    /// and the holes are two sketches, as an outside and an inside profile cut them.
    /// </summary>
    private static CadDocument Sketches()
    {
        var doc = New("Sketch (mounting plate)", new Point3(-5, -5, -6), new Point3(105, 65, 0), "Aluminium");
        List<BamCam.Core.Sketching.SketchVariable> Vars() =>
        [
            new() { Name = "Width", Expression = "100" }, new() { Name = "Height", Expression = "60" },
            new() { Name = "Corner", Expression = "8" }, new() { Name = "Edge", Expression = "10" },
            new() { Name = "Hole", Expression = "6.5" }, new() { Name = "Bore", Expression = "Height / 2" },
        ];

        // The outline: a rectangle on the origin with rounded corners.
        var plate = BamCam.Core.Sketching.Sketch.CreateNew();
        plate.Variables = Vars();
        Point2[] c = [new(0, 0), new(100, 0), new(100, 60), new(0, 60)];
        var l = new BamCam.Core.Sketching.SketchGeometry[4];
        for (var i = 0; i < 4; i++) l[i] = plate.AddLine(c[i], c[(i + 1) % 4]);
        for (var i = 0; i < 4; i++) plate.AddConstraint(BamCam.Core.Sketching.ConstraintKind.Coincident, [l[i].Points[1], l[(i + 1) % 4].Points[0]]);
        plate.AddConstraint(BamCam.Core.Sketching.ConstraintKind.Horizontal, [l[0].Id]);
        plate.AddConstraint(BamCam.Core.Sketching.ConstraintKind.Horizontal, [l[2].Id]);
        plate.AddConstraint(BamCam.Core.Sketching.ConstraintKind.Vertical, [l[1].Id]);
        plate.AddConstraint(BamCam.Core.Sketching.ConstraintKind.Vertical, [l[3].Id]);
        plate.AddConstraint(BamCam.Core.Sketching.ConstraintKind.Coincident, [l[0].Points[0], BamCam.Core.Sketching.Sketch.OriginId]);
        var w = plate.AddConstraint(BamCam.Core.Sketching.ConstraintKind.Distance, [l[0].Id], expression: "Width");
        (w.LabelX, w.LabelY) = (0, -9);
        var h = plate.AddConstraint(BamCam.Core.Sketching.ConstraintKind.Distance, [l[1].Id], expression: "Height");
        (h.LabelX, h.LabelY) = (10, 0);
        BamCam.Core.Sketching.SketchSolver.Solve(plate);
        var round = BamCam.Core.Sketching.SketchCommands.RoundCorners(plate, l.Select(x => x.Points[0]).ToList(), "Corner", chamfer: false, out _);
        BamCam.Core.Sketching.SketchCommands.Settle(plate, round, out _);
        var plateSketch = new SketchEntity();
        plateSketch.SetSketch(plate);
        var plateId = Add(doc, plateSketch);

        // The holes: four in the corners, Edge from the sides, and a bore in the middle.
        var holes = BamCam.Core.Sketching.Sketch.CreateNew();
        holes.Variables = Vars();
        var first = (BamCam.Core.Sketching.SketchGeometry?)null;
        foreach (var (x, y, ex, ey) in new[] { (10.0, 10.0, "Edge", "Edge"), (90.0, 10.0, "Width - Edge", "Edge"), (90.0, 50.0, "Width - Edge", "Height - Edge"), (10.0, 50.0, "Edge", "Height - Edge") })
        {
            var hole = holes.AddCircle(new(x, y), 3.25);
            var dx = holes.AddConstraint(BamCam.Core.Sketching.ConstraintKind.HorizontalDistance, [BamCam.Core.Sketching.Sketch.OriginId, hole.Points[0]], [1], ex);
            var dy = holes.AddConstraint(BamCam.Core.Sketching.ConstraintKind.VerticalDistance, [BamCam.Core.Sketching.Sketch.OriginId, hole.Points[0]], [1], ey);
            (dx.LabelX, dx.LabelY) = (0, y < 30 ? -y / 2 - 6 : 66 - y / 2);
            (dy.LabelX, dy.LabelY) = (x < 50 ? -x / 2 - 6 : 106 - x / 2, 0);
            if (first == null)
            {
                var d = holes.AddConstraint(BamCam.Core.Sketching.ConstraintKind.Diameter, [hole.Id], expression: "Hole");
                (d.LabelX, d.LabelY) = (16, 8);
                first = hole;
            }
            else
            {
                holes.AddConstraint(BamCam.Core.Sketching.ConstraintKind.Equal, [first.Id, hole.Id]);
            }
        }
        var bore = holes.AddCircle(new(50, 30), 15);
        var bx = holes.AddConstraint(BamCam.Core.Sketching.ConstraintKind.HorizontalDistance, [BamCam.Core.Sketching.Sketch.OriginId, bore.Points[0]], [1], "Width / 2");
        var by = holes.AddConstraint(BamCam.Core.Sketching.ConstraintKind.VerticalDistance, [BamCam.Core.Sketching.Sketch.OriginId, bore.Points[0]], [1], "Height / 2");
        // The bore's dimensions a step further out than the holes'.
        (bx.LabelX, bx.LabelY) = (0, -31);
        (by.LabelX, by.LabelY) = (-45, 0);
        var bd = holes.AddConstraint(BamCam.Core.Sketching.ConstraintKind.Diameter, [bore.Id], expression: "Bore");
        (bd.LabelX, bd.LabelY) = (18, 14);
        BamCam.Core.Sketching.SketchSolver.Solve(holes);
        var holesSketch = new SketchEntity();
        holesSketch.SetSketch(holes);
        var holesId = Add(doc, holesSketch);

        Op<ProfileOp>(doc, "Holes and bore (inside)", 3, [holesId], op =>
        {
            Depth(op, -6, 2);
            op.InsideOutside.Set(InsideOutside.Inside);
            op.LeadInMove.Set(new LeadMove(LeadMoveType.Spiral, 5));
        });
        Op<ProfileOp>(doc, "Outline (outside, tabs)", 6, [plateId], op =>
        {
            Depth(op, -6, 2);
            op.InsideOutside.Set(InsideOutside.Outside);
            op.HoldingTabs.Set(new HoldingTabSettings(TabMethod.Automatic, 6, 1.5, 4, 4, 0, 0, false, TabStyle.Triangle));
        });
        return doc;
    }

    [Fact]
    public void Sketch_example_is_fully_constrained_and_follows_its_variables()
    {
        var doc = Sketches();
        var sketches = doc.AllEntities().OfType<SketchEntity>().ToList();
        Assert.Equal(2, sketches.Count);
        foreach (var s in sketches)
            Assert.Equal(BamCam.Core.Sketching.SketchStatus.FullyConstrained, BamCam.Core.Sketching.SketchSolver.Solve(s.GetSketch()).Status);
        var holes = sketches[1].GetSketch();
        holes.Variables.Single(v => v.Name == "Width").Expression = "120";
        Assert.True(BamCam.Core.Sketching.SketchSolver.Solve(holes).Solved);
        Assert.Equal(110, holes.Geometry.Where(g => g.Kind == BamCam.Core.Sketching.SketchGeometryKind.Circle).Max(g => holes.P(g.Points[0]).X), 6);
    }

    public static IEnumerable<object[]> Examples() =>
    [
        ["Profile"], ["Pocket"], ["Engrave"], ["V-Engrave"], ["Drilling"], ["Thread milling and tapping"], ["Drag knife"], ["Plasma"],
        ["Turning"], ["Trochoidal milling (matmill)"], ["NC file"], ["Sketch (mounting plate)"],
    ];

    private static CadDocument Build(string name) => name switch
    {
        "Profile" => Profile(),
        "Pocket" => Pocket(),
        "Engrave" => Engrave(),
        "V-Engrave" => VEngrave(),
        "Drilling" => Drilling(),
        "Thread milling and tapping" => Threads(),
        "Drag knife" => DragKnife(),
        "Plasma" => Plasma(),
        "Turning" => Turning(),
        "Trochoidal milling (matmill)" => Trochoidal(),
        "NC file" => NcFile(),
        "Sketch (mounting plate)" => Sketches(),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Examples))]
    public void The_example_cuts_with_every_operation(string name)
    {
        var file = Path.Combine(Dir, name + ".cb");
        if (Environment.GetEnvironmentVariable("BAMCAM_WRITE_EXAMPLES") == "1")
        {
            Directory.CreateDirectory(Dir);
            CbWriter.Save(Build(name), file);
            if (name == "NC file") File.WriteAllText(Path.Combine(Dir, "NC file (spiral).nc"), SpiralGCode());
        }
        Assert.True(File.Exists(file), file);
        var doc = CbReader.Load(file);
        var ops = doc.Parts.SelectMany(p => p.Operations).ToList();
        Assert.NotEmpty(ops);
        foreach (var op in ops)
        {
            // Text outlines come from the fonts of the system, which only the user interface reads: operations on text
            // alone are checked in BamCam itself (--generate), not here.
            if (TextEntity.OutlineProvider == null && op.PrimitiveIds.Count > 0 && op.PrimitiveIds.All(id => doc.FindEntity(id) is TextEntity)) continue;
            var tp = ToolpathGenerator.Generate(doc, op);
            Assert.True(tp.Warnings.Count == 0, $"{name} / {op.Name}: {string.Join(" | ", tp.Warnings)}");
            // Thread turning and tapping are single cycles: a few moves.
            Assert.True(tp.Moves.Count > 1, $"{name} / {op.Name}: {tp.Moves.Count} moves");
        }
    }
}
