using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Drawing;

/// <summary>Base for commands that transform the current selection with point picks.</summary>
public abstract class SelectionTransformTool : ViewTool
{
    protected Point2 Cursor;
    protected readonly List<Point2> Picks = [];
    protected List<Entity> Targets = [];

    protected override void OnAttached()
    {
        Targets = Host.Session.Selection.ToList();
        if (Targets.Count == 0)
        {
            Host.SetPrompt(T("Select objects first"));
            Host.EndTool();
        }
    }

    protected abstract int PickCount { get; }
    protected abstract Matrix4 Build(IReadOnlyList<Point2> picks, Point2 cursor, KeyModifiers mods);
    protected virtual bool Copy => false;
    protected abstract string Description { get; }

    private KeyModifiers _mods;

    public override void PointerMoved(Point screen, Point2 world, KeyModifiers mods)
    {
        Cursor = world;
        _mods = mods;
        if (Picks.Count > 0) Host.Invalidate();
    }

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        Picks.Add(world);
        if (Picks.Count < PickCount)
        {
            Host.SetPrompt(Prompt);
            Host.Invalidate();
            return;
        }
        var m = Build(Picks.Take(PickCount - 1).ToList(), world, mods);
        var session = Host.Session;
        session.Execute(Description, () =>
        {
            if (Copy)
            {
                var copies = Targets.Select(t => t.CloneEntity()).ToList();
                foreach (var c in copies)
                {
                    c.Id = 0;
                    session.Document.AddEntity(c, Targets[copies.IndexOf(c)].Layer);
                }
                CadOps.Transform(copies, m);
            }
            else
            {
                CadOps.Transform(Targets, m);
            }
        });
        Host.EndTool();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key != Key.Escape) return false;
        Host.EndTool();
        return true;
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (Picks.Count == 0) return;
        var m = Build(Picks, Cursor, _mods);
        foreach (var t in Targets)
        foreach (var p in t.ToPaths())
            DrawPath(dc, view, p.Transformed(m), PreviewPen);
        dc.DrawLine(PreviewPen, view.ToScreen(Picks[0]), view.ToScreen(Cursor));
    }
}

public sealed class MoveTool : SelectionTransformTool
{
    public override string Prompt => Picks.Count == 0 ? T("Move: select base point") : T("Select destination point");
    protected override int PickCount => 2;
    protected override string Description => T("Move");

    protected override Matrix4 Build(IReadOnlyList<Point2> picks, Point2 cursor, KeyModifiers mods)
    {
        var d = cursor - picks[0];
        return Matrix4.Translation(d.X, d.Y);
    }
}

public sealed class RotateTool : SelectionTransformTool
{
    public override string Prompt => Picks.Count switch
    {
        0 => T("Rotate: select center of rotation"),
        1 => T("Select reference angle point"),
        _ => T("Select new angle"),
    };
    protected override int PickCount => 3;
    protected override string Description => T("Rotate");

    protected override Matrix4 Build(IReadOnlyList<Point2> picks, Point2 cursor, KeyModifiers mods)
    {
        var c = picks[0];
        var reference = picks.Count > 1 ? (picks[1] - c).Angle : 0;
        var angle = (cursor - c).Angle - reference;
        if (picks.Count < 2) angle = 0;
        // Snap to 15° steps while Ctrl is held.
        if (mods.HasFlag(KeyModifiers.Control)) angle = Math.Round(angle / (Math.PI / 12)) * (Math.PI / 12);
        return Matrix4.RotationZ(angle, c);
    }
}

public sealed class MirrorTool : SelectionTransformTool
{
    public override string Prompt => Picks.Count == 0 ? T("Mirror: select first point of mirror line") : T("Select second point (Shift = constrain angle)");
    protected override int PickCount => 2;
    protected override bool Copy => true;
    protected override string Description => T("Mirror");

    protected override Matrix4 Build(IReadOnlyList<Point2> picks, Point2 cursor, KeyModifiers mods)
    {
        var a = picks[0];
        var b = cursor;
        if (mods.HasFlag(KeyModifiers.Shift))
        {
            var ang = Math.Round((b - a).Angle / (Math.PI / 12)) * (Math.PI / 12);
            b = a + Point2.FromAngle(ang, Math.Max(Point2.Distance(a, b), 1));
        }
        if (a.AlmostEquals(b)) return Matrix4.Identity;
        return Matrix4.Mirror(a, b);
    }
}

/// <summary>Picks a single point and reports it (e.g. MOP start point, machining origin).</summary>
public sealed class PickPointTool(string prompt, Action<Point2> picked) : ViewTool
{
    public override string Prompt => prompt;

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        picked(world);
        Host.EndTool();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key != Key.Escape) return false;
        Host.EndTool();
        return true;
    }
}
