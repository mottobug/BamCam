using BamCam.Core.Cam;

namespace BamCam.Tests;

/// <summary>
/// Issue 33: a plasma operation without cut data makes no toolpath until kerf, feed and heights are entered. Tests that
/// are about something else enter the starting values as typed values, which keeps their numbers.
/// </summary>
internal static class PlasmaTestValues
{
    public static PlasmaOp WithValues(this PlasmaOp op)
    {
        foreach (var p in new[] { op.KerfWidth, op.CutFeedrate, op.PierceHeight, op.PierceDelay, op.CutHeight })
            if (p.State == ParamState.Default) p.Set(p.Value);
        return op;
    }
}
