using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private readonly List<(ToggleButton Button, Func<bool> IsChecked)> _toggleButtons = [];
    private readonly List<(Button Button, Func<ViewTool, bool> IsActive)> _toolButtons = [];
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

    /// <summary>
    /// Menu item with keys Avalonia would show badly (Ctrl+D1 for Ctrl+1): the key text is shown in the header,
    /// the keys work as shortcuts.
    /// </summary>
    private MenuItem Item(string header, Action action, string keyText, params (Key Key, KeyModifiers Mods)[] keys)
    {
        var text = new AccessText { Text = T(header) };
        var shown = new TextBlock { Text = keyText, Foreground = Brushes.Gray, Margin = new Thickness(24, 0, 0, 0) };
        var panel = new DockPanel();
        DockPanel.SetDock(shown, Dock.Right);
        panel.Children.Add(shown);
        panel.Children.Add(text);
        var item = new MenuItem { Header = panel };
        item.Click += (_, _) => action();
        if (_registering)
            foreach (var k in keys) _shortcuts[k] = action;
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

    /// <summary>Check item for one kind of object snap point.</summary>
    private MenuItem SnapKindCheck(string header, Core.Cad.SnapKinds kind)
    {
        var s = View.Settings;
        return Check(header, () => s.ObjectSnapKinds.HasFlag(kind),
            v => s.ObjectSnapKinds = v ? s.ObjectSnapKinds | kind : s.ObjectSnapKinds & ~kind);
    }

    private MenuItem RecentMenu()
    {
        _recentMenu = new MenuItem { Header = T("_Recent Files") };
        _recentMenu.Items.Add(new MenuItem { Header = T("(empty)"), IsEnabled = false });
        _recentMenu.SubmenuOpened += (_, _) => FillRecentMenu();
        return _recentMenu;
    }

    /// <summary>Updates the check marks of the View menu and the toggle buttons of the toolbar.</summary>
    private void RefreshChecks()
    {
        foreach (var (item, get) in _checkItems) item.IsChecked = get();
        foreach (var (button, get) in _toggleButtons)
        {
            button.IsChecked = get();
            // The checked button has the accent background: draw its icon light.
            if ((button.Content as Panel)?.Children[0] is Path icon) icon.Stroke = get() ? Brushes.White : Brushes.Black;
        }
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
            Item("Insert _File...", InsertFile),
            Sub("_Import Image",
                Item("As _Height Map...", ImportHeightMap),
                Item("_Trace Outlines...", TraceImage)),
            Sub("_Export", [Item("To _DXF...", ExportDxf), Item("To _PDF...", () => PrintPdf(export: true)),
                .. _plugins.Exporters.Select(x => (object)Item(F("To {0}...", x.Format.Name), () => ExportWithPlugin(x)))]),
            "-",
            Item("_Print...", () => PrintPdf(export: false), "Ctrl+Shift+P"),
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
            Item("_Speeds and Feeds...", ShowSpeedsFeeds),
            Item("_Options...", ShowOptions),
            Item("_Plugins...", ShowPluginManager),
            Item("_Clear Messages", () => Messages.Text = ""),
            "-",
            Item("Reload Post Processors", () => { _system.Reload(); BuildSystemTree(); Log(F("{0} post processors loaded.", _system.Posts.Count)); }),
            Item("Browse System Folder", () => OpenFolder(Services.SystemLibrary.SystemFolder)),
            "-",
            Item("Edit _Language File...", EditLanguageFile),
            Item("Reload Language File", ReloadLanguageFile),
            "-",
            Item("Import from Cam_Bam...", ImportFromCamBam),
            Item("E_xport Configuration...", ExportConfiguration),
            Item("Import Con_figuration...", ImportConfiguration)));

        MainMenu.Items.Add(ScriptMenu());

        if (PluginMenuItems("Plugins") is { Length: > 0 } pluginItems) MainMenu.Items.Add(Sub("P_lugins", pluginItems));

        MainMenu.Items.Add(Sub("_Help", [
            Item("User _Guide", OpenUserGuide, "F1"),
            "-",
            .. UpdateMenuItems(),
            "-",
            Item("_About BamCam", () => _ = Dialogs.Message(this, T("About BamCam"),
                F("BamCam {2} – cross-platform 2.5D CAD/CAM\n\nAvalonia {0}, .NET {1}\nReads and writes .cb drawings and .cbpp post processors.",
                    typeof(Avalonia.Application).Assembly.GetName().Version, Environment.Version, CurrentVersion)))]));

        // Single-key draw shortcuts (active when no text field has focus).
        _shortcuts[(Key.P, KeyModifiers.None)] = () => View.SetTool(new PolylineTool());
        _shortcuts[(Key.C, KeyModifiers.None)] = () => { if (View.Tool is not PolylineTool) View.SetTool(new CircleTool()); else View.ForwardKeyToTool(Key.C, KeyModifiers.None); };
        _shortcuts[(Key.R, KeyModifiers.None)] = () => View.SetTool(new RectangleTool());
        _shortcuts[(Key.A, KeyModifiers.None)] = () => View.SetTool(new ArcTool());
        _shortcuts[(Key.D, KeyModifiers.None)] = () => View.SetTool(new PointListTool());
        _shortcuts[(Key.T, KeyModifiers.None)] = DrawText;
        _shortcuts[(Key.M, KeyModifiers.None)] = () => View.SetTool(new MeasureTool((a, b) => ReportMeasure(a, b)));
        _shortcuts[(Key.F2, KeyModifiers.None)] = () => _ = RenameSelected();
        // As in CamBam: F5 runs the scripts of the selected script objects, Ctrl+L aligns.
        _shortcuts[(Key.F5, KeyModifiers.None)] = RunSelectedScripts;
        _shortcuts[(Key.L, KeyModifiers.Control)] = () =>
        {
            if (_alignButton != null) ShowAlignMenu(_alignButton);
        };
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
        Item("Select All on Active Layer", () => { _session.Select(_session.Document.ActiveLayer.Entities.Where(e => !e.Hidden)); View.Invalidate(); }, "Ctrl+Shift+A"),
        "-",
        Item("E_xplode", Explode),
        Item("_Join...", Join, "Ctrl+J"),
        Item("_Offset...", () => Offset(false)),
        Item("Open Offset...", () => Offset(true)),
        Item("_Trim / Extend", () => View.SetTool(new TrimExtendTool()), "Ctrl+OemPlus"),
        Item("_Break at Intersections", BreakAtIntersections, "Ctrl+B"),
        Item("_Union", () => BooleanOp("Union"), "Ctrl+U"),
        Item("_Subtract", () => BooleanOp("Subtract")),
        Item("_Intersection", () => BooleanOp("Intersection")),
        Sub("Convert To",
            Item("_Polyline", ConvertToPolyline, "Ctrl+P"),
            Item("_Region", ConvertToRegion, "Ctrl+I")),
        Sub("Polyline",
            Item("_Edit Points...", () => { if (Selected.FirstOrDefault() is { } e) EditPoints(e); }),
            Item("_Reverse", ReversePolylines),
            Item("_Clean", CleanPolylines),
            Item("_Fillet / Chamfer", FilletChamfer)),
        Sub("Sur_face",
            Item("Plane Slice _X...", () => _ = SurfacePlaneSlice(0)),
            Item("Plane Slice _Y...", () => _ = SurfacePlaneSlice(1)),
            Item("Plane Slice _Z...", () => _ = SurfacePlaneSlice(2)),
            Item("_Silhouette...", () => _ = SurfaceSilhouette()),
            Item("_Invert Faces", SurfaceInvertFaces),
            Item("_Edge Detect...", () => _ = SurfaceEdges()),
            Item("_Project Lines to Surface...", () => _ = SurfaceProject())),
        Item("Edit Scr_ipt...", EditSelectedScript),
        Sub("_Transform", TransformMenuItems()),
        .. PluginMenuItems("Edit"),
    ];

    private object[] TransformMenuItems() =>
    [
        Item("_Numeric...", ShowNumericTransform),
        Item("_Move", () => View.SetTool(new MoveTool()), "Ctrl+M"),
        Item("_Rotate", () => View.SetTool(new RotateTool()), "Ctrl+R"),
        Item("Resi_ze...", Resize, "Ctrl+E"),
        Item("M_irror", () => View.SetTool(new MirrorTool())),
        Item("_Array Copy...", ArrayCopy),
        Item("_Polar Array Copy...", PolarArrayCopy),
        Item("_Center (Extents)", CenterExtents),
        AlignSubmenu(),
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
            Item("_XY Plane", () => View.SetStandardView("XY"), "Ctrl+1", (Key.D1, KeyModifiers.Control), (Key.NumPad1, KeyModifiers.Control)),
            Item("X_Z Plane", () => View.SetStandardView("XZ"), "Ctrl+2", (Key.D2, KeyModifiers.Control), (Key.NumPad2, KeyModifiers.Control)),
            Item("_YZ Plane", () => View.SetStandardView("YZ"), "Ctrl+3", (Key.D3, KeyModifiers.Control), (Key.NumPad3, KeyModifiers.Control)),
            Item("_Isometric", () => View.SetStandardView("ISO"), "Ctrl+4", (Key.D4, KeyModifiers.Control), (Key.NumPad4, KeyModifiers.Control)),
            "-",
            Check("Show _Toolpaths", () => s.ShowToolpaths, v => s.ShowToolpaths = v),
            Check("Show Cut _Widths", () => _session.Document.ShowCutWidths, v => _session.Document.ShowCutWidths = v),
            Check("Show _Rapids", () => s.ShowRapids, v => { s.ShowRapids = v; _session.Document.ShowRapids = v; }),
            Check("Show _Stock", () => s.ShowStock, v => { s.ShowStock = v; _session.Document.ShowStock = v; }),
            Check("Show Direction Arrows", () => s.ShowDirectionArrows, v => s.ShowDirectionArrows = v),
            Check("Shade S_urfaces", () => s.ShadeSurfaces, v => s.ShadeSurfaces = v),
            Check("Show _Grid", () => s.ShowGrid, v => s.ShowGrid = v),
            Check("Show _Axis", () => s.ShowAxis, v => s.ShowAxis = v),
            Check("Show _Nests", () => s.ShowNests, v => s.ShowNests = v),
            Check("Holding Tab Drag Toolpath Refresh", () => s.TabDragToolpathRefresh, v => s.TabDragToolpathRefresh = v),
            Item("Toolpath View _Filter...", ShowToolpathFilter, "Ctrl+F"),
            Item("Show All Ob_jects", ShowAllObjects),
            "-",
            Check("Snap to _Grid", () => s.SnapToGrid, v => s.SnapToGrid = v, "Ctrl+G"),
            Check("Snap to _Objects", () => s.SnapToObjects, v => s.SnapToObjects = v),
            Sub("Object Snap _Modes",
                SnapKindCheck("_End Points", Core.Cad.SnapKinds.EndPoint),
                SnapKindCheck("_Mid Points", Core.Cad.SnapKinds.MidPoint),
                SnapKindCheck("Circle _Centers", Core.Cad.SnapKinds.Center),
                SnapKindCheck("_Quadrants", Core.Cad.SnapKinds.Quadrant),
                SnapKindCheck("_Intersections", Core.Cad.SnapKinds.Intersection)),
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
        Item("Text on Pol_yline", TextOnPolyline),
        Item("Script O_bject", NewScriptObject),
        Sub("S_urface",
            Item("_Extrude...", () => _ = DrawExtrude(solid: false)),
            Item("Extrude _Solid...", () => _ = DrawExtrude(solid: true)),
            Item("_Fill", DrawFill)),
        Item("Fill Re_gion...", () => _ = DrawFillRegion()),
        .. PluginMenuItems("Draw"),
    ];

    private object[] MachiningMenuItems() =>
    [
        Item("_Profile", () => CreateOp(new ProfileOp())),
        Item("P_ocket", () => CreateOp(new PocketOp())),
        Item("_Drill", () => CreateOp(new DrillOp())),
        Item("_Thread Milling", () => CreateOp(new ThreadMillOp())),
        Item("Tapping (G8_4)", () => CreateOp(new TapOp())),
        Item("_Engrave", () => CreateOp(new EngraveOp())),
        Item("_V-Engrave", () => CreateOp(new VEngraveOp())),
        Item("_3D Profile", () => CreateOp(new Profile3DOp())),
        Item("Drag _Knife", () => CreateOp(new DragKnifeOp())),
        Item("Pl_asma", () => CreateOp(new PlasmaOp())),
        Sub("_Lathe",
            Item("_Turning", () => CreateOp(new LatheOp())),
            Item("_Facing", () => CreateOp(new LatheFacingOp())),
            Item("_Grooving / Parting", () => CreateOp(new LatheGrooveOp())),
            Item("T_hreading", () => CreateOp(new LatheThreadOp())),
            Item("_Drilling", () => CreateOp(new LatheDrillOp()))),
        .. PluginOperationItems(),
        "-",
        Item("New Part", NewPart),
        "-",
        Item("_Generate Toolpaths", () => GenerateToolpaths(), "Ctrl+T"),
        Item("3D _Simulation...", () => _ = ShowSimulation(), "Ctrl+Shift+T"),
        Item("Produce G_code", () => _ = ProduceGCode(MenuPostScope()), "Ctrl+W"),
        Item("Edit Gcode", EditGCode),
        .. PluginMenuItems("Machining"),
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

    // Icon colours of the toolbar groups; dark enough for thin lines on the light background, and blue and orange stay
    // apart with red-green colour blindness. File and view buttons remain black.
    private static readonly IBrush DrawIconBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
    private static readonly IBrush OperationIconBrush = new SolidColorBrush(Color.FromRgb(0xC2, 0x41, 0x0C));
    private static readonly IBrush ActionIconBrush = new SolidColorBrush(Color.FromRgb(0x7B, 0x1F, 0xA2));

    private Button ToolButton(string iconKey, string tip, Action action, IBrush? iconBrush = null)
    {
        var geo = (Geometry)this.FindResource(iconKey)!;
        var b = new Button
        {
            Content = new Path
            {
                Data = geo, Stroke = iconBrush ?? Brushes.Black, StrokeThickness = 1.4, Width = 18, Height = 18, Stretch = Stretch.Uniform,
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

    /// <summary>Toolbar button for an on/off view setting: shows its state and stays in step with the View menu.</summary>
    /// <remarks><paramref name="dotKey"/>: optional red mark on the icon (the snap point of the snap buttons).</remarks>
    private ToggleButton ToggleToolButton(string iconKey, string tip, Func<bool> get, Action<bool> set, string? shortcut = null, string? dotKey = null)
    {
        // The 24 x 24 icon box scaled to 18 px, the same for both layers so that they stay aligned.
        Path Layer(string key, IBrush stroke, IBrush? fill)
        {
            var geo = ((Geometry)this.FindResource(key)!).Clone();
            geo.Transform = new ScaleTransform(0.75, 0.75);
            return new Path
            {
                Data = geo, Stroke = stroke, Fill = fill, StrokeThickness = 1.4, Width = 18, Height = 18, Stretch = Stretch.None,
                StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round,
            };
        }
        var icon = new Grid();
        icon.Children.Add(Layer(iconKey, Brushes.Black, null));
        if (dotKey != null) icon.Children.Add(Layer(dotKey, Brushes.Red, Brushes.Red));
        var b = new ToggleButton { Content = icon };
        ToolTip.SetTip(b, shortcut == null ? T(tip) : $"{T(tip)} ({shortcut})");
        b.Click += (_, _) =>
        {
            set(!get());
            RefreshChecks();
            SaveSettings();
            View.Invalidate();
            UpdateTitle();
            View.Focus();
        };
        _toggleButtons.Add((b, get));
        return b;
    }

    /// <summary>Toolbar button that starts a drawing tool and is highlighted while that tool is active.</summary>
    private Button ToolModeButton(string iconKey, string tip, Action start, Func<ViewTool, bool> isActive)
    {
        var b = ToolButton(iconKey, tip, start, DrawIconBrush);
        _toolButtons.Add((b, isActive));
        return b;
    }

    /// <summary>Highlights the button of the active drawing tool (same look as a checked toggle button).</summary>
    private void RefreshToolButtons()
    {
        var accent = this.FindResource("ToggleButtonBackgroundChecked") as IBrush ?? Brushes.DodgerBlue;
        foreach (var (button, isActive) in _toolButtons)
        {
            var active = isActive(View.Tool);
            if (active) button.Background = accent;
            else button.ClearValue(BackgroundProperty);
            if (button.Content is Path icon) icon.Stroke = active ? Brushes.White : DrawIconBrush;
        }
    }

    /// <summary>A toolbar icon (24 x 24 geometry scaled to 18 px).</summary>
    private Path IconPath(string key) => new()
    {
        Data = (Geometry)this.FindResource(key)!, Stroke = Brushes.Black, StrokeThickness = 1.4, Width = 18, Height = 18,
        Stretch = Stretch.Uniform, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round,
    };

    /// <summary>The six align commands and the choice of the reference (toolbar menu and Transform submenu).</summary>
    private object[] AlignMenuItems()
    {
        MenuItem Edge(string header, string icon, Core.Cad.AlignEdge edge)
        {
            var item = Item(header, () => AlignSelection(edge));
            item.Icon = IconPath(icon);
            return item;
        }
        var items = new List<object>
        {
            Edge("_Left", "IconAlignLeft", Core.Cad.AlignEdge.Left),
            Edge("_Center (Horizontal)", "IconAlignCenterX", Core.Cad.AlignEdge.CenterX),
            Edge("_Right", "IconAlignRight", Core.Cad.AlignEdge.Right),
            Edge("_Top", "IconAlignTop", Core.Cad.AlignEdge.Top),
            Edge("_Middle (Vertical)", "IconAlignCenterY", Core.Cad.AlignEdge.CenterY),
            Edge("_Bottom", "IconAlignBottom", Core.Cad.AlignEdge.Bottom),
            new Separator(),
            new MenuItem { Header = T("Align to") + ":", IsEnabled = false },
        };
        foreach (var (value, text) in AlignReferences)
        {
            var item = Item(text, () => AlignTo = value);
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = AlignTo == value;
            items.Add(item);
        }
        return items.ToArray();
    }

    private Button? _alignButton;

    /// <summary>Opens the align menu below the toolbar button, built fresh so that the reference check is current.</summary>
    private MenuFlyout ShowAlignMenu(Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var item in AlignMenuItems()) menu.Items.Add(item);
        menu.ShowAt(anchor);
        return menu;
    }

    /// <summary>Toolbar buttons that open a menu of plugin commands (for diagnostics).</summary>
    private readonly List<(Button Button, List<Services.PluginCommandEntry> Commands)> _pluginMenuButtons = [];

    /// <summary>The menu of a plugin toolbar button with several commands.</summary>
    private MenuFlyout ShowPluginMenu(Control anchor, IEnumerable<Services.PluginCommandEntry> commands)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var entry in commands)
        {
            var item = new MenuItem { Header = entry.Command.Header };
            if (entry.Command.Description is { } d) ToolTip.SetTip(item, d);
            if (!string.IsNullOrWhiteSpace(entry.Command.Icon))
            {
                try
                {
                    item.Icon = new Path
                    {
                        Data = Geometry.Parse(entry.Command.Icon), Stroke = Brushes.Black, StrokeThickness = 1.4, Width = 18, Height = 18,
                        Stretch = Stretch.Uniform, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round,
                    };
                }
                catch (FormatException) { }
            }
            item.Click += (_, _) =>
            {
                _ = RunPluginCommand(entry);
                View.Focus();
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
        return menu;
    }

    private MenuItem AlignSubmenu()
    {
        var sub = Sub("A_lign");
        sub.SubmenuOpened += (_, _) =>
        {
            sub.Items.Clear();
            foreach (var i in AlignMenuItems()) sub.Items.Add(i);
        };
        foreach (var i in AlignMenuItems()) sub.Items.Add(i);
        return sub;
    }

    private ComboBox? _unitsCombo;
    private bool _unitsHooked;

    private static Control ToolSeparator() => new Rectangle { Width = 1, Height = 20, Fill = Brushes.Gray, Margin = new Thickness(4, 2) };

    private void BuildToolbar()
    {
        var p = ToolbarPanel;
        p.Children.Clear();
        _toggleButtons.Clear();
        _toolButtons.Clear();
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
        var s = View.Settings;
        p.Children.Add(ToggleToolButton("IconAxis", "Show axis", () => s.ShowAxis, v => s.ShowAxis = v));
        p.Children.Add(ToggleToolButton("IconGrid", "Show grid", () => s.ShowGrid, v => s.ShowGrid = v));
        p.Children.Add(ToggleToolButton("IconSnap", "Snap to grid", () => s.SnapToGrid, v => s.SnapToGrid = v, "Ctrl+G", "IconSnapDot"));
        p.Children.Add(ToggleToolButton("IconSnapObjects", "Snap to objects", () => s.SnapToObjects, v => s.SnapToObjects = v, dotKey: "IconSnapObjectsDot"));
        p.Children.Add(ToolButton("IconFit", "Zoom to fit", () => View.ZoomToFit()));
        RefreshChecks();
        p.Children.Add(ToolSeparator());
        p.Children.Add(ToolModeButton("IconLine", "Line", () => View.SetTool(new PolylineTool(linesOnly: true)), t => t is PolylineTool { LinesOnly: true }));
        p.Children.Add(ToolModeButton("IconPolyline", "Polyline (P)", () => View.SetTool(new PolylineTool()), t => t is PolylineTool { LinesOnly: false }));
        p.Children.Add(ToolModeButton("IconArc", "Arc (A)", () => View.SetTool(new ArcTool()), t => t is ArcTool));
        p.Children.Add(ToolModeButton("IconSpline", "Spline", () => View.SetTool(new SplineTool()), t => t is SplineTool));
        p.Children.Add(ToolModeButton("IconCircle", "Circle (C)", () => View.SetTool(new CircleTool()), t => t is CircleTool));
        p.Children.Add(ToolModeButton("IconRect", "Rectangle (R)", () => View.SetTool(new RectangleTool()), t => t is RectangleTool));
        p.Children.Add(ToolModeButton("IconPoints", "Point list (D)", () => View.SetTool(new PointListTool()), t => t is PointListTool));
        p.Children.Add(ToolModeButton("IconText", "Text (T)", DrawText, t => t is TextTool));
        p.Children.Add(ToolSeparator());
        // One button for aligning: a menu with the six edges and the reference.
        // A plain toolbar button (same look and height as the others) with a small arrow and a menu.
        var arrow = new Path
        {
            Data = Geometry.Parse("M0,0 L6,0 L3,3.5 Z"), Fill = Brushes.Black, Width = 6, Height = 4,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0),
        };
        var align = new Button
        {
            Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { IconPath("IconAlignLeft"), arrow } },
        };
        align.Click += (_, _) => ShowAlignMenu(align);
        ToolTip.SetTip(align, T("Align (Ctrl+L)"));
        _alignButton = align;
        p.Children.Add(align);
        p.Children.Add(ToolSeparator());
        p.Children.Add(ToolButton("IconProfile", "Profile", () => CreateOp(new ProfileOp()), OperationIconBrush));
        p.Children.Add(ToolButton("IconPocket", "Pocket", () => CreateOp(new PocketOp()), OperationIconBrush));
        // The trochoidal pocket comes from the matmill plugin, which comes with BamCam but can be switched off.
        p.Children.Add(ToolButton("IconTrochoidal", "Trochoidal Pocket (matmill)", () =>
        {
            if (MachineOpFactory.Create("MOPTrochopock") is { } trochoidal and not UnsupportedOp) CreateOp(trochoidal);
            else Log(T("The trochoidal pocket comes from the plugin Trochoidal Milling (matmill); switch it on in Tools > Plugins."));
        }, OperationIconBrush));
        p.Children.Add(ToolButton("IconDrill", "Drill", () => CreateOp(new DrillOp()), OperationIconBrush));
        p.Children.Add(ToolButton("IconEngrave", "Engrave", () => CreateOp(new EngraveOp()), OperationIconBrush));
        p.Children.Add(ToolButton("IconVEngrave", "V-Engrave", () => CreateOp(new VEngraveOp()), OperationIconBrush));
        p.Children.Add(ToolButton("Icon3D", "3D Profile", () => CreateOp(new Profile3DOp()), OperationIconBrush));
        p.Children.Add(ToolButton("IconPlasma", "Plasma", () => CreateOp(new PlasmaOp()), OperationIconBrush));
        p.Children.Add(ToolSeparator());
        // Toolbar buttons of plugin commands with an icon.
        var pluginButtons = Services.PluginManager.ToolbarButtons(_plugins.Commands);
        _pluginMenuButtons.Clear();
        if (pluginButtons.Count > 0)
        {
            foreach (var commands in pluginButtons)
            {
                Geometry geo;
                try { geo = Geometry.Parse(commands.First(c => !string.IsNullOrWhiteSpace(c.Command.Icon)).Command.Icon!); }
                catch (FormatException) { continue; } // a broken icon must not break the toolbar
                var icon = new Path
                {
                    Data = geo, Stroke = Brushes.Black, StrokeThickness = 1.4, Width = 18, Height = 18, Stretch = Stretch.Uniform,
                    StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round,
                };
                var b = new Button();
                var plugin = commands[0].Plugin.Name;
                if (commands.Count == 1)
                {
                    var entry = commands[0];
                    b.Content = icon;
                    var tip = entry.Command.Header.Replace("_", "") + (entry.Command.Description is { } d ? "\n" + d : "");
                    ToolTip.SetTip(b, $"{tip}\n({plugin})");
                    b.Click += (_, _) =>
                    {
                        _ = RunPluginCommand(entry);
                        View.Focus();
                    };
                }
                else
                {
                    // Several commands: a button with an arrow and a menu, like the align button.
                    var arrowMark = new Path
                    {
                        Data = Geometry.Parse("M0,0 L6,0 L3,3.5 Z"), Fill = Brushes.Black, Width = 6, Height = 4,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0),
                    };
                    b.Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { icon, arrowMark } };
                    ToolTip.SetTip(b, string.Join("\n", commands.Select(c => c.Command.Header.Replace("_", ""))) + $"\n({plugin})");
                    b.Click += (_, _) => ShowPluginMenu(b, commands);
                    _pluginMenuButtons.Add((b, commands));
                }
                p.Children.Add(b);
            }
            p.Children.Add(ToolSeparator());
        }
        p.Children.Add(ToolButton("IconGenerate", "Generate toolpaths (Ctrl+T)", () => GenerateToolpaths(), ActionIconBrush));
        p.Children.Add(ToolButton("IconSimulate", "3D simulation (Ctrl+Shift+T)", () => _ = ShowSimulation(), ActionIconBrush));
        p.Children.Add(ToolButton("IconGcode", "Produce G-code (Ctrl+W)", () => _ = ProduceGCode(MenuPostScope()), ActionIconBrush));
        RefreshToolButtons();
    }
}
