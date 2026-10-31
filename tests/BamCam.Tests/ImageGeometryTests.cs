using BamCam.Core.Cad;

namespace BamCam.Tests;

public class ImageGeometryTests
{
    /// <summary>White image of w x h pixels with black pixels where <paramref name="black"/> is true.</summary>
    private static float[,] Image(int w, int h, Func<int, int, bool> black)
    {
        var g = new float[w, h];
        for (var x = 0; x < w; x++)
        for (var y = 0; y < h; y++)
            g[x, y] = black(x, y) ? 0f : 1f;
        return g;
    }

    [Fact]
    public void Height_map_grid_and_heights()
    {
        // Left half black, right half white; 10 px wide image becomes 20 units.
        var gray = Image(10, 5, (x, _) => x < 5);
        var s = ImageGeometry.HeightMap(gray, 20, 3, 1, 1, invert: false);
        // 21 x 11 samples at 1 unit spacing.
        Assert.Equal(21 * 11, s.Vertices.Count);
        Assert.Equal(20 * 10 * 6, s.Faces.Count);
        Assert.All(s.Vertices, v => Assert.InRange(v.Z, -2 - 1e-9, 1 + 1e-9));
        Assert.Equal(-2, s.Vertices.First(v => v.X == 0).Z, 9);
        Assert.Equal(1, s.Vertices.First(v => v.X == 20).Z, 9);
        Assert.Equal(10, s.Vertices.Max(v => v.Y), 9);

        var inverted = ImageGeometry.HeightMap(gray, 20, 3, 1, 1, invert: true);
        Assert.Equal(1, inverted.Vertices.First(v => v.X == 0).Z, 9);
    }

    [Fact]
    public void Height_map_row_zero_is_the_top()
    {
        // Top row black: the samples with the largest Y are low.
        var gray = Image(4, 4, (_, y) => y == 0);
        var s = ImageGeometry.HeightMap(gray, 4, 2, 0, 1, invert: false);
        Assert.Equal(-2, s.Vertices.Where(v => v.Y == s.Vertices.Max(p => p.Y)).Max(v => v.Z), 9);
        Assert.Equal(0, s.Vertices.Where(v => v.Y == 0).Min(v => v.Z), 9);
    }

    [Fact]
    public void Square_traces_to_one_loop_of_the_right_size()
    {
        // Black square of 10 x 10 px in a 20 x 20 px image, 1 px = 1 unit.
        var gray = Image(20, 20, (x, y) => x is >= 5 and < 15 && y is >= 5 and < 15);
        var loops = ImageGeometry.TraceOutlines(gray, 20, 0.5, invert: false, simplify: 0.3);
        var loop = Assert.Single(loops);
        // The outline runs halfway between the pixel centres: the 10 x 10 square with half-pixel corner chamfers.
        Assert.Equal(99.5, Math.Abs(loop.SignedArea()), 6);
        var box = loop.Vertices.Select(v => v.Point).ToList();
        Assert.Equal(5, box.Min(p => p.X), 6);
        Assert.Equal(15, box.Max(p => p.X), 6);
        Assert.Equal(5, box.Min(p => p.Y), 6);
        Assert.Equal(15, box.Max(p => p.Y), 6);
        // Straight edges collapse to few points.
        Assert.Equal(8, loop.Count);
    }

    [Fact]
    public void Ring_traces_to_outline_and_hole()
    {
        // Ring between radius 4 and 8 px around the centre of a 20 x 20 px image.
        var gray = Image(20, 20, (x, y) =>
        {
            var d = Math.Sqrt((x - 9.5) * (x - 9.5) + (y - 9.5) * (y - 9.5));
            return d is >= 4 and <= 8;
        });
        var loops = ImageGeometry.TraceOutlines(gray, 20, 0.5, invert: false, simplify: 0.3);
        Assert.Equal(2, loops.Count);
        var areas = loops.Select(l => Math.Abs(l.SignedArea())).Order().ToList();
        Assert.InRange(areas[0], Math.PI * 3.5 * 3.5 * 0.8, Math.PI * 4 * 4 * 1.1);
        Assert.InRange(areas[1], Math.PI * 8 * 8 * 0.9, Math.PI * 8.5 * 8.5 * 1.1);
    }

    [Fact]
    public void Invert_traces_the_light_areas_and_shapes_touching_the_border_close()
    {
        // All black except a white square: inverted, the white square is traced.
        var gray = Image(10, 10, (x, y) => !(x is >= 2 and < 6 && y is >= 2 and < 6));
        var loop = Assert.Single(ImageGeometry.TraceOutlines(gray, 10, 0.5, invert: true, simplify: 0.3));
        Assert.Equal(15.5, Math.Abs(loop.SignedArea()), 6);
        // Not inverted, the black area touching the border still closes (outer outline plus hole).
        Assert.Equal(2, ImageGeometry.TraceOutlines(gray, 10, 0.5, invert: false, simplify: 0.5).Count);
    }

    [Fact]
    public void Blank_image_has_no_outlines()
    {
        Assert.Empty(ImageGeometry.TraceOutlines(Image(8, 8, (_, _) => false), 8, 0.5, false, 1));
    }
}
