using Avalonia;
using Avalonia.Input;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Drawing;

/// <summary>
/// Re-selection of the drawing objects of a machining operation (Primitive IDs [...] / Select Drawing Objects):
/// the current objects start selected; click, Ctrl+click and window selection change the set.
/// Enter or a middle click applies, Esc (or switching to another tool) cancels.
/// </summary>
public sealed class PickObjectsTool(string operationName, Action<IReadOnlyCollection<Entity>> apply, Action cancel) : SelectTool
{
    private bool _done;

    public override string Prompt =>
        F("{0}: {1} object(s) selected. Click / Ctrl+click / window to change, Enter or middle click = apply, Esc = cancel",
            operationName, Host?.Session.Selection.Count ?? 0);

    public override void PointerReleased(Point screen, Point2 world, MouseButton button, KeyModifiers mods)
    {
        if (button == MouseButton.Middle)
        {
            Finish(true);
            return;
        }
        base.PointerReleased(screen, world, button, mods);
        Host.SetPrompt(Prompt);
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        switch (key)
        {
            case Key.Enter or Key.Return:
                Finish(true);
                return true;
            case Key.Escape:
                Finish(false);
                return true;
            default:
                return false;
        }
    }

    public override void OnDetached()
    {
        // Another tool was started: leave the operation unchanged.
        if (_done) return;
        _done = true;
        cancel();
    }

    private void Finish(bool ok)
    {
        if (_done) return;
        _done = true;
        if (ok) apply(Host.Session.Selection.ToList());
        else cancel();
        Host.EndTool();
    }
}