using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using BamCam.Core.Geometry;

namespace BamCam.Core.Model;

/// <summary>
/// A drawing object whose geometry is computed from parameters (plugin object types, script objects). Its public
/// settable properties of simple types (number, integer, bool, text, enum) are the parameters: they show in the
/// property grid and are saved in drawings. Drawings store the computed outlines as ordinary polylines as well, so
/// that CamBam and BamCam without the plugin still show (and can machine) the object.
/// </summary>
public abstract class ParametricEntity : Entity
{
    private List<Path2>? _paths;

    /// <summary>Type name in drawing files, unique (prefix it with the vendor: "acme.gear").</summary>
    [Browsable(false)]
    public abstract string ParametricType { get; }

    /// <summary>Name in the drawing tree.</summary>
    public override string TypeName => ParametricType;

    /// <summary>The outlines in the object's own coordinates, or null when they cannot be computed now.</summary>
    protected abstract IEnumerable<Path2>? Generate();

    /// <summary>
    /// The outlines saved in the drawing, in drawing coordinates (moved along with the object); shown when
    /// <see cref="Generate"/> gives none (plugin missing, script not allowed).
    /// </summary>
    [Browsable(false)]
    public List<Path2> SavedOutlines { get; } = [];

    /// <summary>Recomputes the outlines on the next use (after a parameter changed).</summary>
    public void Invalidate() => _paths = null;

    /// <summary>True when the outlines are computed (not the saved ones).</summary>
    [Browsable(false)]
    public bool IsComputed => Computed != null;

    private List<Path2>? Computed
    {
        get
        {
            if (_paths == null && Generate()?.ToList() is { } generated) _paths = generated;
            return _paths;
        }
    }

    public override IEnumerable<Path2> ToPaths()
    {
        if (Computed is not { } local) return SavedOutlines;
        return Transform.IsIdentity ? local : local.Select(p => p.Transformed(Transform));
    }

    public override void ApplyTransform(Matrix4 m)
    {
        Transform *= m;
        for (var i = 0; i < SavedOutlines.Count; i++) SavedOutlines[i] = SavedOutlines[i].Transformed(m);
    }

    public override Entity CloneEntity()
    {
        var copy = ParametricEntities.Create(ParametricType) ?? throw new InvalidOperationException($"Unknown object type '{ParametricType}'.");
        copy.LoadParameters(SaveParameters());
        copy.SavedOutlines.AddRange(SavedOutlines);
        return CopyBase(copy);
    }

    private IEnumerable<PropertyInfo> ParameterProperties() =>
        GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.DeclaringType != typeof(Entity)
                        && p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false
                        && (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string) || p.PropertyType == typeof(double)));

    /// <summary>The parameters as invariant text (for saving).</summary>
    public IReadOnlyList<(string Name, string Value)> SaveParameters() =>
        ParameterProperties().Select(p => (p.Name, Convert.ToString(p.GetValue(this), CultureInfo.InvariantCulture) ?? "")).ToList();

    /// <summary>Sets parameters from saved text; unknown names and unreadable values are ignored.</summary>
    public void LoadParameters(IEnumerable<(string Name, string Value)> values)
    {
        var props = ParameterProperties().ToDictionary(p => p.Name);
        foreach (var (name, value) in values)
        {
            if (!props.TryGetValue(name, out var p)) continue;
            try
            {
                object v = p.PropertyType.IsEnum ? Enum.Parse(p.PropertyType, value)
                    : p.PropertyType == typeof(string) ? value
                    : Convert.ChangeType(value, p.PropertyType, CultureInfo.InvariantCulture);
                p.SetValue(this, v);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or InvalidCastException)
            {
                // Keep the default of a value that cannot be read.
            }
        }
        Invalidate();
    }
}

/// <summary>Registry of parametric object types (built-in and from plugins).</summary>
public static class ParametricEntities
{
    private static readonly Dictionary<string, Func<ParametricEntity>> Types = new(StringComparer.Ordinal)
    {
        [ScriptEntity.Type] = () => new ScriptEntity(),
    };

    public static void Register(string type, Func<ParametricEntity> create) => Types[type] = create;

    public static bool IsRegistered(string type) => Types.ContainsKey(type);

    public static ParametricEntity? Create(string type) => Types.TryGetValue(type, out var create) ? create() : null;

    /// <summary>
    /// Turns the saved polylines of parametric objects in a layer back into the objects (reading a drawing): the
    /// polyline carrying the object's data (see <paramref name="dataOf"/>) and the polylines marked as its further
    /// parts become one object of a registered type. Unregistered types stay ordinary polylines.
    /// </summary>
    /// <param name="dataOf">Type, transform, parameters and part count of a polyline that starts an object, else null.</param>
    /// <param name="partOf">Id of the object a further part belongs to, else null.</param>
    public static void Restore(Layer layer,
        Func<PolylineEntity, (string Type, Matrix4 Transform, List<(string, string)> Parameters)?> dataOf,
        Func<PolylineEntity, int?> partOf)
    {
        var list = layer.MutableEntities;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not PolylineEntity first || dataOf(first) is not { } data || Create(data.Type) is not { } obj) continue;
            obj.Id = first.Id;
            obj.Tag = first.Tag;
            obj.Transform = data.Transform;
            obj.LoadParameters(data.Parameters);
            obj.SavedOutlines.AddRange(first.ToPaths().Where(p => p.Count > 0));
            foreach (var x in first.Extensions) obj.Extensions.Add(x);
            obj.Layer = layer;
            list[i] = obj;
            first.Layer = null;
            // Further parts follow the first one.
            for (var j = i + 1; j < list.Count;)
            {
                if (list[j] is PolylineEntity part && partOf(part) == obj.Id)
                {
                    obj.SavedOutlines.AddRange(part.ToPaths());
                    part.Layer = null;
                    list.RemoveAt(j);
                }
                else
                {
                    j++;
                }
            }
        }
    }
}
