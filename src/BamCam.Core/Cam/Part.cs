using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Xml.Linq;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

/// <summary>Stock block definition (Machining or Part level).</summary>
public sealed class Stock
{
    [Description("Stock material name; used for style library lookup.")]
    public string Material { get; set; } = "";

    /// <summary>Lower corner (X, Y, Z of the bottom).</summary>
    public Point3 PMin { get; set; }

    /// <summary>Upper corner (Z = stock surface).</summary>
    public Point3 PMax { get; set; }

    public RgbColor Color { get; set; } = new(255, 165, 0);

    public bool IsEmpty => PMax.X - PMin.X <= 0 || PMax.Y - PMin.Y <= 0;

    public Point3 Min => PMin;
    public Point3 Max => PMax;

    public double SurfaceZ => PMax.Z;
    public Point3 Size => PMax - PMin;

    public Stock Clone() => (Stock)MemberwiseClone();

    public override string ToString() => IsEmpty ? "(none)" : $"{Size.X} x {Size.Y} x {Size.Z}";
}

public sealed class NestingSettings
{
    public Point2 BasePoint { get; set; }
    public NestMethod NestMethod { get; set; } = NestMethod.None;
    public int Rows { get; set; } = 1;
    public int Columns { get; set; } = 1;
    public double Spacing { get; set; }
    public string GridOrder { get; set; } = "";
    public bool GridAlternate { get; set; }
    public int PointListID { get; set; }

    /// <summary>Unknown nesting children kept for round-tripping.</summary>
    public List<XElement> Extensions { get; } = [];

    public override string ToString() => NestMethod.ToString();
}

public sealed class NestingItem
{
    public Point2 Location { get; set; }
    public Matrix4 Transform { get; set; } = Matrix4.Identity;
}

/// <summary>A group of machining operations (one output job), as in the "parts" section of a project.</summary>
public sealed class Part
{
    [Category("Part")]
    public string Name { get; set; } = "Part";

    [Category("Part"), Description("Disabled parts are skipped when generating toolpaths and G-code.")]
    public bool Enabled { get; set; } = true;

    [Category("Part"), Description("Free text notes.")]
    public string? Tag { get; set; }

    [Category("Output"), DisplayName("Out File")]
    public string OutFile { get; set; } = "";

    [Category("Machining"), DisplayName("Machining Origin"), Description("Drawing point that becomes X0 Y0 for this part.")]
    public OptionalPoint MachiningOrigin { get; set; } = OptionalPoint.None;

    [Category("Machining"), Description("Default CAM style for operations in this part.")]
    public string Style { get; set; } = "";

    [Category("Machining"), DisplayName("Style Library")]
    public string StyleLibrary { get; set; } = "";

    [Category("Tool"), DisplayName("Tool Library")]
    public string ToolLibrary { get; set; } = "";

    [Category("Tool"), DisplayName("Tool Number")]
    public int ToolNumber { get; set; }

    [Category("Tool"), DisplayName("Tool Diameter")]
    public double ToolDiameter { get; set; }

    [Category("Tool"), DisplayName("Tool Profile")]
    public ToolProfile ToolProfile { get; set; } = ToolProfile.Unspecified;

    [Category("Stock")]
    public Stock Stock { get; set; } = new() { Color = RgbColor.Black };

    [Category("Nesting")]
    public NestingSettings Nesting { get; set; } = new();

    [Browsable(false)]
    public List<NestingItem> NestingItems { get; } = [];

    [Browsable(false)]
    public ObservableCollection<MachineOp> Operations { get; } = [];

    /// <summary>Unknown/unmodelled part-level XML, preserved on save.</summary>
    [Browsable(false)]
    public List<XElement> Extensions { get; } = [];

    public override string ToString() => Name;
}

/// <summary>Project-wide machining settings ("MachiningOptions").</summary>
public sealed class MachiningOptions
{
    [Category("Output"), DisplayName("Out File")]
    public string OutFile { get; set; } = "";

    [Category("Output"), DisplayName("Post Processor"), Description("Blank = system default post processor.")]
    public string PostProcessor { get; set; } = "";

    [Category("Output"), DisplayName("Post Processor Macros"), Description("Lines of the form $name=value.")]
    public string PostProcessorMacros { get; set; } = "";

    [Category("Output"), DisplayName("Custom File Header")]
    public string CustomFileHeader { get; set; } = "";

    [Category("Output"), DisplayName("Custom File Footer")]
    public string CustomFileFooter { get; set; } = "";

    [Category("Output"), DisplayName("Arc Center Mode")]
    public ArcCenterMode ArcCenterMode { get; set; } = ArcCenterMode.Default;

    [Category("Output"), DisplayName("Velocity Mode")]
    public VelocityMode VelocityMode { get; set; } = VelocityMode.Default;

    [Category("Output"), DisplayName("Rebuild Toolpath Before Post")]
    public RebuildToolpath RebuildToolpathBeforePost { get; set; } = RebuildToolpath.IfNeeded;

    [Category("Machining"), DisplayName("Fast Plunge Height"),
     Description("Rapid down to this height above the stock or last cut level, then feed. 0 = always feed.")]
    public double FastPlungeHeight { get; set; } = 0.2;

    [Category("Machining"), DisplayName("Machining Origin")]
    public OptionalPoint MachiningOrigin { get; set; } = OptionalPoint.None;

    [Category("Machining")]
    public string Style { get; set; } = "";

    [Category("Machining"), DisplayName("Style Library")]
    public string StyleLibrary { get; set; } = "";

    [Category("Tool"), DisplayName("Tool Library")]
    public string ToolLibrary { get; set; } = "";

    [Category("Tool"), DisplayName("Tool Number")]
    public int ToolNumber { get; set; }

    [Category("Tool"), DisplayName("Tool Diameter")]
    public double ToolDiameter { get; set; }

    [Category("Tool"), DisplayName("Tool Profile")]
    public ToolProfile ToolProfile { get; set; } = ToolProfile.Unspecified;

    [Category("Stock")]
    public Stock Stock { get; set; } = new();

    [Browsable(false)]
    public List<XElement> Extensions { get; } = [];
}

/// <summary>Marks a <see cref="CamParam{T}"/> property with its file/grid order and category.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ParamAttribute(int order, string category = "General") : Attribute
{
    public int Order { get; } = order;
    public string Category { get; } = category;

    /// <summary>Element name in files when it differs from the property name.</summary>
    public string? XmlName { get; init; }
}

/// <summary>Base of all machining operations (MOPs).</summary>
public abstract class MachineOp
{
    /// <summary>Element name in project files ("profile", "pocket", ...).</summary>
    [Browsable(false)]
    public abstract string XmlName { get; }

    [Browsable(false)]
    public abstract string DisplayType { get; }

    [Category("General")]
    public string Name { get; set; } = "";

    [Category("General")]
    public bool Enabled { get; set; } = true;

    [Category("General"), Description("CAM style providing default values.")]
    public string Style { get; set; } = "";

    [Category("General")]
    public string? Tag { get; set; }

    /// <summary>Ids of the drawing objects this operation machines.</summary>
    [Browsable(false)]
    public List<int> PrimitiveIds { get; } = [];

    /// <summary>Order of parameter elements as read from file (for faithful re-writing).</summary>
    [Browsable(false)]
    public List<string> ElementOrder { get; } = [];

    [Browsable(false)]
    public List<XElement> Extensions { get; } = [];

    // ---- Common parameters (Â§6.2.1) -----------------------------------------------------

    [Param(10, "Tool")] public CamParam<WorkPlane> WorkPlane { get; set; } = new(Cam.WorkPlane.XY);
    [Param(11, "Tool")] public CamParam<OptimisationMode> OptimisationMode { get; set; } = new(Cam.OptimisationMode.Experimental);
    [Param(12, "Cutting")] public CamParam<RoughingFinishing> RoughingFinishing { get; set; } = new(Cam.RoughingFinishing.Roughing);
    [Param(13, "Tool")] public CamParam<double> ToolDiameter { get; set; } = new(0);
    [Param(14, "Tool")] public CamParam<int> ToolNumber { get; set; } = new(0);
    [Param(15, "Tool")] public CamParam<ToolProfile> ToolProfile { get; set; } = new(Cam.ToolProfile.EndMill);
    [Param(16, "Feedrates")] public CamParam<double> PlungeFeedrate { get; set; } = new(300);
    [Param(17, "Feedrates")] public CamParam<double> CutFeedrate { get; set; } = new(800);
    [Param(18, "Options")] public CamParam<double> MaxCrossoverDistance { get; set; } = new(0.7);
    [Param(19, "G-Code")] public CamParam<string> CustomMOPHeader { get; set; } = new("");
    [Param(20, "G-Code")] public CamParam<string> CustomMOPFooter { get; set; } = new("");
    [Param(30, "Depth")] public CamParam<double> StockSurface { get; set; } = new(0);
    [Param(31, "Cutting")] public CamParam<double> RoughingClearance { get; set; } = new(0);
    [Param(32, "Depth")] public CamParam<double> ClearancePlane { get; set; } = new(3);
    [Param(33, "Spindle")] public CamParam<SpindleDirection> SpindleDirection { get; set; } = new(Cam.SpindleDirection.CW);
    [Param(34, "Spindle")] public CamParam<double> SpindleSpeed { get; set; } = new(1000);
    [Param(35, "Spindle")] public CamParam<int> SpindleRange { get; set; } = new(0);
    [Param(36, "G-Code")] public CamParam<VelocityMode> VelocityMode { get; set; } = new(Cam.VelocityMode.Undefined);
    [Param(37, "Options")] public CamParam<OptionalPoint> StartPoint { get; set; } = new(OptionalPoint.None);
    [Param(38, "Options", XmlName = "Transform")] public CamParam<Matrix4> MOPTransform { get; set; } = new(Matrix4.Identity);

    /// <summary>Tool diameter used for this op, resolving 0 from part / machining defaults.</summary>
    public double EffectiveToolDiameter(CadDocument doc)
    {
        if (ToolDiameter.Value > 0) return ToolDiameter.Value;
        var part = doc.Parts.FirstOrDefault(p => p.Operations.Contains(this));
        if (part is { ToolDiameter: > 0 }) return part.ToolDiameter;
        if (doc.Machining.ToolDiameter > 0) return doc.Machining.ToolDiameter;
        return 3;
    }

    /// <summary>All parameters in canonical order. Name is the file element name.</summary>
    public IEnumerable<(string Name, ICamParam Param, ParamAttribute Info)> Parameters()
    {
        return GetType().GetProperties()
            .Select(p => (Prop: p, Info: p.GetCustomAttributes(typeof(ParamAttribute), true).OfType<ParamAttribute>().FirstOrDefault()))
            .Where(x => x.Info != null && typeof(ICamParam).IsAssignableFrom(x.Prop.PropertyType))
            .OrderBy(x => x.Info!.Order)
            .Select(x => (x.Info!.XmlName ?? x.Prop.Name, (ICamParam)x.Prop.GetValue(this)!, x.Info!));
    }

    public ICamParam? FindParameter(string name) =>
        Parameters().Where(p => p.Name == name).Select(p => p.Param).FirstOrDefault();

    /// <summary>Puts every parameter back into the inherited (Default) state.</summary>
    public void ResetToDefaults()
    {
        foreach (var (_, p, _) in Parameters()) p.State = ParamState.Default;
    }

    public MachineOp CloneOp()
    {
        var clone = (MachineOp)Activator.CreateInstance(GetType())!;
        clone.Name = Name;
        clone.Enabled = Enabled;
        clone.Style = Style;
        clone.Tag = Tag;
        clone.PrimitiveIds.AddRange(PrimitiveIds);
        clone.ElementOrder.AddRange(ElementOrder);
        clone.Extensions.AddRange(Extensions.Select(x => new XElement(x)));
        foreach (var prop in GetType().GetProperties())
            if (typeof(ICamParam).IsAssignableFrom(prop.PropertyType) && prop.CanWrite)
                prop.SetValue(clone, ((ICamParam)prop.GetValue(this)!).CloneParam());
        CloneExtra(clone);
        return clone;
    }

    protected virtual void CloneExtra(MachineOp clone) { }

    public override string ToString() => Name;
}
