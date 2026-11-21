using System.Text.RegularExpressions;
using BamCam.Core.Cam;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>
/// Issue 32: the inch starting values of the operations only BamCam has (plasma, drag knife, V-engrave) survive saving and
/// opening the drawing. Before, the writer left out default values, and they came back as the millimetre ones.
/// </summary>
public class InchDefaultsTests
{
    public static IEnumerable<object[]> Operations() => [[new PlasmaOp()], [new DragKnifeOp()], [new VEngraveOp()]];

    private static (CadDocument Doc, MachineOp Op) InchDrawing(MachineOp op)
    {
        var doc = new CadDocument { Units = DrawingUnits.Inches };
        op.Name = "op";
        doc.ActivePart.Operations.Add(op);
        ((IInchDefaults)op).UseInchDefaults();
        return (doc, op);
    }

    private static Dictionary<string, object?> Values(MachineOp op) => op.Parameters().ToDictionary(p => p.Name, p => p.Param.BoxedValue);

    private static void AssertSameValues(Dictionary<string, object?> before, MachineOp after)
    {
        foreach (var (name, param, _) in after.Parameters())
            Assert.True(Equals(before[name], param.BoxedValue), $"{after.XmlName} {name}: {before[name]} before, {param.BoxedValue} after");
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void Inch_starting_values_survive_saving_and_opening(MachineOp created)
    {
        var (doc, op) = InchDrawing(created);
        var before = Values(op);

        var reopened = CbReader.Parse(CbWriter.ToString(doc)).ActivePart.Operations.Single();

        AssertSameValues(before, reopened);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void A_file_saved_without_the_default_values_gets_the_inch_ones(MachineOp created)
    {
        // As BamCam 0.9.2 wrote it: the parameters in the Default state left out.
        var (doc, op) = InchDrawing(created);
        var before = Values(op);
        var xml = Regex.Replace(CbWriter.ToString(doc), @"\s*<(\w+) state=""Default""[^>]*?(?:/>|>[^<]*</\1>)", "");
        Assert.DoesNotContain("LeadInLength", xml.Replace("LatheLeadInLength", ""));

        var reopened = CbReader.Parse(xml).ActivePart.Operations.Single();

        AssertSameValues(before, reopened);
    }

    [Fact]
    public void Millimetre_drawings_keep_the_millimetre_values()
    {
        var doc = new CadDocument { Units = DrawingUnits.Millimeters };
        doc.ActivePart.Operations.Add(new PlasmaOp { Name = "op" });

        var reopened = (PlasmaOp)CbReader.Parse(CbWriter.ToString(doc)).ActivePart.Operations.Single();

        Assert.Equal(new PlasmaOp().LeadInLength.Value, reopened.LeadInLength.Value, 9);
    }
}
