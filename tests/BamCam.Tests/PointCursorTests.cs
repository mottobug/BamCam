using BamCam.App.Drawing;
using BamCam.Core.Model;

namespace BamCam.Tests;

/// <summary>Issue 22: tools that take points show a cross cursor and the snapped point; selecting does not.</summary>
public class PointCursorTests
{
    [Fact]
    public void Point_taking_tools_are_marked_and_selection_is_not()
    {
        Assert.False(new SelectTool().PicksPoints);
        Assert.True(new PolylineTool().PicksPoints);
        Assert.True(new PolylineTool(linesOnly: true).PicksPoints);
        Assert.True(new CircleTool().PicksPoints);
        Assert.True(new RectangleTool().PicksPoints);
        Assert.True(new ArcTool().PicksPoints);
        Assert.True(new MoveTool().PicksPoints);
        Assert.True(new RotateTool().PicksPoints);
        Assert.True(new PickPointTool("pick", _ => { }).PicksPoints);
        // Tools that pick objects, not points.
        Assert.False(new TrimExtendTool().PicksPoints);
        Assert.False(new CornerTool(new PolylineEntity()).PicksPoints);
    }
}
