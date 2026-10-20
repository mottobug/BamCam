using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.App.Services;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.App.Drawing;

/// <summary>
/// The main drawing window: renders entities, grid, axes, stock and toolpaths, and routes
/// mouse/keyboard input to navigation and the active <see cref="ViewTool"/>.
/// </summary>
public sealed partial class DrawingView : Control, IToolHost
{
    private DocumentSession? _session;
    private ViewTool _tool = new SelectTool();
    private readonly ViewTransform _view = new();
    private bool _initialFitDone;

    private enum DragMode { None, Pan, Rotate }
    private DragMode _drag;
    private Point _dragStart;
    private Point _dragLast;
    private bool _dragMoved;
    private double _rotYaw0, _rotPitch0;

    private Point _cursorScreen;
    private Point2? _snapMarker;

    public DrawingView()
    {
        Focusable = true;
        ClipToBounds = true;
        // The initial selection tool must be attached too, or the first click has no host.
        _tool.Attach(this);
    }

    public ViewSettings Settings { get; } = new();

    public DocumentSession? Session
    {
        get => _session;
        set
        {
            if (_session != null)
            {
                _session.Changed -= OnSessionChanged;
                _session.SelectionChanged -= OnSessionChanged;
                _session.ToolpathsChanged -= OnSessionChanged;
                _session.InspectedChanged -= OnSessionChanged;
                _session.DocumentReplaced -= OnDocumentReplaced;
            }
            _session = value;
            if (_session != null)
            {
                _session.Changed += OnSessionChanged;
                _session.SelectionChanged += OnSessionChanged;
                _session.ToolpathsChanged += OnSessionChanged;
                _session.InspectedChanged += OnSessionChanged;
                _session.DocumentReplaced += OnDocumentReplaced;
            }
            _initialFitDone = false;
            InvalidateVisual();
        }
    }

    DocumentSession IToolHost.Session => _session ?? throw new InvalidOperationException("No session");
    public ViewTransform View => _view;

    public ViewTool Tool => _tool;

    /// <summary>Raised with the (snapped) world coordinate under the cursor.</summary>
    public event EventHandler<Point2>? CursorMoved;

    /// <summary>Raised when the active tool changes its prompt text.</summary>
    public event EventHandler<string?>? PromptChanged;

    /// <summary>Raised when an object is double-clicked with the selection tool.</summary>
    public event EventHandler<Entity>? EntityDoubleClicked;

    /// <summary>Raised on right-click without drag, so the window can show a context menu.</summary>
    public event EventHandler<Point>? ContextMenuRequested;

    public void SetTool(ViewTool? tool)
    {
        _tool.OnDetached();
        _tool = tool ?? new SelectTool();
        _tool.Attach(this);
        InvalidateVisual();
    }

    public void EndTool() => SetTool(null);

    public void SetPrompt(string? text) => PromptChanged?.Invoke(this, text);

    public void Invalidate() => InvalidateVisual();

    private void OnSessionChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnDocumentReplaced(object? sender, EventArgs e)
    {
        _initialFitDone = false;
        InvalidateVisual();
    }

    // ----------------------------------------------------------------- view commands

    public void ZoomToFit()
    {
        if (_session == null) return;
        var box = _session.Document.Bounds();
        foreach (var tp in _session.Toolpaths.Values) box = box.Union(tp.Bounds());
        if (box.IsEmpty) box = BoundingBox.FromCorners(Settings.GridMin, Settings.GridMax);
        _view.Fit(box, Bounds.Size);
        InvalidateVisual();
    }

    public void ResetView()
    {
        _view.ResetRotation();
        ZoomToFit();
    }

    public void SetStandardView(string plane)
    {
        switch (plane)
        {
            case "XZ": _view.SetRotation(0, -Math.PI / 2); break;
            case "YZ": _view.SetRotation(-Math.PI / 2, -Math.PI / 2); break;
            case "ISO": _view.SetRotation(-Math.PI / 4, -Math.PI / 3); break;
            default: _view.ResetRotation(); break;
        }
        ZoomToFit();
    }

    public void ZoomBy(double factor)
    {
        _view.ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);
        InvalidateVisual();
    }

    // ----------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        var pos = pt.Position;
        var props = pt.Properties;

        if (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (e.ClickCount == 2)
            {
                ResetView();
                return;
            }
            BeginDrag(DragMode.Rotate, pos, e);
            return;
        }
        if (props.IsMiddleButtonPressed)
        {
            BeginDrag(DragMode.Pan, pos, e);
            return;
        }
        if (props.IsRightButtonPressed) return;

        if (props.IsLeftButtonPressed && e.ClickCount == 2 && _tool is SelectTool && HitTest(pos).FirstOrDefault() is { } hit)
        {
            EntityDoubleClicked?.Invoke(this, hit);
            return;
        }

        if (props.IsLeftButtonPressed && e.ClickCount == 1 && BeginTabDrag(pos))
        {
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        var world = Snap(_view.ToWorld(pos), pos);
        _tool.PointerPressed(pos, world, props, e.KeyModifiers);
        e.Pointer.Capture(this);
    }

    /// <summary>
    /// Diagnostics: runs a hover, left press and release at a drawing coordinate through the same code
    /// paths as real mouse input (tab dragging, active tool, selection).
    /// </summary>
    /// <summary>Diagnostics: screen position of a drawing coordinate.</summary>
    public Point WorldToScreen(Point2 worldPoint) => _view.ToScreen(worldPoint);

    public void SimulateClick(Point2 worldPoint, KeyModifiers mods = KeyModifiers.None)
    {
        var pos = _view.ToScreen(worldPoint);
        _cursorScreen = pos;
        UpdateHoverCursor(pos);
        _tool.PointerMoved(pos, Snap(_view.ToWorld(pos), pos), mods);
        if (BeginTabDrag(pos))
        {
            EndTabDrag();
            return;
        }
        var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        _tool.PointerPressed(pos, Snap(_view.ToWorld(pos), pos), props, mods);
        _tool.PointerReleased(pos, Snap(_view.ToWorld(pos), pos), MouseButton.Left, mods);
        InvalidateVisual();
    }

    private void BeginDrag(DragMode mode, Point pos, PointerPressedEventArgs e)
    {
        _drag = mode;
        _dragStart = pos;
        _dragLast = pos;
        _dragMoved = false;
        _rotYaw0 = _view.Yaw;
        _rotPitch0 = _view.Pitch;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        _cursorScreen = pos;

        if (_drag != DragMode.None)
        {
            if (Math.Abs(pos.X - _dragStart.X) + Math.Abs(pos.Y - _dragStart.Y) > 3) _dragMoved = true;
            if (_drag == DragMode.Pan)
            {
                _view.Pan += pos - _dragLast;
            }
            else
            {
                var d = pos - _dragStart;
                _view.SetRotation(_rotYaw0 + d.X * 0.01, _rotPitch0 + d.Y * 0.01);
            }
            _dragLast = pos;
            InvalidateVisual();
            return;
        }

        if (_dragTab != null)
        {
            CursorMoved?.Invoke(this, _view.ToWorld(pos));
            DragTab(pos);
            return;
        }
        UpdateHoverCursor(pos);

        var world = Snap(_view.ToWorld(pos), pos);
        CursorMoved?.Invoke(this, world);
        _tool.PointerMoved(pos, world, e.KeyModifiers);
        if (_snapMarker != null || Settings.SnapToObjects) InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pos = e.GetPosition(this);
        e.Pointer.Capture(null);

        if (_dragTab != null)
        {
            EndTabDrag();
            return;
        }

        if (_drag != DragMode.None)
        {
            var wasPan = _drag == DragMode.Pan;
            _drag = DragMode.None;
            // A middle click without movement is a "finish" command for tools.
            if (wasPan && !_dragMoved)
                _tool.PointerReleased(pos, Snap(_view.ToWorld(pos), pos), MouseButton.Middle, e.KeyModifiers);
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            ContextMenuRequested?.Invoke(this, pos);
            return;
        }

        var world = Snap(_view.ToWorld(pos), pos);
        _tool.PointerReleased(pos, world, e.InitialPressMouseButton, e.KeyModifiers);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var factor = Math.Pow(1.2, e.Delta.Y);
        _view.ZoomAt(e.GetPosition(this), factor);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_tool.KeyDown(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        if (HandleNavigationKey(e.Key, e.KeyModifiers)) e.Handled = true;
    }

    /// <summary>Keyboard navigation; public so the main window can forward keys.</summary>
    public bool HandleNavigationKey(Key key, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Shift)) return false; // Shift+arrows are nudges, handled by the window.
        const double step = 40;
        switch (key)
        {
            case Key.Left: _view.Pan += new Vector(step, 0); break;
            case Key.Right: _view.Pan += new Vector(-step, 0); break;
            case Key.Up: _view.Pan += new Vector(0, step); break;
            case Key.Down: _view.Pan += new Vector(0, -step); break;
            case Key.PageDown:
            case Key.Add:
                ZoomBy(1.25); return true;
            case Key.PageUp:
            case Key.Subtract:
                ZoomBy(0.8); return true;
            case Key.Home: ResetView(); return true;
            default: return false;
        }
        InvalidateVisual();
        return true;
    }

    /// <summary>Forwards a key to the active tool (used by the window for global shortcuts).</summary>
    public bool ForwardKeyToTool(Key key, KeyModifiers mods) => _tool.KeyDown(key, mods);

    // ----------------------------------------------------------------- snapping & picking

    public Point2 Snap(Point2 world, Point screen)
    {
        _snapMarker = null;
        if (Settings.SnapToObjects && _session != null)
        {
            const double radius = 8;
            var best = radius;
            Point2? found = null;
            foreach (var layer in _session.Document.Layers)
            {
                if (!layer.Visible) continue;
                foreach (var ent in layer.Entities)
                foreach (var sp in SnapPoints(ent))
                {
                    var s = _view.ToScreen(sp);
                    var d = Math.Abs(s.X - screen.X) + Math.Abs(s.Y - screen.Y);
                    if (d < best)
                    {
                        best = d;
                        found = sp;
                    }
                }
            }
            if (found != null)
            {
                _snapMarker = found;
                return found.Value;
            }
        }
        if (Settings.SnapToGrid)
        {
            var g = Settings.GridMinor;
            return new Point2(Math.Round(world.X / g) * g, Math.Round(world.Y / g) * g);
        }
        return world;
    }

    private static IEnumerable<Point2> SnapPoints(Entity e)
    {
        switch (e)
        {
            case CircleEntity c:
                var center = c.Transform.Transform(c.Center).XY;
                yield return center;
                break;
            case ArcEntity a:
                yield return a.Transform.Transform(a.Center).XY;
                break;
            case PointListEntity pl:
                foreach (var p in pl.WorldPoints()) yield return p.XY;
                yield break;
            case TextEntity:
            case SurfaceEntity:
                yield break;
        }
        foreach (var path in e.ToPaths())
        foreach (var v in path.Vertices)
            yield return v.XY;
    }

    public IReadOnlyList<Entity> HitTest(Point screen, double pixelTolerance = 5)
    {
        var hits = new List<(Entity E, double D)>();
        if (_session == null) return [];
        foreach (var layer in _session.Document.Layers)
        {
            if (!layer.Visible || layer.Locked) continue;
            foreach (var ent in layer.Entities)
            {
                var d = ScreenDistance(ent, screen);
                if (d <= pixelTolerance) hits.Add((ent, d));
            }
        }
        return hits.OrderBy(h => h.D).Select(h => h.E).ToList();
    }

    private double ScreenDistance(Entity ent, Point screen)
    {
        var best = double.PositiveInfinity;
        var tol = FlattenTolerance;
        foreach (var path in ent.ToPaths())
        {
            var pts = path.Flatten(tol);
            if (pts.Count == 1)
            {
                var s = _view.ToScreen(path.StartPoint);
                best = Math.Min(best, Distance(s, screen));
                continue;
            }
            var prev = _view.ToScreen(pts[0]);
            var n = path.Closed ? pts.Count + 1 : pts.Count;
            for (var i = 1; i < n; i++)
            {
                var cur = _view.ToScreen(pts[i % pts.Count]);
                best = Math.Min(best, SegmentDistance(screen, prev, cur));
                prev = cur;
            }
        }
        // Clicking inside a text outline or region counts as a hit.
        if (best > 0 && ent is TextEntity or RegionEntity && _view.IsTopView)
        {
            var w = _view.ToWorld(screen);
            if (ent.Bounds().Contains(w) && ent is TextEntity) best = 0;
            else if (ent is RegionEntity r && r.ToPaths().First().Contains(w)) best = 0;
        }
        return best;
    }

    public IReadOnlyList<Entity> WindowSelect(Point a, Point b, bool crossing)
    {
        if (_session == null) return [];
        var rect = new Rect(a, b).Normalize();
        var result = new List<Entity>();
        var tol = FlattenTolerance;
        foreach (var layer in _session.Document.Layers)
        {
            if (!layer.Visible || layer.Locked) continue;
            foreach (var ent in layer.Entities)
            {
                var pts = ent.ToPaths().SelectMany(p => p.Flatten(tol)).Select(p => _view.ToScreen(p)).ToList();
                if (pts.Count == 0) continue;
                var inside = crossing ? pts.Any(rect.Contains) : pts.All(rect.Contains);
                if (inside) result.Add(ent);
            }
        }
        return result;
    }

    private double FlattenTolerance => Math.Max(_view.ToWorldLength(0.35), 1e-5);

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double SegmentDistance(Point p, Point a, Point b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var len2 = abx * abx + aby * aby;
        if (len2 < 1e-12) return Distance(p, a);
        var t = Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2, 0, 1);
        return Distance(p, new Point(a.X + abx * t, a.Y + aby * t));
    }

    // ----------------------------------------------------------------- rendering

    public override void Render(DrawingContext dc)
    {
        var size = Bounds.Size;
        dc.FillRectangle(new SolidColorBrush(Settings.Background), new Rect(size));
        if (_session == null) return;

        if (!_initialFitDone && size.Width > 1)
        {
            _initialFitDone = true;
            var box = _session.Document.Bounds();
            if (box.IsEmpty)
            {
                _view.Scale = 4;
                _view.Pan = new Point(size.Width * 0.15, size.Height * 0.85);
            }
            else
            {
                _view.Fit(box, size);
            }
        }

        using (dc.PushClip(new Rect(size)))
        {
            if (Settings.ShowGrid) RenderGrid(dc, size);
            if (Settings.ShowAxis) RenderAxes(dc, size);
            if (Settings.ShowStock) RenderStock(dc);
            if (Settings.ShowToolpaths) RenderToolpaths(dc, cutWidthsPass: true);
            RenderEntities(dc);
            if (Settings.ShowToolpaths)
            {
                if (Settings.Filter.Active) RenderFilteredToolpaths(dc);
                else RenderToolpaths(dc, cutWidthsPass: false);
            }
            RenderNests(dc);
            RenderTabs(dc);
            _tool.RenderOverlay(dc, _view);
            RenderSnapMarker(dc);
        }
    }

    private void RenderGrid(DrawingContext dc, Size size)
    {
        var minor = Settings.GridMinor;
        var major = Settings.GridMajor;
        var min = Settings.GridMin;
        var max = Settings.GridMax;
        var minorPen = new Pen(new SolidColorBrush(Settings.GridMinorColor), 1);
        var majorPen = new Pen(new SolidColorBrush(Settings.GridMajorColor), 1);
        var drawMinor = _view.ToScreenLength(minor) >= 5;

        void Line(Point2 a, Point2 b, IPen pen) => dc.DrawLine(pen, _view.ToScreen(a), _view.ToScreen(b));

        if (drawMinor)
        {
            for (var x = Math.Ceiling(min.X / minor) * minor; x <= max.X + 1e-9; x += minor)
                if (!IsMultiple(x, major)) Line(new(x, min.Y), new(x, max.Y), minorPen);
            for (var y = Math.Ceiling(min.Y / minor) * minor; y <= max.Y + 1e-9; y += minor)
                if (!IsMultiple(y, major)) Line(new(min.X, y), new(max.X, y), minorPen);
        }
        if (_view.ToScreenLength(major) >= 4)
        {
            for (var x = Math.Ceiling(min.X / major) * major; x <= max.X + 1e-9; x += major)
                Line(new(x, min.Y), new(x, max.Y), majorPen);
            for (var y = Math.Ceiling(min.Y / major) * major; y <= max.Y + 1e-9; y += major)
                Line(new(min.X, y), new(max.X, y), majorPen);
        }
    }

    private static bool IsMultiple(double v, double step)
    {
        var r = Math.Abs(v / step - Math.Round(v / step));
        return r < 1e-6;
    }

    private void RenderAxes(DrawingContext dc, Size size)
    {
        var len = Math.Max(_view.ToWorldLength(Math.Max(size.Width, size.Height)) * 2, 1000);
        var o = Point3.Zero;
        dc.DrawLine(new Pen(Brushes.Red, 1), _view.ToScreen(new Point3(-len, 0, 0)), _view.ToScreen(new Point3(len, 0, 0)));
        dc.DrawLine(new Pen(Brushes.Lime, 1), _view.ToScreen(new Point3(0, -len, 0)), _view.ToScreen(new Point3(0, len, 0)));
        if (!_view.IsTopView)
            dc.DrawLine(new Pen(Brushes.DodgerBlue, 1), _view.ToScreen(new Point3(0, 0, -len)), _view.ToScreen(new Point3(0, 0, len)));
        _ = o;
    }

    private void RenderStock(DrawingContext dc)
    {
        var stock = _session!.Document.Machining.Stock;
        if (stock.IsEmpty) return;
        var a = stock.Min;
        var b = stock.Max;
        var c = stock.Color;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(160, c.R, c.G, c.B)), 1);
        Point3[] corners =
        [
            new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(a.X, b.Y, a.Z),
            new(a.X, a.Y, b.Z), new(b.X, a.Y, b.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z),
        ];
        int[][] edges = [[0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7]];
        foreach (var e in edges) dc.DrawLine(pen, _view.ToScreen(corners[e[0]]), _view.ToScreen(corners[e[1]]));
        if (_view.IsTopView)
        {
            var r = new Rect(_view.ToScreen(corners[3]), _view.ToScreen(corners[1])).Normalize();
            dc.FillRectangle(new SolidColorBrush(Color.FromArgb(28, c.R, c.G, c.B)), r);
        }
    }

    private void RenderEntities(DrawingContext dc)
    {
        var session = _session!;
        var selectPen = new Pen(new SolidColorBrush(Settings.SelectColor), 2);
        var highlightIds = session.Inspected is MachineOp op ? op.PrimitiveIds.ToHashSet() : null;
        var anySelected = session.Selection.Count > 0;
        var tol = FlattenTolerance;

        foreach (var layer in session.Document.Layers)
        {
            if (!layer.Visible) continue;
            var c = layer.Color;
            var alpha = (byte)Math.Clamp(layer.Alpha * 255, 0, 255);
            if (anySelected && Settings.SelectFade) alpha = (byte)(alpha * 0.6);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)), Math.Max(layer.PenWidth, 1));
            foreach (var ent in layer.Entities)
            {
                var selected = session.IsSelected(ent) || (highlightIds?.Contains(ent.Id) ?? false);
                DrawEntity(dc, ent, selected ? selectPen : pen, tol);
            }
        }
    }

    private void DrawEntity(DrawingContext dc, Entity ent, IPen pen, double tol)
    {
        if (ent is PointListEntity pl)
        {
            foreach (var p in pl.WorldPoints()) DrawCross(dc, _view.ToScreen(p), pen, 4);
            return;
        }
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            foreach (var path in ent.ToPaths())
            {
                if (path.Count == 0) continue;
                if (path.Count == 1)
                {
                    continue;
                }
                var pts = Flatten3D(path, tol);
                ctx.BeginFigure(_view.ToScreen(pts[0]), false);
                for (var i = 1; i < pts.Count; i++) ctx.LineTo(_view.ToScreen(pts[i]));
                ctx.EndFigure(path.Closed);
            }
        }
        dc.DrawGeometry(null, pen, geo);
    }

    /// <summary>Flattens a path keeping per-vertex Z (arcs interpolate Z linearly).</summary>
    private static List<Point3> Flatten3D(Path2 path, double tol)
    {
        var result = new List<Point3> { path.StartPoint };
        foreach (var s in path.Segments())
        {
            if (!s.IsArc)
            {
                result.Add(s.End);
                continue;
            }
            var arc = s.ToArc();
            var n = arc.SegmentCount(tol);
            for (var i = 1; i <= n; i++)
            {
                var t = (double)i / n;
                result.Add(arc.PointAt(t).ToPoint3(s.Start.Z + (s.End.Z - s.Start.Z) * t));
            }
        }
        if (path.Closed && result.Count > 1) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static void DrawCross(DrawingContext dc, Point p, IPen pen, double r)
    {
        dc.DrawLine(pen, new Point(p.X - r, p.Y - r), new Point(p.X + r, p.Y + r));
        dc.DrawLine(pen, new Point(p.X - r, p.Y + r), new Point(p.X + r, p.Y - r));
    }

    private void RenderToolpaths(DrawingContext dc, bool cutWidthsPass)
    {
        var session = _session!;
        var doc = session.Document;
        if (cutWidthsPass && !(doc.ShowCutWidths && _view.IsTopView)) return;

        var a = (byte)Math.Clamp(Settings.Filter.Alpha * 255, 0, 255);
        var w = Settings.Filter.LineWidth;
        SolidColorBrush B(Color c) => new(Color.FromArgb(a, c.R, c.G, c.B));
        var cutPen = new Pen(B(Settings.ToolpathColor), w);
        var arcPen = new Pen(B(Settings.ToolpathArcColor), w);
        var rapidPen = new Pen(B(Settings.RapidColor), w, DashStyle.Dash);

        foreach (var (_, op, tp) in VisibleToolpaths())
        {
            if (cutWidthsPass)
            {
                var width = _view.ToScreenLength(op.EffectiveToolDiameter(doc));
                if (width < 1) continue;
                var c = Settings.CutWidthColor;
                var widthPen = new Pen(new SolidColorBrush(c), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    var open = false;
                    foreach (var (from, to, kind) in tp.DisplaySegments(FlattenTolerance))
                    {
                        if (kind == MoveKind.Rapid)
                        {
                            if (open) ctx.EndFigure(false);
                            open = false;
                            continue;
                        }
                        if (!open)
                        {
                            ctx.BeginFigure(_view.ToScreen(from.XY), false);
                            open = true;
                        }
                        ctx.LineTo(_view.ToScreen(to.XY));
                    }
                    if (open) ctx.EndFigure(false);
                }
                dc.DrawGeometry(null, widthPen, geo);
                continue;
            }

            foreach (var (from, to, kind) in tp.DisplaySegments(FlattenTolerance))
            {
                if (kind == MoveKind.Rapid)
                {
                    if (Settings.ShowRapids) dc.DrawLine(rapidPen, _view.ToScreen(from), _view.ToScreen(to));
                }
                else
                {
                    dc.DrawLine(kind == MoveKind.Feed ? cutPen : arcPen, _view.ToScreen(from), _view.ToScreen(to));
                }
            }
            if (Settings.ShowDirectionArrows) DrawStartArrow(dc, tp, cutPen);
        }
    }

    private void DrawStartArrow(DrawingContext dc, Toolpath tp, IPen pen)
    {
        // Arrow at the first cutting move showing the cut direction.
        foreach (var (from, to, kind) in tp.DisplaySegments(FlattenTolerance))
        {
            if (kind == MoveKind.Rapid) continue;
            var a = _view.ToScreen(from);
            var b = _view.ToScreen(to);
            var d = b - a;
            var len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (len < 1e-3) continue;
            var ux = d.X / len;
            var uy = d.Y / len;
            var tip = new Point(a.X + ux * 14, a.Y + uy * 14);
            dc.DrawLine(pen, a, tip);
            dc.DrawLine(pen, tip, new Point(tip.X - ux * 6 - uy * 4, tip.Y - uy * 6 + ux * 4));
            dc.DrawLine(pen, tip, new Point(tip.X - ux * 6 + uy * 4, tip.Y - uy * 6 - ux * 4));
            return;
        }
    }

    private void RenderSnapMarker(DrawingContext dc)
    {
        if (_snapMarker is not { } p) return;
        var s = _view.ToScreen(p);
        dc.DrawRectangle(null, new Pen(Brushes.Yellow, 1), new Rect(s.X - 5, s.Y - 5, 10, 10));
    }
}

/// <summary>Display options of the drawing view (View menu toggles and colours).</summary>
public sealed class ViewSettings
{
    public bool ShowGrid { get; set; } = true;
    public bool ShowAxis { get; set; } = true;
    public bool ShowToolpaths { get; set; } = true;
    public bool ShowRapids { get; set; } = true;
    public bool ShowStock { get; set; } = true;
    public bool ShowDirectionArrows { get; set; } = true;
    public bool ToolpathVisibilitySelectedOnly { get; set; }
    public bool SnapToGrid { get; set; } = true;
    public bool SnapToObjects { get; set; }
    public bool SelectFade { get; set; } = true;
    /// <summary>Show nest copies while their Part is selected.</summary>
    public bool ShowNests { get; set; } = true;
    /// <summary>Regenerate the toolpath while a holding tab is dragged (instead of on the next Generate Toolpaths).</summary>
    public bool TabDragToolpathRefresh { get; set; }
    public ToolpathFilterSettings Filter { get; } = new();

    public double GridMinor { get; set; } = 1;
    public double GridMajor { get; set; } = 10;
    public Point2 GridMin { get; set; } = new(-100, -100);
    public Point2 GridMax { get; set; } = new(100, 100);

    public Color Background { get; set; } = Color.FromRgb(50, 50, 60);
    public Color GridMinorColor { get; set; } = Color.FromRgb(58, 58, 68);
    public Color GridMajorColor { get; set; } = Color.FromRgb(80, 80, 90);
    public Color SelectColor { get; set; } = Colors.Red;
    public Color ToolpathColor { get; set; } = Color.FromRgb(35, 85, 205);
    public Color ToolpathArcColor { get; set; } = Color.FromRgb(0, 220, 0);
    public Color RapidColor { get; set; } = Color.FromRgb(255, 69, 0);
    public Color CutWidthColor { get; set; } = Color.FromRgb(0, 139, 139);

    public void ApplyUnits(DrawingUnits units)
    {
        if (units == DrawingUnits.Inches)
        {
            GridMinor = 1.0 / 16;
            GridMajor = 1;
            GridMin = new(-4, -4);
            GridMax = new(4, 4);
        }
        else
        {
            GridMinor = 1;
            GridMajor = 10;
            GridMin = new(-100, -100);
            GridMax = new(100, 100);
        }
    }
}
