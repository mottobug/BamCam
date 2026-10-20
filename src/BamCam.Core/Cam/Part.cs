using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Xml.Linq;

namespace BamCam.Core.Cam;

/// <summary>A group of machining operations (one output job), as in the "parts" section of a project.</summary>
public sealed class Part
{
    [Category("Part")]
    public string Name { get; set; } = "Part";

    [Category("Part"), Description("Disabled parts are skipped when generating toolpaths and G-code.")]
    public bool Enabled { get; set; } = true;

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
    [Browsable(false)]
    public List<XElement> Extensions { get; } = [];
}

/// <summary>Base of all machining operations (MOPs).</summary>
public abstract class MachineOp
{
    [Browsable(false)]
    public abstract string XmlName { get; }

    [Category("General")]
    public string Name { get; set; } = "";

    [Category("General")]
    public bool Enabled { get; set; } = true;

    /// <summary>Ids of the drawing objects this operation machines.</summary>
    [Browsable(false)]
    public List<int> PrimitiveIds { get; } = [];

    [Browsable(false)]
    public List<XElement> Extensions { get; } = [];

    public override string ToString() => Name;
}
