using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using BamCam.App.Drawing;
using BamCam.App.Services;
using BamCam.Core.Cad;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Post;
using BamCam.IO.Cb;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private List<Entity> Selected => _session.Selection.ToList();

    private bool RequireSelection(int min = 1)
    {
        if (_session.Selection.Count >= min) return true;
        Log(min == 1 ? T("Select drawing objects first.") : F("Select at least {0} drawing objects.", min));
        return false;
    }

    private void Undo()
    {
        if (!_session.CanUndo) return;
        _session.Undo();
        SetStatus(T("Undo"));
    }

    private void Redo()
    {
        if (!_session.CanRedo) return;
        _session.Redo();
        SetStatus(T("Redo"));
    }

    /// <summary>Replaces the selected entities with the results of an operation.</summary>
    private void ReplaceSelection(string description, Func<List<Entity>, IEnumerable<Entity>> op, bool removeSources = true)
    {
        if (!RequireSelection()) return;
        var sources = Selected;
        List<Entity> created = [];
        _session.Execute(description, () =>
        {
            var layer = sources[0].Layer ?? _session.Document.ActiveLayer;
            created = op(sources).ToList();
            if (removeSources)
                foreach (var s in sources) _session.Document.RemoveEntity(s);
            foreach (var c in created) _session.Document.AddEntity(c, layer);
        });
        _session.Select(created);
        Log(F("{0}: {1} object(s) created.", description, created.Count));
        WarnBrokenReferences(sources, removeSources);
    }

    private void WarnBrokenReferences(IEnumerable<Entity> removed, bool removedSources)
    {
        if (!removedSources) return;
        var ids = removed.Select(e => e.Id).ToHashSet();
        foreach (var op in _session.Document.Parts.SelectMany(p => p.Operations))
            if (op.PrimitiveIds.Any(ids.Contains))
                Log(F("Warning: '{0}' referenced objects that were replaced; reselect its drawing objects.", op.Name));
    }

    private void DeleteSelection()
    {
        if (FocusManager?.GetFocusedElement() is TreeView && DeleteTreeItem()) return;
        if (_session.Selection.Count == 0) return;
        var sel = Selected;
        _session.Execute(T("Delete"), () =>
        {
            foreach (var e in sel) _session.Document.RemoveEntity(e);
        });
        _session.ClearSelection();
    }

    private void Explode() => ReplaceSelection(T("Explode"), s => s.SelectMany(CadOps.Explode));

    private async void Join()
    {
        if (!RequireSelection()) return;
        var tol = await Dialogs.PromptNumber(this, T("Join"), T("Join tolerance:"), 0.001);
        if (tol == null) return;
        ReplaceSelection(T("Join"), s => CadOps.Join(s, tol.Value));
    }

    private async void Offset(bool open)
    {
        if (!RequireSelection()) return;
        var d = await Dialogs.PromptNumber(this, open ? T("Open Offset") : T("Offset"),
            open ? T("Offset distance:") : T("Offset distance (+ outside, - inside):"), 1);
        if (d == null) return;
        ReplaceSelection(open ? T("Open offset") : T("Offset"), s => CadOps.Offset(s, d.Value, open), removeSources: false);
    }

    private void BooleanOp(string kind)
    {
        if (!RequireSelection(kind == "Union" ? 1 : 2)) return;
        // Order matters for subtract/intersect: keep selection order as picked in the view/tree.
        ReplaceSelection(T(kind), s => kind switch
        {
            "Union" => CadOps.Union(s),
            "Subtract" => CadOps.Subtract(s),
            _ => CadOps.Intersect(s),
        });
    }

    private void ConvertToPolyline() => ReplaceSelection(T("Convert to polyline"), s => s.SelectMany(CadOps.ToPolylines));

    private void ConvertToRegion() => ReplaceSelection(T("Convert to region"), s => CadOps.ToRegions(s));

    private void ReversePolylines()
    {
        var plines = Selected.OfType<PolylineEntity>().ToList();
        if (plines.Count == 0) { Log(T("Select polylines to reverse.")); return; }
        _session.Execute(T("Reverse"), () => plines.ForEach(CadOps.Reverse));
    }

    private void CleanPolylines()
    {
        var plines = Selected.OfType<PolylineEntity>().ToList();
        _session.Execute(T("Clean"), () => plines.ForEach(p => CadOps.Clean(p)));
    }

    private async void Resize()
    {
        if (!RequireSelection()) return;
        var b = CadOps.Bounds(Selected);
        var text = await Dialogs.Prompt(this, T("Resize"),
            F("Current size {0:0.###} x {1:0.###}.\nScale in percent (e.g. 50), or new size \"width,height\":", b.Width, b.Height), "100");
        if (text == null) return;
        double sx, sy;
        var parts = text.Split(',');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, Inv, out var w) && double.TryParse(parts[1], NumberStyles.Float, Inv, out var h))
        {
            sx = b.Width > 0 ? w / b.Width : 1;
            sy = b.Height > 0 ? h / b.Height : 1;
        }
        else if (double.TryParse(text.TrimEnd('%'), NumberStyles.Float, Inv, out var pct))
        {
            sx = sy = pct / 100;
        }
        else return;
        var c = b.Center.XY;
        var m = Matrix4.Translation(-c.X, -c.Y) * Matrix4.Scale(sx, sy) * Matrix4.Translation(c.X, c.Y);
        var sel = Selected;
        _session.Execute(T("Resize"), () => CadOps.Transform(sel, m));
        foreach (var t in sel.OfType<TextEntity>()) t.InvalidateOutlines();
    }

    private async void ArrayCopy()
    {
        if (!RequireSelection()) return;
        var count = await Dialogs.PromptNumber(this, T("Array Copy"), T("Number of copies:"), 1);
        if (count is not { } n || n < 1) return;
        var off = await Dialogs.Prompt(this, T("Array Copy"), T("Offset X,Y[,Z]:"), "10,0");
        if (off == null) return;
        var o = off.Split(',').Select(v => double.TryParse(v, NumberStyles.Float, Inv, out var x) ? x : 0).ToArray();
        var dx = o.ElementAtOrDefault(0);
        var dy = o.ElementAtOrDefault(1);
        var dz = o.ElementAtOrDefault(2);
        var sel = Selected;
        _session.Execute(T("Array copy"), () =>
        {
            for (var i = 1; i <= (int)n; i++)
            foreach (var e in sel)
            {
                var c = e.CloneEntity();
                c.Id = 0;
                c.ApplyTransform(Matrix4.Translation(dx * i, dy * i, dz * i));
                _session.Document.AddEntity(c, e.Layer);
            }
        });
    }

    private async void PolarArrayCopy()
    {
        if (!RequireSelection()) return;
        var count = await Dialogs.PromptNumber(this, T("Polar Array Copy"), T("Number of copies:"), 5);
        if (count is not { } n || n < 1) return;
        var ang = await Dialogs.PromptNumber(this, T("Polar Array Copy"), T("Rotation per copy (degrees, around origin):"), 360 / (n + 1));
        if (ang == null) return;
        var sel = Selected;
        _session.Execute(T("Polar array copy"), () =>
        {
            for (var i = 1; i <= (int)n; i++)
            foreach (var e in sel)
            {
                var c = e.CloneEntity();
                c.Id = 0;
                c.ApplyTransform(Matrix4.RotationZ(Angles.ToRadians(ang.Value * i)));
                _session.Document.AddEntity(c, e.Layer);
            }
        });
    }

    private void CenterExtents()
    {
        if (!RequireSelection()) return;
        var sel = Selected;
        var c = CadOps.Bounds(sel).Center;
        _session.Execute(T("Center"), () => CadOps.Transform(sel, Matrix4.Translation(-c.X, -c.Y)));
    }

    private void ApplyTransformations()
    {
        var sel = _session.Selection.Count > 0 ? Selected : _session.Document.AllEntities().ToList();
        _session.Execute(T("Apply transformations"), () => CadOps.Transform(sel, Matrix4.Identity));
    }

    private void Nudge(Key key, bool major)
    {
        if (_session.Selection.Count == 0) return;
        var step = major ? View.Settings.GridMajor : View.Settings.GridMinor;
        var (dx, dy) = key switch
        {
            Key.Left => (-step, 0.0),
            Key.Right => (step, 0.0),
            Key.Up => (0.0, step),
            _ => (0.0, -step),
        };
        var sel = Selected;
        _session.Execute(T("Move"), () => CadOps.Transform(sel, Matrix4.Translation(dx, dy)));
    }

    private async void DividePoints()
    {
        if (!RequireSelection()) return;
        var n = await Dialogs.PromptNumber(this, T("Divide Geometry"), T("Number of points:"), 6);
        if (n is not { } count || count < 1) return;
        ReplaceSelection(T("Divide geometry"), s => [CadOps.Divide(s, (int)count)], removeSources: false);
    }

    private void CenterPoints() => ReplaceSelection(T("Centers"), s =>
    {
        var pl = new PointListEntity();
        pl.Points.AddRange(s.Select(e => e switch
        {
            CircleEntity c => c.Transform.Transform(c.Center),
            ArcEntity a => a.Transform.Transform(a.Center),
            _ => e.Bounds().Center,
        }));
        return [pl];
    }, removeSources: false);

    private async void DrawText()
    {
        var text = await Dialogs.EditText(this, T("Text"), "Text");
        if (string.IsNullOrEmpty(text)) return;
        var height = _session.Document.Units == DrawingUnits.Inches ? 0.5 : 10;
        View.SetTool(new TextTool(text, height));
        View.Focus();
    }

    private void ReportMeasure(Point2 a, Point2 b)
    {
        var d = b - a;
        Log(F("Measure: distance {0:0.####}  dX {1:0.####}  dY {2:0.####}  angle {3:0.##}°", d.Length, d.X, d.Y, Angles.ToDegrees(d.Angle)));
    }

    private void ReportExtremas()
    {
        var b = CadOps.Bounds(_session.Selection.Count > 0 ? Selected : _session.Document.AllEntities());
        if (b.IsEmpty) { Log(T("Nothing to measure.")); return; }
        Log(F("Min: {0:0.####},{1:0.####},{2:0.####}  Max: {3:0.####},{4:0.####},{5:0.####}  Width {6:0.####}  Height {7:0.####}  Depth {8:0.####}",
            b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z, b.Width, b.Height, b.Depth));
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // Not all platforms support shell-opening folders.
        }
    }

    // ------------------------------------------------------------------ clipboard

    private const string ClipboardMarker = "BamCamClipboard";

    private string SerializeEntities(IEnumerable<Entity> entities)
    {
        var temp = new CadDocument { Name = ClipboardMarker, Units = _session.Document.Units };
        var layer = new Layer("Clipboard");
        temp.Layers.Add(layer);
        foreach (var e in entities)
        {
            var c = e.CloneEntity();
            c.Id = e.Id;
            temp.AddLoadedEntity(c, layer);
        }
        return CbWriter.ToString(temp);
    }

    private async Task CopySelection()
    {
        if (_session.Selection.Count == 0) return;
        var xml = SerializeEntities(_session.Selection);
        _internalClipboard = xml;
        if (Clipboard != null) await Clipboard.SetTextAsync(xml);
        Log(F("Copied {0} object(s).", _session.Selection.Count));
    }

    private string? _internalClipboard;
    private MachineOp? _clipboardOp;

    private async Task CutSelection()
    {
        await CopySelection();
        DeleteSelection();
    }

    private async Task Paste()
    {
        string? xml = null;
        if (Clipboard != null)
        {
            try { xml = await Clipboard.GetTextAsync(); } catch { /* ignore */ }
        }
        if (xml == null || !xml.Contains("<CADFile")) xml = _internalClipboard;
        if (xml == null) return;
        try
        {
            var temp = CbReader.Parse(xml);
            var pasted = new List<Entity>();
            var target = SelectedTreeLayer() ?? _session.Document.ActiveLayer;
            _session.Execute(T("Paste"), () =>
            {
                foreach (var e in temp.AllEntities())
                {
                    e.Id = 0;
                    pasted.Add(_session.Document.AddEntity(e, target));
                }
            });
            _session.Select(pasted);
        }
        catch (Exception ex)
        {
            Log(F("Paste failed: {0}", ex.Message));
        }
    }

    private void PasteFormat()
    {
        if (_clipboardOp == null || _session.Inspected is not MachineOp target || target.GetType() != _clipboardOp.GetType()) return;
        _session.Execute(T("Paste format"), () =>
        {
            foreach (var (name, p, _) in _clipboardOp.Parameters())
            {
                var t = target.FindParameter(name)!;
                t.State = p.State;
                t.BoxedValue = p.CloneParam().BoxedValue;
            }
        });
    }

    // ------------------------------------------------------------------ CAM

    private string UniqueOpName(string type)
    {
        var all = _session.Document.Parts.SelectMany(p => p.Operations).Select(o => o.Name).ToHashSet();
        for (var i = 1; ; i++)
            if (!all.Contains(type + i)) return type + i;
    }

    private void ApplyUnitDefaults(MachineOp op)
    {
        var doc = _session.Document;
        op.ToolDiameter.Value = doc.Machining.ToolDiameter > 0 ? doc.Machining.ToolDiameter : op.ToolDiameter.Value;
        if (doc.Machining.ToolNumber > 0) op.ToolNumber.Value = doc.Machining.ToolNumber;
        if (doc.Units != DrawingUnits.Inches) return;
        op.PlungeFeedrate.Value = 10;
        op.CutFeedrate.Value = 30;
        op.ClearancePlane.Value = 0.125;
        if (op is DepthOp d)
        {
            d.DepthIncrement.Value = 0.015;
            d.TargetDepth.Value = -0.04;
        }
    }

    private void CreateOp(MachineOp op)
    {
        var sel = Selected;
        if (op is Profile3DOp && !sel.Any(e => e is SurfaceEntity))
        {
            Log(T("Select surface (mesh) objects before inserting a 3D Profile operation. Import meshes with File > Open (.stl)."));
            return;
        }
        if (op is DrillOp ? !sel.Any() : !sel.Any(e => e is not PointListEntity))
        {
            Log(F("Select drawing objects before inserting a {0} operation.", T(op.DisplayType)));
            return;
        }
        op.Name = UniqueOpName(op.DisplayType.Replace(" ", ""));
        op.PrimitiveIds.AddRange(sel.Select(e => e.Id));
        var doc = _session.Document;
        var part = doc.ActivePart;
        _session.Execute(F("Insert {0}", T(op.DisplayType)), () =>
        {
            part.Operations.Add(op);
            if (CamLibraries.FindStyleLibrary(doc, part) != null) LibraryFiles.ApplyStyle(doc, op);
            else ApplyUnitDefaults(op);
            var stockEmpty = (part.Stock.IsEmpty ? doc.Machining.Stock : part.Stock).IsEmpty;
            if (op is Profile3DOp p3 && stockEmpty)
            {
                // Without stock: machine from the top of the selected meshes down to their lowest point.
                var b = Core.Cad.CadOps.Bounds(sel.OfType<SurfaceEntity>());
                p3.StockSurface.Set(Math.Round(b.Max.Z, 3));
                p3.TargetDepth.Set(Math.Round(b.Min.Z, 3));
            }
            if (op is DepthOp depth && (depth.TargetDepth.Value >= depth.StockSurface.Value && (stockEmpty || depth.TargetDepth.State != ParamState.Auto)))
                depth.TargetDepth.Set(doc.Units == DrawingUnits.Inches ? -0.04 : -1);
            if (op.ToolDiameter.Value <= 0 && CamLibraries.FindTool(doc, part, op.EffectiveToolNumber(doc)) == null)
                op.ToolDiameter.Set(doc.Units == DrawingUnits.Inches ? 0.125 : 3);
        });
        _session.Inspect(op);
        Log(F("{0} added to {1} ({2} object(s)). Set Target Depth and tool, then generate toolpaths.", op.Name, part.Name, sel.Count));
    }

    private void NewPart()
    {
        var doc = _session.Document;
        var part = new Part { Name = doc.UniquePartName() };
        _session.Execute(T("New part"), () =>
        {
            doc.Parts.Add(part);
            doc.ActivePartName = part.Name;
        });
        _session.Inspect(part);
    }

    private IEnumerable<(Part Part, MachineOp Op)> OpsInScope(PostScope scope) =>
        _session.Document.Parts
            .Where(p => scope.Part == null || p == scope.Part)
            .Where(p => p.Enabled || scope.Part != null || scope.Operation != null)
            .SelectMany(p => p.Operations.Select(o => (p, o)))
            .Where(x => scope.Operation == null || x.o == scope.Operation)
            .Where(x => x.o.Enabled || scope.Operation != null);

    private bool _generating;

    private void GenerateToolpaths(PostScope? scope = null) => _ = GenerateToolpathsAsync(scope);

    /// <summary>
    /// Generates toolpaths on a worker thread, one operation at a time, so the window keeps repainting.
    /// Editing is disabled meanwhile because generators read the live document.
    /// </summary>
    private async Task GenerateToolpathsAsync(PostScope? scope = null)
    {
        if (_generating) return;
        scope ??= PostScope.All;
        _generating = true;
        var content = (Control)Content!;
        content.IsEnabled = false;
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            await GenerateCore(scope);
        }
        finally
        {
            _generating = false;
            content.IsEnabled = true;
            Cursor = Cursor.Default;
            SetStatus(T("Ready"));
        }
    }

    private async Task GenerateCore(PostScope scope)
    {
        var sw = Stopwatch.StartNew();
        var count = 0;
        var ops = OpsInScope(scope).ToList();
        foreach (var (_, op) in ops)
        {
            SetStatus(F("Generating toolpaths: {0} ({1}/{2})…", op.Name, count + 1, ops.Count));
            var doc = _session.Document;
            // Like CamBam, Default values are taken from the CAM style when toolpaths are generated
            // (the values cached in the drawing may come from another style library version).
            BamCam.IO.Cb.LibraryFiles.ApplyStyle(doc, op);
            var tp = await Task.Run(() => ToolpathGenerator.Generate(doc, op));
            _session.SetToolpath(op, tp);
            foreach (var w in tp.Warnings) Log(F("Warning: {0}", w));
            var len = 0.0;
            Point3? pos = null;
            foreach (var m in tp.Moves.Where(m => m.IsMotion))
            {
                if (pos is { } p) len += Toolpath.MoveLength(p, m);
                pos = m.To;
            }
            var t = TimeSpan.FromSeconds(tp.EstimateSeconds());
            Log(F("{0}: {1} moves, length {2:0.#}, est. time {3:hh\\:mm\\:ss}", op.Name, tp.Moves.Count, len, t));
            count++;
        }
        if (count == 0) Log(T("No enabled machining operations."));
        else Log(F("Toolpaths generated in {0} ms.", sw.ElapsedMilliseconds));
        UpdateNesting(_session.Document.Parts.Where(p => scope.Part == null || p == scope.Part));
        View.Invalidate();
    }

    /// <summary>Re-lays out Grid / ISO Grid nest copies (spacing is measured between toolpaths).</summary>
    private void UpdateNesting(IEnumerable<Part> parts)
    {
        foreach (var part in parts)
            if (NestingLayout.Update(_session.Document, part, _session.Toolpaths))
                Log(F("{0}: {1} nest copies ({2}).", part.Name, part.NestingItems.Count, part.Nesting.NestMethod));
    }

    private ToolpathFilterWindow? _filterWindow;

    private void ShowToolpathFilter()
    {
        if (_filterWindow == null)
        {
            _filterWindow = new ToolpathFilterWindow(View);
            _filterWindow.Closed += (_, _) =>
            {
                // Closing the filter shows all toolpaths again.
                View.Settings.Filter.IndexEnabled = false;
                View.Settings.Filter.ZEnabled = false;
                View.Invalidate();
                _filterWindow = null;
            };
            _filterWindow.Show(this);
        }
        else
        {
            _filterWindow.Load();
            _filterWindow.Activate();
        }
    }

    private string ExpandOutFile(string pattern)
    {
        var doc = _session.Document;
        var name = pattern.Replace("{$cbfile.name}", doc.Name, StringComparison.OrdinalIgnoreCase);
        if (!Path.IsPathRooted(name) && doc.FilePath != null)
            name = Path.Combine(Path.GetDirectoryName(doc.FilePath)!, name);
        return name;
    }

    private async Task ProduceGCode(PostScope scope)
    {
        var doc = _session.Document;
        var ops = OpsInScope(scope).ToList();
        if (ops.Count == 0) { Log(T("Nothing to post: no enabled machining operations.")); return; }
        if (ops.Any(o => !_session.Toolpaths.ContainsKey(o.Op)) || doc.Machining.RebuildToolpathBeforePost == RebuildToolpath.Always)
            await GenerateToolpathsAsync(scope);

        var outFile = scope.Operation != null ? "" : scope.Part?.OutFile ?? doc.Machining.OutFile;
        string? path = string.IsNullOrWhiteSpace(outFile) ? null : ExpandOutFile(outFile);
        var showDialog = path == null || !Path.IsPathRooted(path) || _settings.OutFilePrompt == OutFilePrompt.Always;
        if (!showDialog && _settings.OutFilePrompt == OutFilePrompt.IfExists && File.Exists(path))
        {
            var answer = await Dialogs.Ask(this, T("Produce G-code"), F("{0}\nalready exists. Overwrite it?\n(No = choose another file)", path));
            if (answer == null) return;
            showDialog = answer == false;
        }
        if (showDialog)
        {
            var ext = _settings.DefaultGCodeExtension;
            var suggested = path != null && Path.IsPathRooted(path) ? Path.GetFileName(path)
                : scope.Operation != null ? $"{doc.Name}.{scope.Part?.Name ?? ops[0].Part.Name}.{scope.Operation.Name}{ext}"
                : scope.Part != null ? $"{doc.Name}.{scope.Part.Name}{ext}" : $"{doc.Name}{ext}";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Produce G-code"),
                SuggestedFileName = suggested,
                DefaultExtension = ext.TrimStart('.'),
                FileTypeChoices = [NcType, AllType],
            });
            if (file?.TryGetLocalPath() is not { } chosen) return;
            path = chosen;
            if (scope == PostScope.All)
            {
                var dir = doc.FilePath != null ? Path.GetDirectoryName(doc.FilePath) : null;
                var rel = dir != null && Path.GetDirectoryName(path) == dir ? Path.GetFileName(path) : path;
                _session.Execute(T("Set output file"), () => doc.Machining.OutFile = rel);
            }
        }

        var post = _system.Resolve(doc.Machining.PostProcessor);
        var result = GCodePoster.Post(doc, _session.Toolpaths, post, scope);
        foreach (var w in result.Warnings.Distinct()) Log(F("Post: {0}", w));
        try
        {
            File.WriteAllText(path, result.Text);
            _lastGCodePath = path;
            Log(F("G-code written to {0} ({1} lines, post processor '{2}').", path, result.LineCount, post.Name));
        }
        catch (Exception ex)
        {
            Log(F("Could not write {0}: {1}", path, ex.Message));
        }
    }

    private string? _lastGCodePath;

    private void EditGCode()
    {
        var path = _lastGCodePath ?? (string.IsNullOrWhiteSpace(_session.Document.Machining.OutFile) ? null : ExpandOutFile(_session.Document.Machining.OutFile));
        if (path == null || !File.Exists(path))
        {
            Log(T("No G-code file yet. Use Produce Gcode first."));
            return;
        }
        if (!string.IsNullOrWhiteSpace(_settings.GCodeEditor))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_settings.GCodeEditor, $"\"{path}\"") { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                Log(F("Could not start G-code editor '{0}': {1}. Using the internal editor.", _settings.GCodeEditor, ex.Message));
            }
        }
        new GCodeWindow(path).Show(this);
    }
}
