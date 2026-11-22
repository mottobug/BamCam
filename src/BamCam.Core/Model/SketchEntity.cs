using System.ComponentModel;
using BamCam.Core.Geometry;
using BamCam.Core.Sketching;

namespace BamCam.Core.Model;

/// <summary>
/// A sketch: lines, arcs and circles held in place by constraints and driving dimensions (Draw ▸ Sketch, double-click
/// to edit). Its outlines are the non-construction geometry, joined where it meets; machining operations use them like
/// any other drawing object. Drawings also store the outlines as polylines, so CamBam sees them.
/// </summary>
public sealed class SketchEntity : ParametricEntity
{
    public const string Type = "bamcam.sketch";

    public override string ParametricType => Type;
    public override string TypeName => "Sketch";

    private Sketch? _sketch;
    private string _data = "";

    /// <summary>The sketch, saved as text (edited in the sketch mode, not in the properties).</summary>
    public string Data
    {
        get => _data;
        set
        {
            _data = value;
            _sketch = null;
            Invalidate();
        }
    }

    public override IReadOnlySet<string> HiddenParameters => HiddenData;

    private static readonly HashSet<string> HiddenData = [nameof(Data)];

    /// <summary>A copy of the sketch to edit; <see cref="SetSketch"/> stores it back.</summary>
    public Sketch GetSketch() => (_sketch ??= Sketch.Deserialize(_data)).Clone();

    public void SetSketch(Sketch sketch) => Data = sketch.Serialize();

    /// <summary>How far the sketch is determined: "Fully constrained", "3 degrees of freedom", "Over-constrained".</summary>
    [Category("Sketch"), DisplayName("Constraint Status")]
    public string Status
    {
        get
        {
            var s = GetSketch();
            var r = SketchSolver.Solve(s);
            return r.Status switch
            {
                SketchStatus.FullyConstrained => Localization.Tr.T("Fully constrained"),
                SketchStatus.OverConstrained => Localization.Tr.T("Over-constrained"),
                _ => Localization.Tr.F("{0} degrees of freedom", r.DegreesOfFreedom),
            };
        }
    }

    /// <summary>The variables of the sketch, as "name = formula" (edited in the sketch mode).</summary>
    [Category("Sketch")]
    public string Variables => string.Join("; ", (_sketch ??= Sketch.Deserialize(_data)).Variables.Select(v => $"{v.Name} = {v.Expression}"));

    protected override IEnumerable<Path2>? Generate() => (_sketch ??= Sketch.Deserialize(_data)).Outlines();
}
