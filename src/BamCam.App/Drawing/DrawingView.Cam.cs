using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Drawing;

// CAM overlays of the drawing view: holding tabs (with dragging), nest copies and the toolpath view filter.
public sealed partial class DrawingView
{
    private HoldingTab? _dragTab;
    private Path2? _dragTabShape;
    private ProfileOp? _dragTabOp;
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

    // ----------------------------------------------------------------- holding tabs

    private ProfileOp? InspectedProfile => _session?.Inspected as ProfileOp;

    private Toolpath? ToolpathOf(MachineOp op) => _session!.Toolpaths.GetValueOrDefault(op);

    private List<TabMarker> TabMarkers(ProfileOp op) => HoldingTabTools.Markers(_session!.Document, op, ToolpathOf(op));

    /// <summary>The tab of the inspected profile under a screen point, if any.</summary>
    public TabMarker? TabAt(Point screen)
    {
        if (InspectedProfile is not { } op) return null;
        var w = _view.ToWorld(screen);
        var slack = _view.ToWorldLength(3);
        foreach (var m in TabMarkers(op))
            if (m with { Length = m.Length + slack, Breadth = m.Breadth + slack } is var grown && grown.Contains(w))
                return m;
        return null;
    }

    private bool BeginTabDrag(Point screen)
    {
        if (_tool is not SelectTool || InspectedProfile is not { } op || TabAt(screen) is not { } marker) return false;
        var key = (marker.Tab.ParentEntityID, marker.Tab.ParametricPoint);
        var shape = HoldingTabTools.SourceShapes(_session!.Document, op).FirstOrDefault(s => s.Id == key.ParentEntityID).Path;
        if (shape == null) return false;
        _session.BeginEdit();
        HoldingTabTools.MakeManual(op, ToolpathOf(op));
        _dragTab = op.Tabs.FirstOrDefault(t => t.ParentEntityID == key.ParentEntityID && Math.Abs(t.ParametricPoint - key.ParametricPoint) < 1e-9);
        if (_dragTab == null) return false;
        _dragTabShape = shape;
        _dragTabOp = op;
        return true;
    }

    private void DragTab(Point screen)
    {
        if (_dragTab == null || _dragTabShape == null) return;
        HoldingTabTools.MoveTab(_dragTab, _dragTabShape, _view.ToWorld(screen));
        if (Settings.TabDragToolpathRefresh) RegenerateTabs(_dragTabOp!, notify: false);
        InvalidateVisual();
    }

    private void EndTabDrag()
    {
        var op = _dragTabOp;
        _dragTab = null;
        _dragTabShape = null;
        _dragTabOp = null;
        if (op == null) return;
        if (Settings.TabDragToolpathRefresh) RegenerateTabs(op, notify: false);
        _session!.NotifyChanged();
        _session.Log(F("{0}: holding tab moved (Tab Method = Manual).", op.Name));
    }

    /// <summary>Diagnostics: drags the tab under <paramref name="from"/> to <paramref name="to"/> (drawing coordinates).</summary>
    public bool SimulateTabDrag(Point2 from, Point2 to)
    {
        if (!BeginTabDrag(_view.ToScreen(from))) return false;
        DragTab(_view.ToScreen(Point2.Lerp(from, to, 0.5)));
        DragTab(_view.ToScreen(to));
        EndTabDrag();
        return true;
    }

    private void RegenerateTabs(ProfileOp op, bool notify)
    {
        if (_session == null) return;
        if (notify || _session.Toolpaths.ContainsKey(op))
            _session.SetToolpath(op, ToolpathGenerator.Generate(_session.Document, op));
    }

    /// <summary>Holding Tabs ▸ Autocalc: automatic tabs, rebuilt now.</summary>
    public void AutocalcTabs()
    {
        if (InspectedProfile is not { } op) return;
        _session!.Execute(T("Autocalc tabs"), () =>
        {
            op.Tabs.Clear();
            var s = op.HoldingTabs.Value;
            op.HoldingTabs.Set(s with { TabMethod = s.TabMethod is TabMethod.AutomaticInner or TabMethod.AutomaticOuter ? s.TabMethod : TabMethod.Automatic });
        });
        RegenerateTabs(op, notify: true);
        _session.Log(F("{0}: {1} holding tab(s) calculated.", op.Name, ToolpathOf(op)?.TabPlacements.Count ?? 0));
    }

    /// <summary>Holding Tabs ▸ Add Tab at the source shape point nearest to <paramref name="screen"/>.</summary>
    public void AddTabAt(Point screen)
    {
        if (InspectedProfile is not { } op) return;
        var w = _view.ToWorld(screen);
        if (HoldingTabTools.NearestShape(_session!.Document, op, w) is not { } shape)
        {
            _session.Log(F("{0}: no closed shape to put a tab on.", op.Name));
            return;
        }
        _session.Execute(T("Add tab"), () =>
        {
            HoldingTabTools.MakeManual(op, ToolpathOf(op));
            op.Tabs.Add(HoldingTabTools.CreateTab(shape.Id, shape.Path, w));
        });
        if (Settings.TabDragToolpathRefresh) RegenerateTabs(op, notify: false);
        InvalidateVisual();
    }

    /// <summary>Holding Tabs ▸ Remove Tab under <paramref name="screen"/>. Returns false if there is none.</summary>
    public bool RemoveTabAt(Point screen)
    {
        if (InspectedProfile is not { } op || TabAt(screen) is not { } marker) return false;
        var key = (marker.Tab.ParentEntityID, marker.Tab.ParametricPoint);
        _session!.Execute(T("Remove tab"), () =>
        {
            HoldingTabTools.MakeManual(op, ToolpathOf(op));
            op.Tabs.RemoveAll(t => t.ParentEntityID == key.ParentEntityID && Math.Abs(t.ParametricPoint - key.ParametricPoint) < 1e-9);
        });
        if (Settings.TabDragToolpathRefresh) RegenerateTabs(op, notify: false);
        InvalidateVisual();
        return true;
    }

    private void RenderTabs(DrawingContext dc)
    {
        if (InspectedProfile is not { } op) return;
        var fill = new SolidColorBrush(Color.FromArgb(70, 255, 215, 0));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 215, 0)), 1.5);
        var dragPen = new Pen(Brushes.White, 2);
        var bad = new Pen(Brushes.Red, 2);
        var z = op.TargetDepth.Value;
        foreach (var m in TabMarkers(op))
        {
            var corners = m.Corners();
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(_view.ToScreen(corners[0].ToPoint3(z)), true);
                for (var i = 1; i < 4; i++) ctx.LineTo(_view.ToScreen(corners[i].ToPoint3(z)));
                ctx.EndFigure(true);
            }
            dc.DrawGeometry(fill, ReferenceEquals(m.Tab, _dragTab) ? dragPen : pen, geo);
            if (m.Pending) DrawCross(dc, _view.ToScreen(m.Center.ToPoint3(z)), bad, Math.Max(5, _view.ToScreenLength(m.Breadth / 2)));
        }
    }

    private void UpdateHoverCursor(Point screen)
    {
        var over = _tool is SelectTool && InspectedProfile != null && TabAt(screen) != null;
        Cursor = over || _dragTab != null ? MoveCursor : Cursor.Default;
    }

    // ----------------------------------------------------------------- nesting

    private void RenderNests(DrawingContext dc)
    {
        if (!Settings.ShowNests || _session?.Inspected is not Part part) return;
        if (part.Nesting.NestMethod == NestMethod.None || part.NestingItems.Count == 0) return;
        var doc = _session.Document;
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(200, 230, 230, 230)), 1, new DashStyle([4, 3], 0));
        var extentPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 120, 120)), 1, new DashStyle([2, 3], 0));
        var marker = new SolidColorBrush(Colors.Red);
        var extents = NestingLayout.PartExtents(doc, part, _session.Toolpaths);
        var entities = part.Operations.Where(o => o.Enabled).SelectMany(o => o.PrimitiveIds).Distinct()
            .Select(doc.FindEntity).OfType<Entity>().ToList();
        var tol = FlattenTolerance;
        var index = 0;
        foreach (var m in NestingLayout.CopyTransforms(part))
        {
            index++;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                foreach (var path in entities.SelectMany(e => e.ToPaths()))
                {
                    if (path.Count < 2) continue;
                    var pts = path.Transformed(m).Flatten(tol);
                    ctx.BeginFigure(_view.ToScreen(pts[0]), false);
                    for (var i = 1; i < pts.Count; i++) ctx.LineTo(_view.ToScreen(pts[i]));
                    ctx.EndFigure(path.Closed);
                }
                foreach (var (from, to, kind) in part.Operations.Where(o => o.Enabled).Select(ToolpathOf).OfType<Toolpath>()
                             .SelectMany(tp => tp.DisplaySegments(tol)))
                {
                    if (kind == MoveKind.Rapid) continue;
                    ctx.BeginFigure(_view.ToScreen(m.Transform(from.XY)), false);
                    ctx.LineTo(_view.ToScreen(m.Transform(to.XY)));
                    ctx.EndFigure(false);
                }
            }
            dc.DrawGeometry(null, outline, geo);
            if (extents.IsEmpty) continue;
            var corners = new[] { extents.Min.XY, new Point2(extents.Max.X, extents.Min.Y), extents.Max.XY, new Point2(extents.Min.X, extents.Max.Y) }
                .Select(c => _view.ToScreen(m.Transform(c))).ToArray();
            for (var i = 0; i < 4; i++) dc.DrawLine(extentPen, corners[i], corners[(i + 1) % 4]);
            // Red triangle at the copy's base corner, with its machining order number.
            var b = corners[0];
            var tri = new StreamGeometry();
            using (var ctx = tri.Open())
            {
                ctx.BeginFigure(b, true);
                ctx.LineTo(new Point(b.X + 12, b.Y));
                ctx.LineTo(new Point(b.X, b.Y - 12));
                ctx.EndFigure(true);
            }
            dc.DrawGeometry(marker, null, tri);
            var text = new FormattedText(index.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
            dc.DrawText(text, new Point(b.X + 3, b.Y - 26));
        }
    }

    // ----------------------------------------------------------------- toolpath view filter

    private IEnumerable<(Part Part, MachineOp Op, Toolpath Tp)> VisibleToolpaths()
    {
        var session = _session!;
        foreach (var part in session.Document.Parts)
        {
            if (!part.Enabled) continue;
            foreach (var op in part.Operations)
            {
                if (!op.Enabled || !session.Toolpaths.TryGetValue(op, out var tp)) continue;
                if (Settings.ToolpathVisibilitySelectedOnly && !ReferenceEquals(session.Inspected, op) && !ReferenceEquals(session.Inspected, part)) continue;
                yield return (part, op, tp);
            }
        }
    }

    /// <summary>All visible toolpath paths in execution order, with the distinct Z levels (top first).</summary>
    public (List<ToolpathPath> Paths, List<double> ZLevels) FilterPaths()
    {
        if (_session == null) return ([], []);
        var paths = VisibleToolpaths().SelectMany(t => t.Tp.Paths(FlattenTolerance)).ToList();
        var levels = new List<double>();
        foreach (var z in paths.Select(p => p.Z).OrderByDescending(z => z))
            if (levels.Count == 0 || Math.Abs(levels[^1] - z) > 1e-6) levels.Add(z);
        return (paths, levels);
    }

    private static int LevelIndex(List<double> levels, double z)
    {
        for (var i = 0; i < levels.Count; i++)
            if (Math.Abs(levels[i] - z) <= 1e-6) return i + 1;
        return levels.Count;
    }

    private void RenderFilteredToolpaths(DrawingContext dc)
    {
        var f = Settings.Filter;
        var (paths, levels) = FilterPaths();
        var a = (byte)Math.Clamp(f.Alpha * 255, 0, 255);
        Pen P(Color c, IDashStyle? dash = null) => new(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)), f.LineWidth, dash);
        var cutPen = P(Settings.ToolpathColor);
        var arcPen = P(Settings.ToolpathArcColor);
        var rapidPen = P(Settings.RapidColor, DashStyle.Dash);
        var donePen = P(f.CutColor);
        var highlightPen = P(f.HighlightColor);
        highlightPen.Thickness = f.LineWidth + 1;

        var n = Math.Clamp(f.Index, 1, Math.Max(1, paths.Count));
        var zn = Math.Clamp(f.ZIndex, 1, Math.Max(1, levels.Count));
        for (var idx = 0; idx < paths.Count; idx++)
        {
            var path = paths[idx];
            var number = idx + 1;
            var level = LevelIndex(levels, path.Z);
            IPen? pen = null; // null = normal colours
            if (f.IndexEnabled)
            {
                if (number > n) continue;
                if (f.ZEnabled && level > zn) continue;
                if (number < n)
                {
                    if (!f.ShowCut) continue;
                    pen = donePen;
                }
                else if (f.Highlight) pen = highlightPen;
            }
            else if (f.ZEnabled)
            {
                if (level != zn) continue;
                if (f.Highlight) pen = highlightPen;
            }
            foreach (var (from, to, kind) in path.Segments)
            {
                if (kind == MoveKind.Rapid)
                {
                    if (Settings.ShowRapids) dc.DrawLine(rapidPen, _view.ToScreen(from), _view.ToScreen(to));
                    continue;
                }
                dc.DrawLine(pen ?? (kind == MoveKind.Feed ? cutPen : arcPen), _view.ToScreen(from), _view.ToScreen(to));
            }
        }
    }
}

/// <summary>Settings of the Toolpath View Filter window (Ctrl+F).</summary>
public sealed class ToolpathFilterSettings
{
    public bool IndexEnabled { get; set; }
    /// <summary>1-based path number in execution order.</summary>
    public int Index { get; set; } = 1;
    public bool ZEnabled { get; set; }
    /// <summary>1-based Z level, counted from the top.</summary>
    public int ZIndex { get; set; } = 1;
    public bool ShowCut { get; set; } = true;
    public Color CutColor { get; set; } = Color.FromRgb(120, 120, 140);
    public bool Highlight { get; set; } = true;
    public Color HighlightColor { get; set; } = Colors.Yellow;
    public double LineWidth { get; set; } = 1;
    public double Alpha { get; set; } = 1;

    public bool Active => IndexEnabled || ZEnabled;
}
