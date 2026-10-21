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
using static BamCam.Core.Localization.Tr;

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
        LoadSettings();

        _session = new DocumentSession(NewDocument())
        {
            Snapshot = CbWriter.ToString,
            Restore = CbReader.Parse,
        };
        View.Session = _session;

        _session.Message += (_, m) => Log(m);
        _session.Changed += (_, _) => OnDocumentChanged();
        _session.DocumentReplaced += (_, _) => OnDocumentReplaced();
        _session.SelectionChanged += (_, _) => SyncTreeSelection();
        _session.InspectedChanged += (_, _) =>
        {
            // While the objects of an operation are re-selected the operation stays in the property grid.
            if (_pickingOp != null && _session.Inspected != _pickingOp)
            {
                _session.Inspect(_pickingOp);
                return;
            }
            Properties.SelectedObject = _session.Inspected ?? _session.Document;
        };

        View.CursorMoved += (_, p) => CoordText.Text = $"X: {p.X:0.###}  Y: {p.Y:0.###}";
        View.PromptChanged += (_, text) =>
        {
            PromptText.Text = text;
            PromptBar.IsVisible = !string.IsNullOrEmpty(text);
        };
        View.ContextMenuRequested += (_, p) => _ = ShowViewContextMenu(p);
        View.EntityDoubleClicked += (_, ent) => EditInView(ent);
        Properties.CollectionEditRequested += (_, e) => EditPoints(e.Target, e.Property);

        Properties.ValueChanging += (_, _) =>
        {
            // Configuration edits are not part of the drawing's undo history.
            if (Properties.SelectedObject is not AppSettings) _session.BeginEdit();
        };
        Properties.ValueChanged += (_, key) =>
        {
            if (Properties.SelectedObject is AppSettings) OnSettingsEditedInGrid();
            else OnPropertyEdited();
            if (key == nameof(Core.Cam.MachineOp.PrimitiveIds) && Properties.SelectedObject is Core.Cam.MachineOp op) CheckPrimitiveIds(op);
        };

        DrawingTree.SelectionChanged += OnTreeSelectionChanged;
        DrawingTree.KeyDown += OnTreeKeyDown;
        DrawingTree.DoubleTapped += OnTreeDoubleTapped;
        SystemTree.SelectionChanged += OnSystemTreeSelectionChanged;

        BuildMenu();
        BuildToolbar();

        _system.Reload();
        _system.DefaultPostName = _settings.DefaultPostProcessor;
        foreach (var m in _pendingLogs.Concat(_system.LoadMessages)) Log(m);
        if (_system.Posts.All(p => !p.Name.Equals(_settings.DefaultPostProcessor, StringComparison.OrdinalIgnoreCase)))
            Log(F("Default post processor '{0}' not found; using '{1}'.", _settings.DefaultPostProcessor, _system.Resolve(null).Name));
        BuildSystemTree();
        ApplyLanguage();
        Core.Localization.Tr.Changed += OnLanguageChanged;
        Closed += (_, _) => Core.Localization.Tr.Changed -= OnLanguageChanged;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        Closing += OnClosing;
        Closed += (_, _) => SaveSettings();

        OnDocumentReplaced();
        Log(T("BamCam ready. Draw or open a drawing (.cb / .dxf)."));
    }

    // ------------------------------------------------------------------ messages / status

    private void Log(string message)
    {
        Messages.Text += (Messages.Text?.Length > 0 ? "\n" : "") + message;
        Messages.CaretIndex = Messages.Text?.Length ?? 0;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Logs an unexpected error with its stack trace (message window and crash.log in the system folder).</summary>
    public void ReportError(Exception ex)
    {
        Log(F("Internal error: {0} (details in crash.log)", ex.Message));
        try
        {
            Directory.CreateDirectory(SystemLibrary.SystemFolder);
            File.AppendAllText(Path.Combine(SystemLibrary.SystemFolder, "crash.log"), $"{DateTime.Now:u}\n{ex}\n\n");
        }
        catch
        {
            // Logging must never throw.
        }
        if (_screenshotMode) Log(ex.ToString());
    }

    private void UpdateTitle()
    {
        var doc = _session.Document;
        var name = doc.FilePath != null ? Path.GetFileName(doc.FilePath) : doc.Name;
        Title = $"BamCam - {name}{(doc.IsModified ? " *" : "")}";
        UnitsText.Text = T(doc.Units.ToString());
        SnapText.Text = string.Join(" ", new[] { View.Settings.SnapToGrid ? T("Grid") : "", View.Settings.SnapToObjects ? T("Points") : "" }.Where(s => s.Length > 0));
    }

    private void OnDocumentChanged()
    {
        RebuildTree();
        Properties.Refresh();
        UpdateTitle();
    }

    private void OnDocumentReplaced()
    {
        _settings.ApplyGrid(View.Settings, _session.Document.Units);
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
            if (changed.Count > 0) Log(F("{0}: style values updated ({1}).", op.Name, string.Join(", ", changed)));
        }
        if (_session.Inspected is CadDocument)
            OnDocumentReplacedSettingsOnly();
        if (_session.Inspected is Core.Cam.Part part) UpdateNesting([part]);
        _session.NotifyChanged();
    }

    private void OnDocumentReplacedSettingsOnly()
    {
        var doc = _session.Document;
        _settings.ApplyGrid(View.Settings, doc.Units);
        View.Settings.ShowRapids = doc.ShowRapids;
        View.Settings.ShowStock = doc.ShowStock;
        View.Settings.ToolpathVisibilitySelectedOnly = doc.ToolpathVisibility == ToolpathVisibility.SelectedOnly;
        View.Invalidate();
    }

    // ------------------------------------------------------------------ files

    private static FilePickerFileType CbType => new(T("BamCam / CamBam drawing")) { Patterns = ["*.cb"] };
    private static FilePickerFileType DxfType => new(T("DXF drawing")) { Patterns = ["*.dxf"] };
    private static readonly FilePickerFileType NcType = new("G-code") { Patterns = ["*.nc", "*.tap", "*.gcode", "*.ngc", "*.cnc"] };
    private static FilePickerFileType AllType => new(T("All files")) { Patterns = ["*"] };

    public void OpenFileOnStartup(string path) => Dispatcher.UIThread.Post(() => OpenPath(path));

    /// <summary>Diagnostics: renders the window to a PNG after start-up and exits.</summary>
    public void CaptureScreenshotAndExit(string path, bool generate, IReadOnlyDictionary<string, string>? options = null)
    {
        options ??= new Dictionary<string, string>();
        _screenshotMode = true;
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
            if (options.ContainsKey("--config"))
            {
                LeftTabs.SelectedIndex = 1;
                Properties.SelectedObject = _settings;
            }
            if (options.TryGetValue("--context", out var ctx))
            {
                // Right-click at a drawing coordinate "x,y": logs the context menu entries.
                var v = ctx.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                var menu = ShowViewContextMenu(View.WorldToScreen(new(v[0], v[1])));
                static string Dump(ItemsControl m, string indent) => string.Join("\n", m.Items.Select(i => i is MenuItem mi
                    ? indent + mi.Header + (mi.Items.Count > 0 ? "\n" + Dump(mi, indent + "  ") : "")
                    : indent + "----"));
                var dump = $"--context: {_session.Selection.Count} selected\n{Dump(menu, "  ")}";
                Log(dump);
                File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bamcam-context.txt"), dump);
            }
            // --pick <MOP name>: re-select its drawing objects (the clicks below change the selection), then Enter.
            var pickOp = options.TryGetValue("--pick", out var pickName)
                ? _session.Document.Parts.SelectMany(p => p.Operations).FirstOrDefault(o => o.Name == pickName)
                : null;
            if (pickOp != null) PickOperationObjects(pickOp);
            if (options.TryGetValue("--click", out var clicks))
            {
                // One or more drawing coordinates "x,y;x,y", optionally prefixed with "ctrl:".
                foreach (var c in clicks.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var ctrl = c.StartsWith("ctrl:");
                    var v = c.Replace("ctrl:", "").Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    try
                    {
                        View.SimulateClick(new(v[0], v[1]), ctrl ? KeyModifiers.Control : KeyModifiers.None);
                        Log($"--click {v[0]},{v[1]}: {_session.Selection.Count} selected.");
                    }
                    catch (Exception ex)
                    {
                        ReportError(ex);
                    }
                }
            }
            if (pickOp != null)
            {
                View.ForwardKeyToTool(Key.Enter, KeyModifiers.None);
                Log($"--pick: {pickOp.Name} = {string.Join(", ", pickOp.PrimitiveIds)}");
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
        var answer = await Dialogs.Ask(this, "BamCam", F("Save changes to {0}?", _session.Document.Name));
        if (answer == null) return false;
        if (answer == true) return await Save();
        return true;
    }

    private async void NewDrawing()
    {
        if (!await ConfirmDiscard()) return;
        var doc = NewDocument();
        _session.Replace(doc);
        Log(T("New drawing."));
    }

    private async void OpenDialog()
    {
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Open"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(T("Drawings")) { Patterns = ["*.cb", "*.dxf"] }, CbType, DxfType,
                new FilePickerFileType(T("STL mesh")) { Patterns = ["*.stl"] }, NcType, AllType],
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
                _session.Execute(T("Insert surface"), () => _session.Document.AddEntity(surface));
                _session.Select([surface]);
                Log(F("Inserted {0}: {1} triangles.", path, surface.Faces.Count / 3));
                View.ZoomToFit();
                return;
            }
            if (ext is not (".cb" or ".dxf"))
            {
                // Anything else is treated as G-code and back-plotted as an NC File operation.
                var nc = new Core.Cam.NcFileOp { Name = Path.GetFileNameWithoutExtension(path) };
                nc.SourceFile.Set(path);
                var part = _session.Document.ActivePart;
                _session.Execute(T("Insert NC file"), () => part.Operations.Add(nc));
                GenerateToolpaths(new Core.Post.PostScope(Operation: nc));
                View.ZoomToFit();
                return;
            }
            if (ext == ".dxf")
            {
                doc = DxfReader.Load(path, out var warnings);
                foreach (var w in warnings.Distinct()) Log(F("DXF: {0}", w));
            }
            else
            {
                doc = CbReader.Load(path);
            }
            _session.Replace(doc);
            AddRecentFile(path);
            var ops = doc.Parts.Sum(p => p.Operations.Count);
            Log(F("Opened {0}: {1} objects, {2} layers, {3} machining operations.", path, doc.AllEntities().Count(), doc.Layers.Count, ops));
            foreach (var op in doc.Parts.SelectMany(p => p.Operations).OfType<Core.Cam.UnsupportedOp>())
                Log(F("Note: operation '{0}' ({1}) is kept in the file but cannot be machined by this version.", op.Name, op.XmlName));
        }
        catch (Exception ex)
        {
            Log(F("Error opening {0}: {1}", path, ex.Message));
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
            Title = T("Save As"),
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
            CbWriter.Save(_session.Document, path, backups: _settings.FileBackups);
            AddRecentFile(path);
            Log(F("Saved {0}", path));
            UpdateTitle();
            return true;
        }
        catch (Exception ex)
        {
            Log(F("Error saving {0}: {1}", path, ex.Message));
            return false;
        }
    }

    private async void ExportDxf()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Export DXF"),
            SuggestedFileName = _session.Document.Name + ".dxf",
            DefaultExtension = "dxf",
            FileTypeChoices = [DxfType],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            DxfWriter.Save(_session.Document, path);
            Log(F("Exported {0}", path));
        }
        catch (Exception ex)
        {
            Log(F("DXF export failed: {0}", ex.Message));
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
