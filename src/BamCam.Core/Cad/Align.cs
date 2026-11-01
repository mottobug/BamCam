using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cad;

/// <summary>Which edge or centre line of the objects is aligned.</summary>
public enum AlignEdge { Left, CenterX, Right, Top, CenterY, Bottom }

/// <summary>What the objects are aligned to.</summary>
public enum AlignReference
{
    /// <summary>The object selected last (it stays where it is).</summary>
    LastSelected,
    /// <summary>The object selected first (it stays where it is).</summary>
    FirstSelected,
    /// <summary>The extents of all selected objects.</summary>
    Selection,
    /// <summary>The extents of the whole drawing.</summary>
    Drawing,
    /// <summary>The drawing origin (0, 0).</summary>
    Origin,
    /// <summary>The stock of the machining options.</summary>
    Stock,
}

/// <summary>Aligning objects by their bounding boxes (as CamBam's Align Toolbar plugin).</summary>
public static class Align
{
    /// <summary>
    /// Moves each object of <paramref name="selected"/> (in selection order) so that its <paramref name="edge"/> lines
    /// up with that of the reference; the anchor object of First/LastSelected is not moved. Returns false (nothing
    /// moved) when the reference is missing (no stock, fewer than two objects for an anchor).
    /// </summary>
    public static bool Apply(CadDocument doc, IReadOnlyList<Entity> selected, AlignEdge edge, AlignReference reference)
    {
        if (selected.Count == 0) return false;
        Entity? anchor = reference switch
        {
            AlignReference.LastSelected => selected[^1],
            AlignReference.FirstSelected => selected[0],
            _ => null,
        };
        if (anchor != null && selected.Count < 2) return false;
        BoundingBox target;
        switch (reference)
        {
            case AlignReference.Origin:
                target = new BoundingBox(Point3.Zero, Point3.Zero);
                break;
            case AlignReference.Stock:
                var stock = doc.ActivePart.Stock.IsEmpty ? doc.Machining.Stock : doc.ActivePart.Stock;
                if (stock.IsEmpty) return false;
                target = new BoundingBox(stock.PMin, stock.PMax);
                break;
            case AlignReference.Drawing:
                target = CadOps.Bounds(doc.AllEntities());
                break;
            case AlignReference.Selection:
                target = CadOps.Bounds(selected);
                break;
            default:
                target = anchor!.Bounds();
                break;
        }
        if (target.IsEmpty) return false;
        foreach (var e in selected)
        {
            if (e == anchor) continue;
            var box = e.Bounds();
            if (box.IsEmpty) continue;
            var d = Offset(box, target, edge);
            if (d.Length > 1e-12) e.ApplyTransform(Matrix4.Translation(d.X, d.Y));
        }
        return true;
    }

    /// <summary>Displacement that brings <paramref name="box"/>'s edge onto that of <paramref name="target"/>.</summary>
    public static Point2 Offset(BoundingBox box, BoundingBox target, AlignEdge edge) => edge switch
    {
        AlignEdge.Left => new Point2(target.Min.X - box.Min.X, 0),
        AlignEdge.Right => new Point2(target.Max.X - box.Max.X, 0),
        AlignEdge.CenterX => new Point2((target.Min.X + target.Max.X - box.Min.X - box.Max.X) / 2, 0),
        AlignEdge.Bottom => new Point2(0, target.Min.Y - box.Min.Y),
        AlignEdge.Top => new Point2(0, target.Max.Y - box.Max.Y),
        _ => new Point2(0, (target.Min.Y + target.Max.Y - box.Min.Y - box.Max.Y) / 2),
    };
}
