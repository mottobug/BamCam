using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.App.Drawing;
using BamCam.Core.Cam;
using BamCam.Core.Post;
using static BamCam.Core.Localization.Tr;
using Path = Avalonia.Controls.Shapes.Path;

namespace BamCam.App.Views;

public partial class MainWindow
{
    private readonly Dictionary<(Key, KeyModifiers), Action> _shortcuts = [];
    private readonly List<(MenuItem Item, Func<bool> IsChecked)> _checkItems = [];
    private MenuItem? _postMenu;

    /// <summary>True while the main menu is built: shortcuts and check items are registered only then (context menus reuse the same builders).</summary>
    private bool _registering;

    private MenuItem Item(string header, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = T(header) };
        item.Click += (_, _) => action();
        if (gesture != null)
        {
            item.InputGesture = KeyGesture.Parse(gesture);
            var g = item.InputGesture;
            if (_registering) _shortcuts[(g.Key, g.KeyModifiers)] = action;
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
            SaveSettings();
            View.Invalidate();
            UpdateTitle();
        }
        item.Click += (_, _) => Toggle();
        if (gesture != null && _registering)
        {
            var g = KeyGesture.Parse(gesture);
            _shortcuts[(g.Key, g.KeyModifiers)] = Toggle;
        }
        if (_registering) _checkItems.Add((item, get));
        return item;
    }

    private MenuItem RecentMenu()
    {
        _recentMenu = new MenuItem { Header = T("_Recent Files") };
        _recentMenu.Items.Add(new MenuItem { Header = T("(empty)"), IsEnabled = false });
        _recentMenu.SubmenuOpened += (_, _) => FillRecentMenu();
        return _recentMenu;
    }

    private void RefreshChecks()
    {
        foreach (var (item, get) in _checkItems) item.IsChecked = get();
    }

    private static MenuItem Sub(string header, params object[] items)
    {
        var m = new MenuItem { Header = T(header) };
        foreach (var i in items) m.Items.Add(i is string s && s == "-" ? new Separator() : i);
        return m;
    }

    private void BuildMenu()
    {
        _registering = true;
        _shortcuts.Clear();
        _checkItems.Clear();
        MainMenu.Items.Clear();

        MainMenu.Items.Add(Sub("_File",
            Item("_New", NewDrawing, "Ctrl+N"),
            Item("_Open...", OpenDialog, "Ctrl+O"),
            Item("_Save", () => _ = Save(), "Ctrl+S"),
            Item("Save _As...", () => _ = SaveAs(), "Ctrl+Shift+S"),
            "-",
            Sub("_Export", Item("To _DXF...", ExportDxf)),
            RecentMenu(),
            "-",
            Item("E_xit", Close)));

        MainMenu.Items.Add(Sub("_Edit", EditMenuItems()));
        MainMenu.Items.Add(Sub("_View", ViewMenuItems()));
        MainMenu.Items.Add(Sub("_Draw", DrawMenuItems()));
        _postMenu = Sub("Post Processor");
        _postMenu.SubmenuOpened += (_, _) => FillPostMenu();
        MainMenu.Items.Add(Sub("_Machining", MachiningMenuItems().Concat(["-", _postMenu,
            Item("Machining Options", () => _session.Inspect(_session.Document.Machining))]).ToArray()));
        MainMenu.Items.Add(Sub("_Tools",
            Item("_Measure", () => View.SetTool(new MeasureTool((a, b) => ReportMeasure(a, b)))),
            Item("Get Object _Extremas", ReportExtremas),
            Item("_Options...", ShowOptions),
            Item("_Clear Messages", () => Messages.Text = ""),
            "-",
            Item("Reload Post Processors", () => { _system.Reload(); BuildSystemTree(); Log(F("{0} post processors loaded.", _system.Posts.Count)); }),
            Item("Browse System Folder", () => OpenFolder(Services.SystemLibrary.SystemFolder))));

        MainMenu.Items.Add(Sub("_Help",
            Item("_About BamCam", () => _ = Dialogs.Message(this, T("About BamCam"),
                F("BamCam – cross-platform 2.5D CAD/CAM\n\nAvalonia {0}, .NET {1}\nReads and writes .cb drawings and .cbpp post processors.",
                    typeof(Avalonia.Application).Assembly.GetName().Version, Environment.Version)))));

        // Single-key draw shortcuts (active when no text field has focus).
        _shortcuts[(Key.P, KeyModifiers.None)] = () => View.SetTool(new PolylineTool());
        _shortcuts[(Key.C, KeyModifiers.None)] = () => { if (View.Tool is not PolylineTool) View.SetTool(new CircleTool()); else View.ForwardKeyToTool(Key.C, KeyModifiers.None); };
        _shortcuts[(Key.R, KeyModifiers.None)] = () => View.SetTool(new RectangleTool());
        _shortcuts[(Key.A, KeyModifiers.None)] = () => View.SetTool(new ArcTool());
        _shortcuts[(Key.D, KeyModifiers.None)] = () => View.SetTool(new PointListTool());
        _shortcuts[(Key.T, KeyModifiers.None)] = DrawText;
        _shortcuts[(Key.M, KeyModifiers.None)] = () => View.SetTool(new MeasureTool((a, b) => ReportMeasure(a, b)));
        _shortcuts[(Key.F2, KeyModifiers.None)] = () => _ = RenameSelected();
        _registering = false;
    }


    // ------------------------------------------------------------------ menu builders (main menu and context menus)

    private object[] EditMenuItems() =>
    [
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
            Item("_Edit Points...", () => { if (Selected.FirstOrDefault() is { } e) EditPoints(e); }),
            Item("_Reverse", ReversePolylines),
            Item("_Clean", CleanPolylines)),
        Sub("_Transform", TransformMenuItems()),
    ];

    private object[] TransformMenuItems() =>
    [
        Item("_Move", () => View.SetTool(new MoveTool()), "Ctrl+M"),
        Item("_Rotate", () => View.SetTool(new RotateTool()), "Ctrl+R"),
        Item("Resi_ze...", Resize, "Ctrl+E"),
        Item("M_irror", () => View.SetTool(new MirrorTool())),
        Item("_Array Copy...", ArrayCopy),
        Item("_Polar Array Copy...", PolarArrayCopy),
        Item("_Center (Extents)", CenterExtents),
        Item("Apply Transformations", ApplyTransformations),
    ];

    private object[] ViewMenuItems()
    {
        var s = View.Settings;
        return
        [
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
            Check("Show _Nests", () => s.ShowNests, v => s.ShowNests = v),
            Check("Holding Tab Drag Toolpath Refresh", () => s.TabDragToolpathRefresh, v => s.TabDragToolpathRefresh = v),
            Item("Toolpath View _Filter...", ShowToolpathFilter, "Ctrl+F"),
            "-",
            Check("Snap to _Grid", () => s.SnapToGrid, v => s.SnapToGrid = v, "Ctrl+G"),
            Check("Snap to _Objects", () => s.SnapToObjects, v => s.SnapToObjects = v),
        ];
    }

    private object[] DrawMenuItems() =>
    [
        Item("_Line", () => View.SetTool(new PolylineTool(linesOnly: true))),
        Item("_Polyline", () => View.SetTool(new PolylineTool())),
        Item("_Arc", () => View.SetTool(new ArcTool())),
        Item("_Spline", () => View.SetTool(new SplineTool())),
        Item("_Circle", () => View.SetTool(new CircleTool())),
        Item("_Rectangle", () => View.SetTool(new RectangleTool())),
        Sub("Point _List",
            Item("_Draw", () => View.SetTool(new PointListTool())),
            Item("Di_vide Geometry...", DividePoints),
            Item("_Centers", CenterPoints)),
        Item("_Text...", DrawText),
    ];

    private object[] MachiningMenuItems() =>
    [
        Item("_Profile", () => CreateOp(new ProfileOp())),
        Item("P_ocket", () => CreateOp(new PocketOp())),
        Item("_Drill", () => CreateOp(new DrillOp())),
        Item("_Engrave", () => CreateOp(new EngraveOp())),
        Item("_3D Profile", () => CreateOp(new Profile3DOp())),
        Item("_Lathe", () => CreateOp(new LatheOp())),
        "-",
        Item("New Part", NewPart),
        "-",
        Item("_Generate Toolpaths", () => GenerateToolpaths(), "Ctrl+T"),
        Item("Produce G_code", () => _ = ProduceGCode(PostScope.All), "Ctrl+W"),
        Item("Edit Gcode", EditGCode),
    ];
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
        var def = new MenuItem { Header = F("(Default: {0})", _system.DefaultPostName), ToggleType = MenuItemToggleType.Radio, IsChecked = string.IsNullOrEmpty(current) };
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
        _session.Execute(T("Set post processor"), () => _session.Document.Machining.PostProcessor = name);
        Log(F("Post processor: {0}", name.Length == 0 ? T("(default)") : name));
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
        ToolTip.SetTip(b, T(tip));
        b.Click += (_, _) =>
        {
            action();
            View.Focus();
        };
        return b;
    }

    private ComboBox? _unitsCombo;
    private bool _unitsHooked;

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
            ItemsSource = new[] { T("Millimeters"), T("Inches") }, SelectedIndex = _session.Document.Units == Core.Model.DrawingUnits.Inches ? 1 : 0, Width = 110, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(units, T("Drawing units"));
        units.SelectionChanged += (_, _) =>
        {
            var u = units.SelectedIndex == 1 ? Core.Model.DrawingUnits.Inches : Core.Model.DrawingUnits.Millimeters;
            if (_session.Document.Units == u) return;
            _session.Execute(T("Change units"), () => _session.Document.Units = u);
            _settings.ApplyGrid(View.Settings, u);
            View.Invalidate();
            UpdateTitle();
        };
        // The toolbar is rebuilt when the language changes: subscribe once and update the current combo box.
        _unitsCombo = units;
        if (!_unitsHooked)
        {
            _unitsHooked = true;
            _session.DocumentReplaced += (_, _) => _unitsCombo!.SelectedIndex = _session.Document.Units == Core.Model.DrawingUnits.Inches ? 1 : 0;
        }
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
