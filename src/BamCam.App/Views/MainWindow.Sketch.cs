using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using BamCam.App.Sketching;
using BamCam.Core.Model;
using BamCam.Core.Sketching;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>Draw ▸ Sketch and the sketch mode's bar of commands.</summary>
public partial class MainWindow
{
    /// <summary>Draw ▸ Sketch: a new, empty sketch in the active layer, opened in the sketch mode.</summary>
    private void NewSketch()
    {
        var entity = new SketchEntity();
        entity.SetSketch(Sketch.CreateNew());
        var doc = _session.Document;
        _session.Execute(T("New sketch"), () => doc.AddEntity(entity, doc.ActiveLayer));
        StartSketch(entity, isNew: true);
    }

    private void StartSketch(SketchEntity entity, bool isNew = false)
    {
        var tool = new SketchTool(entity, isNew,
            (title, label, value) => Dialogs.Prompt(this, title, label, value, remember: false),
            text => Dialogs.EditText(this, T("Sketch variables (one per line: name = value or formula)"), text),
            Log);
        tool.StateChanged += (_, _) => UpdateSketchBar(tool);
        tool.Ended += (_, _) =>
        {
            SketchBar.IsVisible = false;
            SketchBarPanel.Children.Clear();
        };
        BuildSketchBar(tool);
        View.SetTool(tool);
        if (isNew) Log(T("Sketch: draw with Line (L), Rectangle (R), Circle (C) or Arc (A); select and add constraints and dimensions (D). Enter finishes."));
    }

    private readonly List<(ToggleButton Button, SketchMode Mode)> _sketchModeButtons = [];
    private ToggleButton? _constructionButton;
    private TextBlock? _sketchStatus;

    private void BuildSketchBar(SketchTool tool)
    {
        var p = SketchBarPanel;
        p.Children.Clear();
        _sketchModeButtons.Clear();

        // The icons of the 24 x 24 box scaled to 18 px, as in the main toolbar; the name is in the tooltip.
        Control Icon(string key)
        {
            var geo = ((Geometry)this.FindResource(key)!).Clone();
            geo.Transform = new ScaleTransform(0.75, 0.75);
            return new Avalonia.Controls.Shapes.Path
            {
                Data = geo, Stroke = Brushes.Black, StrokeThickness = 1.4, Width = 18, Height = 18, Stretch = Stretch.None,
                StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round,
            };
        }
        void Setup(Button b, string? icon, string text, string tip, Action action)
        {
            b.Content = icon != null ? Icon(icon) : text;
            b.Margin = new Thickness(1);
            b.Padding = icon != null ? new Thickness(3) : new Thickness(6, 2);
            b.FontSize = 12;
            ToolTip.SetTip(b, icon != null ? $"{text}: {tip}" : tip);
            b.Click += (_, _) =>
            {
                action();
                View.Focus();
            };
            p.Children.Add(b);
        }
        Button Cmd(string? icon, string text, string tip, Action action)
        {
            var b = new Button();
            Setup(b, icon, text, tip, action);
            return b;
        }
        void ModeButton(string icon, string text, string tip, SketchMode mode)
        {
            var b = new ToggleButton();
            Setup(b, icon, text, tip, () => tool.SetMode(mode));
            _sketchModeButtons.Add((b, mode));
        }
        void Gap() => p.Children.Add(new Border { Width = 1, Margin = new Thickness(5, 3), Background = Brushes.Gray });

        Cmd(null, T("Finish"), T("Keep the sketch and leave the sketch mode (Enter)"), tool.Finish).FontWeight = FontWeight.Bold;
        Cmd(null, T("Cancel"), T("Drop the changes of this sketch session"), tool.Cancel);
        Gap();
        ModeButton("IconSkSelect", T("Select"), T("Select and drag geometry, double-click a dimension to change it (S)"), SketchMode.Select);
        ModeButton("IconSkLine", T("Line"), T("Lines in a chain; nearly horizontal or vertical lines get the constraint (L)"), SketchMode.Line);
        ModeButton("IconRect", T("Rectangle"), T("Rectangle with horizontal and vertical sides (R)"), SketchMode.Rectangle);
        ModeButton("IconCircle", T("Circle"), T("Circle: centre, then a point on it (C)"), SketchMode.Circle);
        ModeButton("IconArc", T("Arc"), T("Arc: start, end, then a point on the arc (A)"), SketchMode.Arc);
        ModeButton("IconSkArcCenter", T("Arc (Centre)"), T("Arc: centre, start, end, counter-clockwise (Shift+A)"), SketchMode.ArcCenter);
        ModeButton("IconSkPoint", T("Point"), T("Point (P)"), SketchMode.Point);
        _constructionButton = new ToggleButton();
        Setup(_constructionButton, "IconSkConstruction", T("Construction"),
            T("Construction geometry helps with constraints but is not part of the outlines. Switches the selected geometry, or what is drawn next (X)"),
            tool.ToggleConstruction);
        Gap();
        Cmd("IconSkCoincident", T("Coincident"), T("Selected points on one place, or points on a line, circle or arc (K)"), () => tool.Constrain(ConstraintKind.Coincident));
        Cmd("IconSkHorizontal", T("Horizontal"), T("Selected lines, or two points, horizontal (H)"), () => tool.Constrain(ConstraintKind.Horizontal));
        Cmd("IconSkVertical", T("Vertical"), T("Selected lines, or two points, vertical (V)"), () => tool.Constrain(ConstraintKind.Vertical));
        Cmd("IconSkParallel", T("Parallel"), T("Selected lines parallel"), () => tool.Constrain(ConstraintKind.Parallel));
        Cmd("IconSkPerpendicular", T("Perpendicular"), T("Two selected lines at right angles"), () => tool.Constrain(ConstraintKind.Perpendicular));
        Cmd("IconSkTangent", T("Tangent"), T("A line and a circle or arc, or two circles or arcs, touching (T)"), () => tool.Constrain(ConstraintKind.Tangent));
        Cmd("IconSkEqual", T("Equal"), T("Selected lines of equal length, or circles and arcs of equal radius (E)"), () => tool.Constrain(ConstraintKind.Equal));
        Cmd("IconSkMidpoint", T("Midpoint"), T("A point in the middle of a line (M)"), () => tool.Constrain(ConstraintKind.Midpoint));
        Cmd("IconSkSymmetric", T("Symmetric"), T("Two points symmetric about a line"), () => tool.Constrain(ConstraintKind.Symmetric));
        Cmd("IconSkFix", T("Fix"), T("Selected points (or the points of selected geometry) fixed where they are (F)"), () => tool.Constrain(ConstraintKind.Fix));
        Gap();
        Cmd("IconSkFillet", T("Fillet"), T("Round the selected corners (points where two lines meet, or two lines) with a radius (G)"), () => tool.RoundCorners(chamfer: false));
        Cmd("IconSkChamfer", T("Chamfer"), T("Chamfer the selected corners: the same length off both lines (N)"), () => tool.RoundCorners(chamfer: true));
        Gap();
        Cmd("IconSkDimension", T("Dimension"), T("Length of a line, distance of two points or a point and a line, diameter of a circle, radius of an arc, angle of two lines (D)"), () => tool.Dimension());
        Cmd("IconSkDimX", T("Horizontal Dim."), T("Horizontal distance of two points or of the ends of a line"), () => tool.Dimension(ConstraintKind.HorizontalDistance));
        Cmd("IconSkDimY", T("Vertical Dim."), T("Vertical distance of two points or of the ends of a line"), () => tool.Dimension(ConstraintKind.VerticalDistance));
        Cmd("IconSkRadius", T("Radius"), T("Radius instead of the diameter of a circle"), () => tool.Dimension(ConstraintKind.Radius));
        Cmd(null, T("Variables..."), T("Named values the dimensions can use in formulas, e.g. Width = 60, Height = Width / 3"), tool.EditVariables);
        Gap();
        Cmd("IconSkDelete", T("Delete"), T("Delete the selected geometry and constraints (Del)"), tool.DeleteSelection);
        _sketchStatus = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        p.Children.Add(_sketchStatus);
        SketchBar.IsVisible = true;
        UpdateSketchBar(tool);
    }

    private void UpdateSketchBar(SketchTool tool)
    {
        foreach (var (button, mode) in _sketchModeButtons) button.IsChecked = tool.Mode == mode;
        if (_constructionButton != null) _constructionButton.IsChecked = tool.ConstructionMode;
        if (_sketchStatus != null)
        {
            _sketchStatus.Text = tool.StatusText;
            _sketchStatus.Foreground = tool.Result.Status switch
            {
                SketchStatus.FullyConstrained => new SolidColorBrush(Color.FromRgb(30, 140, 50)),
                SketchStatus.OverConstrained => Brushes.Red,
                _ => new SolidColorBrush(Color.FromRgb(30, 90, 170)),
            };
        }
    }
}
