using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

public class TextOnPathTests
{
    /// <summary>A "text" whose outline is a 10 x 4 box standing on the baseline at x 0 … 10 (no font needed).</summary>
    private static TextEntity BoxText()
    {
        var text = new TextEntity { Text = "X", Position = new Point3(0, 0, 0), Height = 4 };
        text.Outlines.Add(Path2.FromPoints([new(0, 0), new(10, 0), new(10, 4), new(0, 4)], closed: true));
        return text;
    }

    private static BoundingBox Bounds(IEnumerable<Path2> paths) =>
        paths.SelectMany(p => p.Vertices).Aggregate(BoundingBox.Empty, (b, v) => b.Include(v.XY));

    [Fact]
    public void Straight_guide_moves_the_text_to_the_start_point()
    {
        var guide = new TextOnPath.Sampler(Path2.FromPoints([new(100, 50), new(200, 50)], false));
        var bent = TextOnPath.Bend(BoxText(), guide, guide.Project(new(130, 48)), flip: false);
        var b = Bounds(bent);
        Assert.Equal((130.0, 50.0, 140.0, 54.0), (Math.Round(b.Min.X, 6), Math.Round(b.Min.Y, 6), Math.Round(b.Max.X, 6), Math.Round(b.Max.Y, 6)));
    }

    [Fact]
    public void Vertical_guide_turns_the_text_and_flip_puts_it_on_the_other_side()
    {
        var guide = new TextOnPath.Sampler(Path2.FromPoints([new(0, 0), new(0, 100)], false));
        var up = Bounds(TextOnPath.Bend(BoxText(), guide, 20, flip: false));
        // Running upwards, "above" the baseline is to the left (negative X).
        Assert.Equal((-4.0, 20.0, 0.0, 30.0), (Math.Round(up.Min.X, 6), Math.Round(up.Min.Y, 6), Math.Round(up.Max.X, 6), Math.Round(up.Max.Y, 6)));
        var flipped = Bounds(TextOnPath.Bend(BoxText(), guide, 20, flip: true));
        Assert.Equal((0.0, 20.0, 4.0, 30.0), (Math.Round(flipped.Min.X, 6), Math.Round(flipped.Min.Y, 6), Math.Round(flipped.Max.X, 6), Math.Round(flipped.Max.Y, 6)));
    }

    [Fact]
    public void Text_on_a_circle_keeps_its_distance_from_the_guide()
    {
        // Circle radius 50, clockwise from the top: the box stands outside, between radius 50 and 54.
        var circle = new Path2([new Vertex(new Point3(0, 50, 0), -1), new Vertex(new Point3(0, -50, 0), -1)], true);
        var guide = new TextOnPath.Sampler(circle);
        foreach (var v in TextOnPath.Bend(BoxText(), guide, 0, flip: false).SelectMany(p => p.Vertices))
            Assert.InRange(v.XY.Length, 50 - 0.01, 54 + 0.01); // the guide is flattened within 0.005
    }

    [Fact]
    public void Direction_is_chosen_so_that_text_reads_left_to_right()
    {
        Assert.True(TextOnPath.ReadsBetterReversed(Path2.FromPoints([new(10, 0), new(0, 0)], false)));
        Assert.False(TextOnPath.ReadsBetterReversed(Path2.FromPoints([new(0, 0), new(10, 0)], false)));
        var ccw = Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], true);
        Assert.True(TextOnPath.ReadsBetterReversed(ccw));
        Assert.False(TextOnPath.ReadsBetterReversed(ccw.Reversed()));
    }
}
