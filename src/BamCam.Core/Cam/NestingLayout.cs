using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

/// <summary>Computes the nest copies (<see cref="Part.NestingItems"/>) of a part for Grid and ISO Grid nesting.</summary>
public static class NestingLayout
{
    /// <summary>
    /// Extents of a part's enabled MOPs: their toolpaths where generated, otherwise the machined
    /// shapes grown by the tool radius.
    /// </summary>
    public static BoundingBox PartExtents(CadDocument doc, Part part, IReadOnlyDictionary<MachineOp, Toolpath>? toolpaths)
    {
        var box = BoundingBox.Empty;
        foreach (var op in part.Operations)
        {
            if (!op.Enabled) continue;
            if (toolpaths != null && toolpaths.TryGetValue(op, out var tp) && tp.Moves.Count > 0)
            {
                var b = BoundingBox.Empty;
                foreach (var (from, to, kind) in tp.DisplaySegments())
                    if (kind != MoveKind.Rapid) b = b.Include(from).Include(to);
                box = box.Union(b);
                continue;
            }
            var shapes = BoundingBox.Empty;
            foreach (var id in op.PrimitiveIds)
                if (doc.FindEntity(id) is { } e) shapes = shapes.Union(e.Bounds());
            box = box.Union(shapes.Inflate(op.EffectiveToolDiameter(doc) / 2));
        }
        return box;
    }

    /// <summary>Copy offsets for Grid / ISO Grid nesting, in machining order.</summary>
    public static List<NestingItem> Grid(NestingSettings n, BoundingBox extents)
    {
        var result = new List<NestingItem>();
        if (extents.IsEmpty) return result;
        var rows = Math.Max(1, n.Rows);
        var cols = Math.Max(1, n.Columns);
        var pitchX = extents.Width + n.Spacing;
        var pitchY = extents.Height + n.Spacing;

        var order = n.GridOrder;
        var rowFirst = order is NestGridOrder.RightUp or NestGridOrder.RightDown or NestGridOrder.LeftUp or NestGridOrder.LeftDown;
        var sx = order is NestGridOrder.LeftUp or NestGridOrder.LeftDown or NestGridOrder.UpLeft or NestGridOrder.DownLeft ? -1 : 1;
        var sy = order is NestGridOrder.RightDown or NestGridOrder.LeftDown or NestGridOrder.DownRight or NestGridOrder.DownLeft ? -1 : 1;
        var outer = rowFirst ? rows : cols;
        var inner = rowFirst ? cols : rows;
        var iso = n.NestMethod == NestMethod.ISOGrid;

        for (var o = 0; o < outer; o++)
        for (var k = 0; k < inner; k++)
        {
            var i = n.GridAlternate && o % 2 == 1 ? inner - 1 - k : k;
            var col = rowFirst ? i : o;
            var row = rowFirst ? o : i;
            var x = col * pitchX;
            var y = row * pitchY;
            // ISO grid: every other row (or column) shifted by half a pitch.
            if (iso && rowFirst && row % 2 == 1) x += pitchX / 2;
            if (iso && !rowFirst && col % 2 == 1) y += pitchY / 2;
            result.Add(new NestingItem { Location = new Point2(sx * x, sy * y) });
        }
        return result;
    }

    /// <summary>Regenerates <see cref="Part.NestingItems"/> for Grid / ISO Grid parts. Returns true if they changed.</summary>
    public static bool Update(CadDocument doc, Part part, IReadOnlyDictionary<MachineOp, Toolpath>? toolpaths)
    {
        if (part.Nesting.NestMethod is not (NestMethod.Grid or NestMethod.ISOGrid)) return false;
        var items = Grid(part.Nesting, PartExtents(doc, part, toolpaths));
        if (items.Count == 0) return false;
        var same = items.Count == part.NestingItems.Count && items.Zip(part.NestingItems)
            .All(p => p.First.Location.AlmostEquals(p.Second.Location, 1e-9) && p.Second.Transform.IsIdentity);
        if (same) return false;
        part.NestingItems.Clear();
        part.NestingItems.AddRange(items);
        return true;
    }

    /// <summary>Copy transforms as used for output (same composition as the post processor).</summary>
    public static IEnumerable<Matrix4> CopyTransforms(Part part) =>
        part.NestingItems.Select(i => i.Transform * Matrix4.Translation(i.Location.X, i.Location.Y));
}
