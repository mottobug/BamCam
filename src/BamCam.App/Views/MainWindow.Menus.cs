using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.App.Drawing;
using BamCam.Core.Cam;
using BamCam.Core.Post;
using Path = Avalonia.Controls.Shapes.Path;

namespace BamCam.App.Views;

public partial class MainWindow
{
    private readonly Dictionary<(Key, KeyModifiers), Action> _shortcuts = [];
    private readonly List<(MenuItem Item, Func<bool> IsChecked)> _checkItems = [];
    private MenuItem? _postMenu;

    private MenuItem Item(string header, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        if (gesture != null)
        {
            item.InputGesture = KeyGesture.Parse(gesture);
            var g = item.InputGesture;
            _shortcuts[(g.Key, g.KeyModifiers)] = action;
        }
        return item;
    }

    private MenuItem Check(string header, Func<bool> get, Action<bool> set, string? gesture = null)
    {
        var item = Item(header, () => { }, gesture);
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = get();
        void Toggle()
        {
            set(!get());
            RefreshChecks();
            View.Invalidate();
            UpdateTitle();
        }
        item.Click += (_, _) => Toggle();
        if (gesture != null)
        {
            var g = KeyGesture.Parse(gesture);
            _shortcuts[(g.Key, g.KeyModifiers)] = Toggle;
        }
        _checkItems.Add((item, get));
        return item;
    }

    private void RefreshChecks()
    {
        foreach (var (item, get) in _checkItems) item.IsChecked = get();
    }

    private static MenuItem Sub(string header, params object[] items)
    {
        var m = new MenuItem { Header = header };
        foreach (var i in items) m.Items.Add(i is string s && s == "-" ? new Separator() : i);
        return m;
    }

    private void BuildMenu()
    {
        var s = View.Settings;
        MainMenu.Items.Clear();

        MainMenu.Items.Add(Sub("_File",
            Item("_New", NewDrawing, "Ctrl+N"),
            Item("_Open...", OpenDialog, "Ctrl+O"),
            Item("_Save", () => _ = Save(), "Ctrl+S"),
            Item("Save _As...", () => _ = SaveAs(), "Ctrl+Shift+S"),
            "-",
            Sub("_Export", Item("To _DXF...", ExportDxf)),
            "-",
            Item("E_xit", Close)));

        MainMenu.Items.Add(Sub("_Edit",
            Item("_Undo", Undo, "Ctrl+Z"),
            Item("_Redo", Redo, "Ctrl+Y"),
            "-",
            Item("Cu_t", () => _ = CutSelection(), "Ctrl+X"),
            Item("_Copy", () => _ = CopySelection(), "Ctrl+C"),
            Item("_Paste", () => _ = Paste(), "Ctrl+V"),
            Item("Paste Format", PasteFormat, "Ctrl+Shift+V"),
            Item("_Delete", DeleteSelection, "Delete"),
            "-",
            Item("Select _All", () => { _session.SelectAll(); View.Invalidate(); }, "Ctrl+A"),
            Item("Select All on Active Layer", () => { _session.Select(_session.Document.ActiveLayer.Entities); View.Invalidate(); }, "Ctrl+Shift+A"),
            "-",
            Item("E_xplode", Explode),
            Item("_Join...", Join, "Ctrl+J"),
            Item("_Offset...", () => Offset(false)),
            Item("Open Offset...", () => Offset(true)),
            Item("_Union", () => BooleanOp("Union"), "Ctrl+U"),
            Item("_Subtract", () => BooleanOp("Subtract")),
            Item("_Intersection", () => BooleanOp("Intersection")),
            Sub("Convert To",
                Item("_Polyline", ConvertToPolyline, "Ctrl+P"),
                Item("_Region", ConvertToRegion, "Ctrl+I")),
            Sub("Polyline",
                Item("_Reverse", ReversePolylines),
                Item("_Clean", CleanPolylines)),
            Sub("_Transform",
                Item("_Move", () => View.SetTool(new MoveTool()), "Ctrl+M"),
                Item("_Rotate", () => View.SetTool(new RotateTool()), "Ctrl+R"),
                Item("Resi_ze...", Resize, "Ctrl+E"),
                Item("M_irror", () => View.SetTool(new MirrorTool())),
                Item("_Array Copy...", ArrayCopy),
                Item("_Polar Array Copy...", PolarArrayCopy),
                Item("_Center (Extents)", CenterExtents),
                Item("Apply Transformations", ApplyTransformations))));

        MainMenu.Items.Add(Sub("_View",
            Item("_Reset", () => View.ResetView(), "Home"),
            Item("Zoom To _Fit", () => View.ZoomToFit()),
            "-",
            Item("_XY Plane", () => View.SetStandardView("XY")),
            Item("X_Z Plane", () => View.SetStandardView("XZ")),
            Item("_YZ Plane", () => View.SetStandardView("YZ")),
            Item("_Isometric", () => View.SetStandardView("ISO")),
            "-",
            Check("Show _Toolpaths", () => s.ShowToolpaths, v => s.ShowToolpaths = v),
            Check("Show Cut _Widths", () => _session.Document.ShowCutWidths, v => _session.Document.ShowCutWidths = v),
            Check("Show _Rapids", () => s.ShowRapids, v => { s.ShowRapids = v; _session.Document.ShowRapids = v; }),
            Check("Show _Stock", () => s.ShowStock, v => { s.ShowStock = v; _session.Document.ShowStock = v; }),
            Check("Show Direction Arrows", () => s.ShowDirectionArrows, v => s.ShowDirectionArrows = v),
            Check("Show _Grid", () => s.ShowGrid, v => s.ShowGrid = v),
            Check("Show _Axis", () => s.ShowAxis, v => s.ShowAxis = v),
            "-",
            Check("Snap to _Grid", () => s.SnapToGrid, v => s.SnapToGrid = v, "Ctrl+G"),
            Check("Snap to _Objects", () => s.SnapToObjects, v => s.SnapToObjects = v)));

        MainMenu.Items.Add(Sub("_Draw",
            Item("_Line", () => View.SetTool(new PolylineTool(linesOnly: true))),
            Item("_Polyline", () => View.SetTool(new PolylineTool())),
            Item("_Arc", () => View.SetTool(new ArcTool())),
            Item("_Circle", () => View.SetTool(new CircleTool())),
            Item("_Rectangle", () => View.SetTool(new RectangleTool())),
            Sub("Point _List",
                Item("_Draw", () => View.SetTool(new PointListTool())),
                Item("Di_vide Geometry...", DividePoints),
                Item("_Centers", CenterPoints)),
            Item("_Text...", DrawText)));

        _postMenu = Sub("Post Processor");
        _postMenu.SubmenuOpened += (_, _) => FillPostMenu();
        MainMenu.Items.Add(Sub("_Machining",
            Item("_Profile", () => CreateOp(new ProfileOp())),
            Item("P_ocket", () => CreateOp(new PocketOp())),
            Item("_Drill", () => CreateOp(new DrillOp())),
            Item("_Engrave", () => CreateOp(new EngraveOp())),
            Item("_3D Profile", () => CreateOp(new Profile3DOp())),
            "-",
            Item("New Part", NewPart),
            "-",
            Item("_Generate Toolpaths", () => GenerateToolpaths(), "Ctrl+T"),
            Item("Produce G_code", () => _ = ProduceGCode(PostScope.All), "Ctrl+W"),
            Item("Edit Gcode", EditGCode),
            "-",
            _postMenu,
            Item("Machining Options", () => _session.Inspect(_session.Document.Machining))));

        MainMenu.Items.Add(Sub("_Tools",
            Item("_Measure", () => View.SetTool(new MeasureTool((a, b) => ReportMeasure(a, b)))),
            Item("Get Object _Extremas", ReportExtremas),
            Item("_Clear Messages", () => Messages.Text = ""),
            "-",
            Item("Reload Post Processors", () => { _system.Reload(); BuildSystemTree(); Log($"{_system.Posts.Count} post processors loaded."); }),
            Item("Browse System Folder", () => OpenFolder(Services.SystemLibrary.SystemFolder))));

        MainMenu.Items.Add(Sub("_Help",
            Item("_About BamCam", () => _ = Dialogs.Message(this, "About BamCam",
                "BamCam – cross-platform 2.5D CAD/CAM\n\nAvalonia " + typeof(Avalonia.Application).Assembly.GetName().Version +
                ", .NET " + Environment.Version + "\nReads and writes .cb drawings and .cbpp post processors."))));

        // Single-key draw shortcuts (active when no text field has focus).
        _shortcuts[(Key.P, KeyModifiers.None)] = () => View.SetTool(new PolylineTool());
        _shortcuts[(Key.C, KeyModifiers.None)] = () => { if (View.Tool is not PolylineTool) View.SetTool(new CircleTool()); else View.ForwardKeyToTool(Key.C, KeyModifiers.None); };
        _shortcuts[(Key.R, KeyModifiers.None)] = () => View.SetTool(new RectangleTool());
        _shortcuts[(Key.A, KeyModifiers.None)] = () => View.SetTool(new ArcTool());
        _shortcuts[(Key.D, KeyModifiers.None)] = () => View.SetTool(new PointListTool());
        _shortcuts[(Key.T, KeyModifiers.None)] = DrawText;
        _shortcuts[(Key.M, KeyModifiers.None)] = () => View.SetTool(new MeasureTool((a, b) => ReportMeasure(a, b)));
        _shortcuts[(Key.F2, KeyModifiers.None)] = () => _ = RenameSelected();
    }

    private bool HandleShortcut(Key key, KeyModifiers mods)
    {
        // Letter keys only when the drawing view is idle or the shortcut belongs to the active tool.
        if (mods == KeyModifiers.None && key is >= Key.A and <= Key.Z && View.Tool is not SelectTool and not PolylineTool)
            return false;
        if (mods.HasFlag(KeyModifiers.Shift) && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            Nudge(key, mods.HasFlag(KeyModifiers.Control));
            return true;
        }
        if (_shortcuts.TryGetValue((key, mods), out var action))
        {
            action();
            return true;
        }
        return false;
    }

    private void FillPostMenu()
    {
        if (_postMenu == null) return;
        _postMenu.Items.Clear();
        var current = _session.Document.Machining.PostProcessor;
        var def = new MenuItem { Header = $"(Default: {_system.DefaultPostName})", ToggleType = MenuItemToggleType.Radio, IsChecked = string.IsNullOrEmpty(current) };
        def.Click += (_, _) => SetPost("");
        _postMenu.Items.Add(def);
        foreach (var p in _system.Posts)
        {
            var item = new MenuItem { Header = p.Name, ToggleType = MenuItemToggleType.Radio, IsChecked = p.Name == current };
            item.Click += (_, _) => SetPost(p.Name);
            _postMenu.Items.Add(item);
        }
    }

    private void SetPost(string name)
    {
        _session.Execute("Set post processor", () => _session.Document.Machining.PostProcessor = name);
        Log($"Post processor: {(name.Length == 0 ? "(default)" : name)}");
    }

    // ------------------------------------------------------------------ toolbar

    private Button ToolButton(string iconKey, string tip, Action action)
    {
        var geo = (Geometry)this.FindResource(iconKey)!;
        var b = new Button
        {
            Content = new Path
            {
                Data = geo, Stroke = Brushes.Black, StrokeThickness = 1.4, Width = 18, Height = 18, Stretch = Stretch.Uniform,
                StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round,
            },
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) =>
        {
            action();
            View.Focus();
        };
        return b;
    }

    private static Control ToolSeparator() => new Rectangle { Width = 1, Height = 20, Fill = Brushes.Gray, Margin = new Thickness(4, 2) };

    private void BuildToolbar()
    {
        var p = ToolbarPanel;
        p.Children.Clear();
        p.Children.Add(ToolButton("IconNew", "New", NewDrawing));
        p.Children.Add(ToolButton("IconOpen", "Open", OpenDialog));
        p.Children.Add(ToolButton("IconSave", "Save", () => _ = Save()));
        p.Children.Add(ToolSeparator());
        p.Children.Add(ToolButton("IconUndo", "Undo", Undo));
        p.Children.Add(ToolButton("IconRedo", "Redo", Redo));
        p.Children.Add(ToolSeparator());

        var units = new ComboBox
        {
            ItemsSource = new[] { "Millimeters", "Inches" }, SelectedIndex = 0, Width = 110, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(units, "Drawing units");
        units.SelectionChanged += (_, _) =>
        {
            var u = units.SelectedIndex == 1 ? Core.Model.DrawingUnits.Inches : Core.Model.DrawingUnits.Millimeters;
            if (_session.Document.Units == u) return;
            _session.Execute("Change units", () => _session.Document.Units = u);
            View.Settings.ApplyUnits(u);
            View.Invalidate();
            UpdateTitle();
        };
        _session.DocumentReplaced += (_, _) => units.SelectedIndex = _session.Document.Units == Core.Model.DrawingUnits.Inches ? 1 : 0;
        p.Children.Add(units);
        p.Children.Add(ToolButton("IconAxis", "Show axis", () => { View.Settings.ShowAxis = !View.Settings.ShowAxis; RefreshChecks(); View.Invalidate(); }));
        p.Children.Add(ToolButton("IconGrid", "Show grid", () => { View.Settings.ShowGrid = !View.Settings.ShowGrid; RefreshChecks(); View.Invalidate(); }));
        p.Children.Add(ToolButton("IconSnap", "Snap to grid", () => { View.Settings.SnapToGrid = !View.Settings.SnapToGrid; RefreshChecks(); UpdateTitle(); }));
        p.Children.Add(ToolButton("IconFit", "Zoom to fit", () => View.ZoomToFit()));
        p.Children.Add(ToolSeparator());
        p.Children.Add(ToolButton("IconLine", "Line", () => View.SetTool(new PolylineTool(linesOnly: true))));
        p.Children.Add(ToolButton("IconPolyline", "Polyline (P)", () => View.SetTool(new PolylineTool())));
        p.Children.Add(ToolButton("IconArc", "Arc (A)", () => View.SetTool(new ArcTool())));
        p.Children.Add(ToolButton("IconCircle", "Circle (C)", () => View.SetTool(new CircleTool())));
        p.Children.Add(ToolButton("IconRect", "Rectangle (R)", () => View.SetTool(new RectangleTool())));
        p.Children.Add(ToolButton("IconPoints", "Point list (D)", () => View.SetTool(new PointListTool())));
        p.Children.Add(ToolButton("IconText", "Text (T)", DrawText));
        p.Children.Add(ToolSeparator());
        p.Children.Add(ToolButton("IconProfile", "Profile", () => CreateOp(new ProfileOp())));
        p.Children.Add(ToolButton("IconPocket", "Pocket", () => CreateOp(new PocketOp())));
        p.Children.Add(ToolButton("IconDrill", "Drill", () => CreateOp(new DrillOp())));
        p.Children.Add(ToolButton("IconEngrave", "Engrave", () => CreateOp(new EngraveOp())));
        p.Children.Add(ToolButton("Icon3D", "3D Profile", () => CreateOp(new Profile3DOp())));
        p.Children.Add(ToolSeparator());
        p.Children.Add(ToolButton("IconGenerate", "Generate toolpaths (Ctrl+T)", () => GenerateToolpaths()));
        p.Children.Add(ToolButton("IconGcode", "Produce G-code (Ctrl+W)", () => _ = ProduceGCode(PostScope.All)));
    }
}
