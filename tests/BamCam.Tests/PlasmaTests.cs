using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Post;
using BamCam.IO.Cb;

namespace BamCam.Tests;

public class PlasmaTests
{
    /// <summary>A 40 x 40 plate with a 10 x 10 hole in the middle (hole drawn first).</summary>
    private static (CadDocument Doc, PlasmaOp Op) Plate(Action<PlasmaOp>? setup = null)
    {
        var doc = new CadDocument();
        var hole = new PolylineEntity(Path2.FromPoints([new(15, 15), new(25, 15), new(25, 25), new(15, 25)], closed: true));
        var outline = new PolylineEntity(Path2.FromPoints([new(0, 0), new(40, 0), new(40, 40), new(0, 40)], closed: true));
        doc.AddEntity(outline);
        doc.AddEntity(hole);
        var op = new PlasmaOp { Name = "plasma" };
        op.PrimitiveIds.Add(outline.Id);
        op.PrimitiveIds.Add(hole.Id);
        op.KerfWidth.Set(2);
        op.Overcut.Set(0);
        setup?.Invoke(op);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    /// <summary>Cutting moves at cut height between each torch on and off.</summary>
    private static List<List<Move>> Cuts(Toolpath tp)
    {
        var cuts = new List<List<Move>>();
        List<Move>? current = null;
        foreach (var m in tp.Moves)
        {
            if (m is { Kind: MoveKind.Torch, Torch: TorchAction.On }) cuts.Add(current = []);
            else if (m is { Kind: MoveKind.Torch, Torch: TorchAction.Off }) current = null;
            else if (current != null && m.IsMotion) current.Add(m);
        }
        return cuts;
    }

    [Fact]
    public void Hole_is_cut_first_inside_and_the_outline_outside()
    {
        var (doc, op) = Plate(o => o.LeadIn.Set(PlasmaLead.None));
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var cuts = Cuts(tp);
        Assert.Equal(2, cuts.Count);
        // Hole first, 1 (half kerf) inside: 16..24.
        var hole = cuts[0].Select(m => m.To.XY).ToList();
        Assert.All(hole, p => Assert.InRange(p.X, 15.99, 24.01));
        Assert.Contains(hole, p => Math.Abs(p.X - 24) < 1e-6);
        // Outline 1 outside: -1..41 (round corners).
        var outline = cuts[1].Select(m => m.To.XY).ToList();
        Assert.Contains(outline, p => Math.Abs(p.X - 41) < 1e-6);
        Assert.All(outline, p => Assert.InRange(p.X, -1.01, 41.01));
        Assert.Contains(cuts[1], m => m.IsArc);
        // Cut height 1.5 above the stock surface.
        Assert.All(cuts.SelectMany(c => c).Skip(1), m => Assert.Equal(1.5, m.To.Z, 9));
    }

    [Fact]
    public void Outline_runs_clockwise_and_holes_counter_clockwise()
    {
        var (doc, op) = Plate(o => o.LeadIn.Set(PlasmaLead.None));
        var cuts = Cuts(ToolpathGenerator.Generate(doc, op));
        double Area(List<Move> c) => Path2.FromPoints(c.Select(m => m.To.XY), closed: true).SignedArea();
        Assert.True(Area(cuts[0]) > 0, "hole counter-clockwise");
        Assert.True(Area(cuts[1]) < 0, "outline clockwise");
    }

    [Fact]
    public void Arc_lead_in_pierces_in_the_scrap()
    {
        var (doc, op) = Plate(o => o.LeadInLength.Set(3));
        var tp = ToolpathGenerator.Generate(doc, op);
        var outer = Path2.FromPoints([new(0, 0), new(40, 0), new(40, 40), new(0, 40)], closed: true);
        var hole = Path2.FromPoints([new(15, 15), new(25, 15), new(25, 25), new(15, 25)], closed: true);
        var pierces = tp.Moves.Select((m, i) => (m, i)).Where(x => x.m is { Kind: MoveKind.Torch, Torch: TorchAction.On })
            .Select(x => tp.Moves.Take(x.i).Last(m => m.IsMotion).To.XY).ToList();
        Assert.Equal(2, pierces.Count);
        Assert.True(hole.Contains(pierces[0]) && hole.DistanceTo(pierces[0]) > 1, $"hole pierce {pierces[0]}");
        Assert.False(outer.Contains(pierces[1]), $"outline pierce {pierces[1]}");
        Assert.True(outer.DistanceTo(pierces[1]) > 1);
        // The lead-in is an arc onto the cut.
        Assert.True(Cuts(tp).All(c => c[1].IsArc));
    }

    [Fact]
    public void Probe_or_fixed_heights_before_each_pierce()
    {
        var (doc, op) = Plate(o => o.SwitchOffset.Set(1.2));
        var tp = ToolpathGenerator.Generate(doc, op);
        var probes = tp.Moves.Where(m => m is { Kind: MoveKind.Torch, Torch: TorchAction.Probe }).ToList();
        Assert.Equal(2, probes.Count);
        Assert.All(probes, p => Assert.Equal(-1.2, p.ProbeZ, 9));
        Assert.Equal(2, tp.Moves.Count(m => m is { Kind: MoveKind.Torch, Torch: TorchAction.ThcOn }));

        op.HeightControl.Set(TorchHeightControl.FixedHeights);
        op.UseThc.Set(false);
        tp = ToolpathGenerator.Generate(doc, op);
        Assert.DoesNotContain(tp.Moves, m => m is { Kind: MoveKind.Torch, Torch: TorchAction.Probe or TorchAction.ThcOn });
        // Pierce at pierce height, then down to cut height.
        var on = tp.Moves.FindIndex(m => m is { Kind: MoveKind.Torch, Torch: TorchAction.On });
        Assert.Equal(3.8, tp.Moves.Take(on).Last(m => m.IsMotion).To.Z, 9);
        Assert.Equal(1.5, tp.Moves.Skip(on).First(m => m.IsMotion).To.Z, 9);
    }

    [Fact]
    public void Small_holes_are_cut_slower_without_thc()
    {
        var (doc, op) = Plate(o =>
        {
            o.SmallHoleSize.Set(12);
            o.SmallHoleFeedrate.Set(50);
        });
        var tp = ToolpathGenerator.Generate(doc, op);
        var cuts = Cuts(tp);
        Assert.Contains(cuts[0], m => Math.Abs(m.Feed - 1000) < 1e-9);
        Assert.DoesNotContain(cuts[1].Skip(1), m => Math.Abs(m.Feed - 1000) < 1e-9);
        Assert.Equal(1, tp.Moves.Count(m => m is { Kind: MoveKind.Torch, Torch: TorchAction.ThcOn }));
    }

    [Fact]
    public void Grbl_plasma_post_switches_the_torch_per_cut()
    {
        var (doc, op) = Plate(o => o.SwitchOffset.Set(1.2));
        var tp = ToolpathGenerator.Generate(doc, op);
        var post = PostProcessorDefinition.BuiltIn().Single(p => p.Name == "GRBL Plasma");
        var lines = GCodePoster.Post(doc, new Dictionary<MachineOp, Toolpath> { [op] = tp }, post).Text.Split("\r\n");
        Assert.Equal(2, lines.Count(l => l == "M3 S1000"));
        Assert.Equal(3, lines.Count(l => l == "M5")); // after each cut and once more in the footer
        Assert.Equal(2, lines.Count(l => l == "G4 P0.5"));
        Assert.Equal(2, lines.Count(l => l == "G38.2 Z-10.0 F300.0"));
        Assert.Equal(2, lines.Count(l => l == "G92 Z-1.2"));
        Assert.Contains("G92.1", lines);
        // Nothing switches the torch before the first probe.
        var firstProbe = Array.FindIndex(lines, l => l.StartsWith("G38.2"));
        Assert.DoesNotContain(lines.Take(firstProbe), l => l.StartsWith("M3") || l.StartsWith("M4"));
        // Probe, up to pierce height, torch on, wait, down to cut height.
        Assert.Equal(["G92 Z-1.2", "G0 Z3.8", "M3 S1000", "G4 P0.5"], lines[(firstProbe + 1)..(firstProbe + 5)]);
        Assert.StartsWith("G1 F1000.0 Z1.5", lines[firstProbe + 5]);
    }

    [Fact]
    public void Cut_data_tables_round_trip_through_files()
    {
        var table = PlasmaCutTable.Template(inches: false);
        var path = Path.Combine(Path.GetTempPath(), $"bamcam-plasma-{Guid.NewGuid():N}.xml");
        try
        {
            LibraryFiles.SaveCutData(table, path);
            var back = LibraryFiles.LoadCutData(path);
            Assert.Equal(DrawingUnits.Millimeters, back.Units);
            Assert.Equal(table.Entries.Count, back.Entries.Count);
            var entry = Assert.IsType<PlasmaCutData>(back.Find("Mild steel 3 mm 45 A"));
            Assert.Equal(1.5, entry.KerfWidth, 9);
            Assert.Equal(3000, entry.CutFeedrate, 9);
            Assert.Equal(45, entry.Current, 9);
            Assert.Equal(DrawingUnits.Inches, PlasmaCutTable.Template(inches: true).Units);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Collection(nameof(GlobalLibrariesCollection))]
    public class LinkedCutData
    {
        [Fact]
        public void Default_values_follow_the_cut_data_and_typed_values_win()
        {
            var table = new PlasmaCutTable { Name = "test" };
            var data = new PlasmaCutData { Name = "Steel 4", KerfWidth = 1.8, CutFeedrate = 2400, PierceHeight = 4, PierceDelay = 0.4, CutHeight = 1.2 };
            table.Entries.Add(data);
            CamLibraries.PlasmaTables.Add(table);
            try
            {
                var (doc, op) = Plate();
                op.KerfWidth.State = ParamState.Default; // Plate() typed the kerf width
                op.CutData.Set("Steel 4");
                op.PierceDelay.Set(0.9);
                var changed = LibraryFiles.ApplyStyle(doc, op);
                Assert.Contains(nameof(PlasmaOp.KerfWidth), changed);
                Assert.Equal(1.8, op.KerfWidth.Value, 9);
                Assert.Equal(2400, op.CutFeedrate.Value, 9);
                Assert.Equal(1.2, op.CutHeight.Value, 9);
                Assert.Equal(0.9, op.PierceDelay.Value, 9); // typed in the operation

                // Linked: a changed table value reaches the operation the next time styles are applied.
                data.CutFeedrate = 2100;
                LibraryFiles.ApplyStyle(doc, op);
                Assert.Equal(2100, op.CutFeedrate.Value, 9);

                var tp = ToolpathGenerator.Generate(doc, op);
                var post = PostProcessorDefinition.BuiltIn().Single(p => p.Name == "GRBL Plasma");
                var text = GCodePoster.Post(doc, new Dictionary<MachineOp, Toolpath> { [op] = tp }, post).Text;
                Assert.Contains("( plasma Steel 4 )", text);
                Assert.Contains("F2100.0", text);
                Assert.Contains(PlasmaOp.CutDataChoices(), c => c.Key == "Steel 4");
            }
            finally
            {
                CamLibraries.PlasmaTables.Remove(table);
            }
        }
    }

    [Fact]
    public void Operation_round_trips_through_cb_files()
    {
        var (doc, op) = Plate(o =>
        {
            o.HeightControl.Set(TorchHeightControl.FixedHeights);
            o.KerfSide.Set(PlasmaKerfSide.OnLine);
            o.LeadOut.Set(PlasmaLead.Line);
            o.PierceDelay.Set(0.8);
        });
        var back = CbReader.Read(System.Xml.Linq.XDocument.Parse(CbWriter.ToString(doc)));
        var plasma = Assert.IsType<PlasmaOp>(Assert.Single(back.ActivePart.Operations));
        Assert.Equal(TorchHeightControl.FixedHeights, plasma.HeightControl.Value);
        Assert.Equal(PlasmaKerfSide.OnLine, plasma.KerfSide.Value);
        Assert.Equal(PlasmaLead.Line, plasma.LeadOut.Value);
        Assert.Equal(0.8, plasma.PierceDelay.Value, 9);
        Assert.Equal(2, plasma.KerfWidth.Value, 9);
        Assert.Equal(SpindleDirection.Off, plasma.SpindleDirection.Value);
    }
}
