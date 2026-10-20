using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Xml.Linq;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;

namespace BamCam.Core.Model;

public enum DrawingUnits { Millimeters, Inches, Centimeters, Meters, Thousandths, Unspecified }

public sealed class Layer
{
    private readonly ObservableCollection<Entity> _entities = [];

    public Layer(string name) => Name = name;

    [Category("Layer")]
    public string Name { get; set; }

    [Category("Layer")]
    public RgbColor Color { get; set; } = RgbColor.DefaultLayer;

    [Category("Layer"), Description("Display transparency, 0 = transparent, 1 = opaque.")]
    public double Alpha { get; set; } = 1;

    [Category("Layer")]
    public bool Visible { get; set; } = true;

    [Category("Layer"), Description("Locked layers cannot be selected or edited.")]
    public bool Locked { get; set; }

    [Category("Layer"), Description("Pen width used for display.")]
    public double PenWidth { get; set; } = 1;

    [Browsable(false)]
    public ReadOnlyObservableCollection<Entity> Entities => new(_entities);

    [Browsable(false)]
    public List<XElement> Extensions { get; } = [];

    internal ObservableCollection<Entity> MutableEntities => _entities;

    public override string ToString() => Name;
}

/// <summary>The project: drawing (layers and entities) plus machining (parts and operations).</summary>
public sealed class CadDocument
{
    private int _nextId = 1;

    [Category("Drawing")]
    public string Name { get; set; } = "Untitled";

    [Category("Drawing")]
    public DrawingUnits Units { get; set; } = DrawingUnits.Millimeters;

    [Browsable(false)]
    public string? FilePath { get; set; }

    [Browsable(false)]
    public string FormatVersion { get; set; } = "0.9.8.0";

    [Browsable(false)]
    public ObservableCollection<Layer> Layers { get; } = [];

    [Browsable(false)]
    public ObservableCollection<Part> Parts { get; } = [];

    [Browsable(false)]
    public MachiningOptions Machining { get; } = new();

    [Browsable(false)]
    public string? ActiveLayerName { get; set; }

    [Browsable(false)]
    public string? ActivePartName { get; set; }

    [Category("Drawing")]
    public bool ShowCutWidths { get; set; }

    /// <summary>Unknown root-level XML children, preserved on save.</summary>
    [Browsable(false)]
    public List<XElement> Extensions { get; } = [];

    /// <summary>Raised after any structural or geometric modification.</summary>
    public event EventHandler? Changed;

    public bool IsModified { get; set; }

    public void NotifyChanged()
    {
        IsModified = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static CadDocument CreateNew()
    {
        var doc = new CadDocument();
        doc.Layers.Add(new Layer("Default"));
        doc.ActiveLayerName = "Default";
        var part = new Part { Name = "Part1" };
        doc.Parts.Add(part);
        doc.ActivePartName = part.Name;
        doc.IsModified = false;
        return doc;
    }

    public Layer ActiveLayer
    {
        get
        {
            var layer = Layers.FirstOrDefault(l => l.Name == ActiveLayerName) ?? Layers.FirstOrDefault();
            if (layer == null)
            {
                layer = new Layer("Default");
                Layers.Add(layer);
            }
            ActiveLayerName = layer.Name;
            return layer;
        }
    }

    public Part ActivePart
    {
        get
        {
            var part = Parts.FirstOrDefault(p => p.Name == ActivePartName) ?? Parts.FirstOrDefault();
            if (part == null)
            {
                part = new Part { Name = "Part1" };
                Parts.Add(part);
            }
            ActivePartName = part.Name;
            return part;
        }
    }

    public IEnumerable<Entity> AllEntities() => Layers.SelectMany(l => l.Entities);

    public Entity? FindEntity(int id) => AllEntities().FirstOrDefault(e => e.Id == id);

    public int AllocateId() => _nextId++;

    /// <summary>Adds an entity to a layer, assigning a fresh id when it has none or a clashing one.</summary>
    public T AddEntity<T>(T entity, Layer? layer = null) where T : Entity
    {
        layer ??= ActiveLayer;
        if (entity.Id <= 0 || FindEntity(entity.Id) != null) entity.Id = AllocateId();
        else _nextId = Math.Max(_nextId, entity.Id + 1);
        entity.Layer = layer;
        layer.MutableEntities.Add(entity);
        return entity;
    }

    /// <summary>Adds an entity keeping its id as read from a file.</summary>
    public void AddLoadedEntity(Entity entity, Layer layer)
    {
        _nextId = Math.Max(_nextId, entity.Id + 1);
        entity.Layer = layer;
        layer.MutableEntities.Add(entity);
    }

    public bool RemoveEntity(Entity entity)
    {
        var layer = entity.Layer;
        if (layer == null || !layer.MutableEntities.Remove(entity)) return false;
        entity.Layer = null;
        return true;
    }

    public void MoveEntity(Entity entity, Layer target)
    {
        if (entity.Layer == target) return;
        entity.Layer?.MutableEntities.Remove(entity);
        entity.Layer = target;
        target.MutableEntities.Add(entity);
    }

    public BoundingBox Bounds(bool visibleOnly = true)
    {
        var b = BoundingBox.Empty;
        foreach (var l in Layers)
        {
            if (visibleOnly && !l.Visible) continue;
            foreach (var e in l.Entities) b = b.Union(e.Bounds());
        }
        return b;
    }

    public string UniqueLayerName(string prefix = "Layer")
    {
        for (var i = 1; ; i++)
        {
            var name = $"{prefix}{i}";
            if (Layers.All(l => l.Name != name)) return name;
        }
    }

    public string UniquePartName(string prefix = "Part")
    {
        for (var i = 1; ; i++)
        {
            var name = $"{prefix}{i}";
            if (Parts.All(p => p.Name != name)) return name;
        }
    }
}
