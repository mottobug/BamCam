using BamCam.Core.Cad;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;

namespace BamCam.Core.Model;

/// <summary>What was added by <see cref="DrawingMerge.Insert"/>.</summary>
public sealed record InsertedDrawing(List<Entity> Entities, List<Layer> NewLayers, List<Part> NewParts, List<MachineOp> Operations, double Scale);

/// <summary>Inserts another drawing into a drawing (File ▸ Insert Drawing).</summary>
public static class DrawingMerge
{
    /// <summary>True when both drawings have known, different units, so that the geometry must be scaled.</summary>
    public static bool UnitsDiffer(CadDocument target, CadDocument source) =>
        target.Units != DrawingUnits.Unspecified && source.Units != DrawingUnits.Unspecified && target.Units != source.Units;

    /// <summary>
    /// Moves the objects of <paramref name="source"/>, which is used up, into <paramref name="target"/> under new ids.
    /// Objects go to the layer of the same name, other layers are added. With <paramref name="withOperations"/> the
    /// parts with operations are added as new parts and their references follow the new ids. In other units the
    /// geometry is scaled and operations stay out, because depths, feedrates and tools would not fit.
    /// </summary>
    public static InsertedDrawing Insert(CadDocument target, CadDocument source, bool withOperations)
    {
        var scale = UnitsDiffer(target, source)
            ? CuttingData.MillimetresPerUnit(source.Units) / CuttingData.MillimetresPerUnit(target.Units)
            : 1;
        var result = new InsertedDrawing([], [], [], [], scale);
        var ids = new Dictionary<int, int>();
        foreach (var layer in source.Layers.ToList())
        {
            var entities = layer.Entities.ToList();
            layer.MutableEntities.Clear();
            var into = target.Layers.FirstOrDefault(l => l.Name == layer.Name);
            if (into == null)
            {
                into = layer;
                target.Layers.Add(into);
                result.NewLayers.Add(into);
            }
            foreach (var entity in entities)
            {
                var oldId = entity.Id;
                entity.Id = 0;
                target.AddEntity(entity, into);
                ids[oldId] = entity.Id;
                result.Entities.Add(entity);
            }
        }
        if (scale != 1) CadOps.Transform(result.Entities, Matrix4.Scale(scale, scale, scale));
        if (!withOperations || scale != 1) return result;

        foreach (var part in source.Parts.Where(p => p.Operations.Count > 0).ToList())
        {
            part.Name = UniqueName(target, part.Name, source.Name);
            // The file name of the other job must not be overwritten when this drawing writes its G-code.
            part.OutFile = "";
            TakeOverMachiningOptions(part, source.Machining);
            if (part.Nesting.PointListID != 0 && ids.TryGetValue(part.Nesting.PointListID, out var pointList)) part.Nesting.PointListID = pointList;
            foreach (var op in part.Operations)
            {
                Remap(op, ids);
                result.Operations.Add(op);
            }
            target.Parts.Add(part);
            result.NewParts.Add(part);
        }
        return result;
    }

    /// <summary>Values a part inherits from the machining options of its own drawing stay with it in the other drawing.</summary>
    private static void TakeOverMachiningOptions(Part part, MachiningOptions options)
    {
        if (part.Style.Length == 0) part.Style = options.Style;
        if (part.StyleLibrary.Length == 0) part.StyleLibrary = options.StyleLibrary;
        if (part.ToolLibrary.Length == 0) part.ToolLibrary = options.ToolLibrary;
        if (part.ToolNumber == 0) part.ToolNumber = options.ToolNumber;
        if (part.ToolDiameter == 0) part.ToolDiameter = options.ToolDiameter;
        if (part.ToolProfile == ToolProfile.Unspecified) part.ToolProfile = options.ToolProfile;
        if (!part.MachiningOrigin.IsSet) part.MachiningOrigin = options.MachiningOrigin;
        if (part.Stock.IsEmpty) part.Stock = options.Stock.Clone();
    }

    private static void Remap(MachineOp op, Dictionary<int, int> ids)
    {
        int Map(int id) => ids.GetValueOrDefault(id, id);
        for (var i = 0; i < op.PrimitiveIds.Count; i++) op.PrimitiveIds[i] = Map(op.PrimitiveIds[i]);
        if (op is ProfileOp profile)
            foreach (var tab in profile.Tabs) tab.ParentEntityID = Map(tab.ParentEntityID);
        if (op is Profile3DOp surface)
            for (var i = 0; i < surface.BoundaryShapes.Count; i++) surface.BoundaryShapes[i] = Map(surface.BoundaryShapes[i]);
        // Written back as read: the ids in the original element must follow too.
        if (op is UnsupportedOp { RawXml: { } raw })
            foreach (var prim in raw.Element("primitive")?.Elements("prim") ?? [])
                if (int.TryParse(prim.Value, out var id)) prim.Value = Map(id).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string UniqueName(CadDocument target, string name, string drawing)
    {
        if (target.Parts.All(p => p.Name != name)) return name;
        var unique = $"{name} ({drawing})";
        for (var i = 2; target.Parts.Any(p => p.Name == unique); i++) unique = $"{name} ({drawing} {i})";
        return unique;
    }
}
