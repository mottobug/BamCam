using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BamCam.App.Drawing;
using BamCam.App.Services;
using BamCam.Core.Model;
using BamCam.IO.Cb;
using BamCam.IO.Dxf;

namespace BamCam.App.Views;

public partial class MainWindow : Window
{
    private readonly DocumentSession _session;
    private readonly SystemLibrary _system = new();
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        TextEntity.OutlineProvider = TextOutlines.Build;

        _session = new DocumentSession(CadDocument.CreateNew())
        {
            Snapshot = CbWriter.ToString,
            Restore = CbReader.Parse,
        };
        View.Session = _session;

        _session.Message += (_, m) => Log(m);
        _session.Changed += (_, _) => OnDocumentChanged();
        _session.DocumentReplaced += (_, _) => OnDocumentReplaced();
        _session.SelectionChanged += (_, _) => SyncTreeSelection();
        _session.InspectedChanged += (_, _) => Properties.SelectedObject = _session.Inspected ?? _session.Document;

        View.CursorMoved += (_, p) => CoordText.Text = $"X: {p.X:0.###}  Y: {p.Y:0.###}";
        View.PromptChanged += (_, text) =>
        {
            PromptText.Text = text;
            PromptBar.IsVisible = !string.IsNullOrEmpty(text);
        };
        View.ContextMenuRequested += (_, p) => ShowViewContextMenu(p);
        View.EntityDoubleClicked += (_, ent) =>
        {
            if (ent is TextEntity t) _ = EditTextEntity(t);
            else View.SetTool(new EditPointsTool(ent));
        };

        Properties.ValueChanging += (_, _) => _session.BeginEdit();
        Properties.ValueChanged += (_, _) => OnPropertyEdited();

        DrawingTree.SelectionChanged += OnTreeSelectionChanged;
        DrawingTree.KeyDown += OnTreeKeyDown;
        DrawingTree.DoubleTapped += OnTreeDoubleTapped;
        SystemTree.SelectionChanged += OnSystemTreeSelectionChanged;

        BuildMenu();
        BuildToolbar();

        _system.Reload();
        foreach (var m in _system.LoadMessages) Log(m);
        BuildSystemTree();

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        Closing += OnClosing;

        OnDocumentReplaced();
        Log("BamCam ready. Draw or open a drawing (.cb / .dxf).");
    }

    // ------------------------------------------------------------------ messages / status

    private void Log(string message)
    {
        Messages.Text += (Messages.Text?.Length > 0 ? "\n" : "") + message;
        Messages.CaretIndex = Messages.Text?.Length ?? 0;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void UpdateTitle()
    {
        var doc = _session.Document;
        var name = doc.FilePath != null ? Path.GetFileName(doc.FilePath) : doc.Name;
        Title = $"BamCam - {name}{(doc.IsModified ? " *" : "")}";
        UnitsText.Text = doc.Units.ToString();
        SnapText.Text = (View.Settings.SnapToGrid ? "Grid " : "") + (View.Settings.SnapToObjects ? "Points" : "");
    }

    private void OnDocumentChanged()
    {
        RebuildTree();
        Properties.Refresh();
        UpdateTitle();
    }

    private void OnDocumentReplaced()
    {
        View.Settings.ApplyUnits(_session.Document.Units);
        View.Settings.ShowRapids = _session.Document.ShowRapids;
        View.Settings.ShowStock = _session.Document.ShowStock;
        View.Settings.ToolpathVisibilitySelectedOnly = _session.Document.ToolpathVisibility == ToolpathVisibility.SelectedOnly;
        RebuildTree();
        Properties.SelectedObject = _session.Inspected ?? _session.Document;
        UpdateTitle();
        Dispatcher.UIThread.Post(() => View.ZoomToFit(), DispatcherPriority.Background);
    }

    private void OnPropertyEdited()
    {
        if (_session.Inspected is TextEntity t) t.InvalidateOutlines();
        if (_session.Inspected is Core.Cam.MachineOp op)
        {
            var changed = LibraryFiles.ApplyStyle(_session.Document, op);
            if (changed.Count > 0) Log($"{op.Name}: style values updated ({string.Join(", ", changed)}).");
        }
        if (_session.Inspected is CadDocument)
            OnDocumentReplacedSettingsOnly();
        if (_session.Inspected is Core.Cam.Part part) UpdateNesting([part]);
        _session.NotifyChanged();
    }

    private void OnDocumentReplacedSettingsOnly()
    {
        var doc = _session.Document;
        View.Settings.ApplyUnits(doc.Units);
        View.Settings.ShowRapids = doc.ShowRapids;
        View.Settings.ShowStock = doc.ShowStock;
        View.Settings.ToolpathVisibilitySelectedOnly = doc.ToolpathVisibility == ToolpathVisibility.SelectedOnly;
        View.Invalidate();
    }

    // ------------------------------------------------------------------ files

    private static readonly FilePickerFileType CbType = new("BamCam / CamBam drawing") { Patterns = ["*.cb"] };
    private static readonly FilePickerFileType DxfType = new("DXF drawing") { Patterns = ["*.dxf"] };
    private static readonly FilePickerFileType NcType = new("G-code") { Patterns = ["*.nc", "*.tap", "*.gcode", "*.ngc", "*.cnc"] };
    private static readonly FilePickerFileType AllType = new("All files") { Patterns = ["*"] };

    public void OpenFileOnStartup(string path) => Dispatcher.UIThread.Post(() => OpenPath(path));

    /// <summary>Diagnostics: renders the window to a PNG after start-up and exits.</summary>
    public void CaptureScreenshotAndExit(string path, bool generate, IReadOnlyDictionary<string, string>? options = null)
    {
        options ??= new Dictionary<string, string>();
        Opened += async (_, _) =>
        {
            await Task.Delay(1200);
            if (generate)
            {
                View.Settings.ToolpathVisibilitySelectedOnly = false;
                await GenerateToolpathsAsync();
                _session.ClearSelection();
                View.ZoomToFit();
                await Task.Delay(400);
            }
            if (options.TryGetValue("--inspect", out var name))
            {
                var doc = _session.Document;
                object? target = doc.Parts.FirstOrDefault(p => p.Name == name)
                                 ?? (object?)doc.Parts.SelectMany(p => p.Operations).FirstOrDefault(o => o.Name == name);
                if (target == null) Log($"--inspect: '{name}' not found.");
                _session.Inspect(target);
                if (target is Core.Cam.Part)
                {
                    // Include the nest copies in the view.
                    View.ZoomToFit();
                    var box = Core.Cam.NestingLayout.PartExtents(doc, (Core.Cam.Part)target, _session.Toolpaths);
                    foreach (var m in Core.Cam.NestingLayout.CopyTransforms((Core.Cam.Part)target))
                    {
                        var a = m.Transform(box.Min.XY);
                        var b = m.Transform(box.Max.XY);
                        box = box.Union(Core.Geometry.BoundingBox.FromCorners(a, b));
                    }
                    View.View.Fit(box, View.Bounds.Size);
                }
            }
            if (options.TryGetValue("--drag-tab", out var drag))
            {
                var v = drag.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                var moved = View.SimulateTabDrag(new(v[0], v[1]), new(v[2], v[3]));
                Log(moved ? "--drag-tab: tab moved." : "--drag-tab: no tab at the start point.");
            }
            if (options.TryGetValue("--filter", out var filter))
            {
                var v = filter.Split(',');
                var f = View.Settings.Filter;
                f.IndexEnabled = v[0].Length > 0 && v[0] != "-";
                if (f.IndexEnabled) f.Index = int.Parse(v[0], CultureInfo.InvariantCulture);
                f.ZEnabled = v.Length > 1;
                if (f.ZEnabled) f.ZIndex = int.Parse(v[1], CultureInfo.InvariantCulture);
                ShowToolpathFilter();
            }
            if (options.Count > 0)
            {
                View.Invalidate();
                await Task.Delay(400);
            }
            var size = new Avalonia.PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
            bmp.Render(this);
            bmp.Save(path);
            if (_filterWindow is { } fw)
            {
                using var fbmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)fw.Bounds.Width, (int)fw.Bounds.Height));
                fbmp.Render(fw);
                fbmp.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-filter.png"));
            }
            File.WriteAllText(Path.ChangeExtension(path, ".log"), Messages.Text);
            _closingConfirmed = true; // diagnostic edits are never saved
            Close();
        };
    }

    private async Task<bool> ConfirmDiscard()
    {
        if (!_session.Document.IsModified) return true;
        var answer = await Dialogs.Ask(this, "BamCam", $"Save changes to {_session.Document.Name}?");
        if (answer == null) return false;
        if (answer == true) return await Save();
        return true;
    }

    private async void NewDrawing()
    {
        if (!await ConfirmDiscard()) return;
        var doc = CadDocument.CreateNew();
        _session.Replace(doc);
        Log("New drawing.");
    }

    private async void OpenDialog()
    {
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Drawings") { Patterns = ["*.cb", "*.dxf"] }, CbType, DxfType,
                new FilePickerFileType("STL mesh") { Patterns = ["*.stl"] }, NcType, AllType],
        });
        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is { } path) OpenPath(path);
    }

    private void OpenPath(string path)
    {
        try
        {
            CadDocument doc;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".stl")
            {
                var surface = IO.Stl.StlReader.Load(path);
                _session.Execute("Insert surface", () => _session.Document.AddEntity(surface));
                _session.Select([surface]);
                Log($"Inserted {path}: {surface.Faces.Count / 3} triangles.");
                View.ZoomToFit();
                return;
            }
            if (ext is not (".cb" or ".dxf"))
            {
                // Anything else is treated as G-code and back-plotted as an NC File operation.
                var nc = new Core.Cam.NcFileOp { Name = Path.GetFileNameWithoutExtension(path) };
                nc.SourceFile.Set(path);
                var part = _session.Document.ActivePart;
                _session.Execute("Insert NC file", () => part.Operations.Add(nc));
                GenerateToolpaths(new Core.Post.PostScope(Operation: nc));
                View.ZoomToFit();
                return;
            }
            if (ext == ".dxf")
            {
                doc = DxfReader.Load(path, out var warnings);
                foreach (var w in warnings.Distinct()) Log("DXF: " + w);
            }
            else
            {
                doc = CbReader.Load(path);
            }
            _session.Replace(doc);
            var ops = doc.Parts.Sum(p => p.Operations.Count);
            Log($"Opened {path}: {doc.AllEntities().Count()} objects, {doc.Layers.Count} layers, {ops} machining operations.");
            foreach (var op in doc.Parts.SelectMany(p => p.Operations).OfType<Core.Cam.UnsupportedOp>())
                Log($"Note: operation '{op.Name}' ({op.XmlName}) is kept in the file but cannot be machined by this version.");
        }
        catch (Exception ex)
        {
            Log($"Error opening {path}: {ex.Message}");
        }
    }

    private async Task<bool> Save()
    {
        var doc = _session.Document;
        if (doc.FilePath == null || !doc.FilePath.EndsWith(".cb", StringComparison.OrdinalIgnoreCase)) return await SaveAs();
        return SaveTo(doc.FilePath);
    }

    private async Task<bool> SaveAs()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save As",
            SuggestedFileName = _session.Document.Name + ".cb",
            DefaultExtension = "cb",
            FileTypeChoices = [CbType],
        });
        if (file?.TryGetLocalPath() is not { } path) return false;
        _session.Document.Name = Path.GetFileNameWithoutExtension(path);
        return SaveTo(path);
    }

    private bool SaveTo(string path)
    {
        try
        {
            CbWriter.Save(_session.Document, path, backups: 3);
            Log($"Saved {path}");
            UpdateTitle();
            return true;
        }
        catch (Exception ex)
        {
            Log($"Error saving {path}: {ex.Message}");
            return false;
        }
    }

    private async void ExportDxf()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export DXF",
            SuggestedFileName = _session.Document.Name + ".dxf",
            DefaultExtension = "dxf",
            FileTypeChoices = [DxfType],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            DxfWriter.Save(_session.Document, path);
            Log($"Exported {path}");
        }
        catch (Exception ex)
        {
            Log($"DXF export failed: {ex.Message}");
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files?.FirstOrDefault()?.TryGetLocalPath() is { } path) OpenPath(path);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed || !_session.Document.IsModified) return;
        e.Cancel = true;
        if (await ConfirmDiscard())
        {
            _closingConfirmed = true;
            Close();
        }
    }

    // ------------------------------------------------------------------ keyboard

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var inText = FocusManager?.GetFocusedElement() is TextBox;
        if (inText && e.KeyModifiers == KeyModifiers.None) return;
        if (inText && e.Key is Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.A) return;

        if (HandleShortcut(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        // Keys for the drawing view (tools, navigation) when focus is elsewhere.
        if (FocusManager?.GetFocusedElement() is TreeView) return;
        if (View.ForwardKeyToTool(e.Key, e.KeyModifiers) || View.HandleNavigationKey(e.Key, e.KeyModifiers))
            e.Handled = true;
    }
}
