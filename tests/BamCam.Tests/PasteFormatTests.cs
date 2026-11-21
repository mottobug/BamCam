using BamCam.Core.Cam;
using BamCam.Core.Geometry;

namespace BamCam.Tests;

/// <summary>Paste Format takes the settings of another operation of the same type, but keeps what belongs to its objects.</summary>
public class PasteFormatTests
{
    [Fact]
    public void Plasma_settings_are_taken_but_the_shape_start_points_stay()
    {
        var source = new PlasmaOp { Name = "Source" };
        source.CutData.Set("Steel 4");
        source.PierceHeight.Set(4.2);
        source.WriteShapeStarts([(1, new Point2(5, 0))]);
        var target = new PlasmaOp { Name = "Target" };
        target.PrimitiveIds.Add(7);
        target.WriteShapeStarts([(7, new Point2(0, 3))]);

        target.PasteFormatFrom(source);

        Assert.Equal("Steel 4", target.CutData.Value);
        Assert.Equal(4.2, target.PierceHeight.Value, 9);
        Assert.Equal(ParamState.Value, target.PierceHeight.State);
        Assert.Equal("Target", target.Name);
        Assert.Equal([7], target.PrimitiveIds);
        Assert.Equal([(7, new Point2(0, 3))], target.ReadShapeStarts());
    }

    [Fact]
    public void Default_values_stay_default()
    {
        var source = new PlasmaOp();
        var target = new PlasmaOp();
        target.KerfWidth.Set(3);

        target.PasteFormatFrom(source);

        Assert.Equal(ParamState.Default, target.KerfWidth.State);
    }

    [Fact]
    public void Another_type_is_refused() =>
        Assert.Throws<ArgumentException>(() => new PlasmaOp().PasteFormatFrom(new ProfileOp()));
}
