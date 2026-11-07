using Avalonia.Media;
using BamCam.App.Drawing;
using BamCam.App.Services;
using BamCam.Core.Geometry;

namespace BamCam.Tests;

/// <summary>Meshes as shaded surfaces: which pixels they cover, what hides what, and how they are lit.</summary>
public class SurfaceShaderTests
{
    private static readonly Color Green = Color.FromRgb(100, 200, 50);

    private static ViewTransform TopView(double scale = 1, double panY = 40) => new() { Scale = scale, Pan = new Avalonia.Point(0, panY) };

    private static ShadedMesh Mesh(Color colour, params Point3[] triangleCorners) =>
        new([.. triangleCorners], [.. Enumerable.Range(0, triangleCorners.Length)], Matrix4.Identity, colour);

    /// <summary>A rectangle of two triangles at a height.</summary>
    private static ShadedMesh Rectangle(double x0, double y0, double x1, double y1, double z, Color colour, Matrix4? transform = null) =>
        new([new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z)], [0, 1, 2, 0, 2, 3], transform ?? Matrix4.Identity, colour);

    private static (int[] Pixels, int[] Owners) Draw(ViewTransform view, int size, double scaling, params ShadedMesh[] meshes)
    {
        var (pixels, depth, owners) = (new int[size * size], new float[size * size], new int[size * size]);
        SurfaceShader.Rasterize(meshes, view, scaling, size, size, pixels, depth, owners);
        return (pixels, owners);
    }

    private static (int[] Pixels, int[] Owners) Draw(params ShadedMesh[] meshes) => Draw(TopView(), 40, 1, meshes);

    [Fact]
    public void A_rectangle_covers_its_pixels_and_nothing_else()
    {
        // World Y up, screen Y down: y from 10 to 20 with pan 40 are the rows 20 to 29.
        var (pixels, _) = Draw(Rectangle(5, 10, 15, 20, 0, Green));

        Assert.Equal(100, pixels.Count(p => p != 0));
        Assert.NotEqual(0, pixels[20 * 40 + 5]);
        Assert.NotEqual(0, pixels[29 * 40 + 14]);
        Assert.Equal(0, pixels[19 * 40 + 5]);
        Assert.Equal(0, pixels[20 * 40 + 4]);
    }

    [Fact]
    public void A_fine_mesh_at_any_position_has_no_holes()
    {
        var mesh = new ShadedMesh([], [], Matrix4.Identity, Green);
        const int n = 60;
        for (var y = 0; y <= n; y++)
        for (var x = 0; x <= n; x++)
            mesh.Vertices.Add(new Point3(5.17 + x * 0.413 + y * 0.03, 6.31 + y * 0.417 - x * 0.02, x * 0.1));
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            int a = y * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
            mesh.Faces.AddRange([a, b, d, a, d, c]);
        }

        var (pixels, _) = Draw(new ViewTransform { Scale = 1, Pan = new Avalonia.Point(0, 40) }, 40, 1, mesh);

        for (var y = 12; y < 28; y++)
        for (var x = 10; x < 26; x++)
            Assert.True(pixels[y * 40 + x] != 0, $"hole at {x}, {y}");
    }

    [Fact]
    public void The_nearer_surface_hides_the_other_whatever_the_order()
    {
        var low = Rectangle(0, 0, 20, 20, 0, Color.FromRgb(255, 0, 0));
        var high = Rectangle(5, 5, 15, 15, 3, Color.FromRgb(0, 0, 255));

        var (first, firstOwners) = Draw(low, high);
        var (second, secondOwners) = Draw(high, low);

        var middle = 30 * 40 + 10;
        Assert.Equal(first[middle], second[middle]);
        Assert.True((first[middle] & 255) > ((first[middle] >> 16) & 255), $"{first[middle]:X}"); // blue, with a white shine at most
        Assert.Equal(2, firstOwners[middle]);
        Assert.Equal(1, secondOwners[middle]);
        Assert.Equal(1, firstOwners[38 * 40 + 1]);
    }

    [Fact]
    public void In_a_turned_view_what_was_below_is_in_front()
    {
        // Looked at from below: the lower surface is now nearer.
        var view = new ViewTransform { Scale = 1, Pan = new Avalonia.Point(0, 40) };
        view.SetRotation(0, Math.PI);

        var (_, owners) = Draw(view, 40, 1, Rectangle(0, -20, 20, 0, 0, Green), Rectangle(0, -20, 20, 0, 3, Green));

        Assert.Equal(1, owners[50 * 0 + 30 * 40 + 10]);
    }

    [Fact]
    public void A_surface_crossing_another_is_cut_at_the_crossing()
    {
        // Across X the tilted one rises from below to above the flat one at x = 20.
        var tilted = Mesh(Green, new(0, 0, -10), new(40, 0, 10), new(40, 40, 10), new(0, 0, -10), new(40, 40, 10), new(0, 40, -10));
        var flat = Rectangle(0, 0, 40, 40, 0, Green);

        var (_, owners) = Draw(flat, tilted);

        Assert.Equal(1, owners[20 * 40 + 10]);
        Assert.Equal(2, owners[20 * 40 + 30]);
    }

    [Fact]
    public void Both_sides_of_a_triangle_look_the_same()
    {
        var (up, _) = Draw(Mesh(Green, new(0, 0, 0), new(30, 0, 5), new(0, 30, 0)));
        var (down, _) = Draw(Mesh(Green, new(0, 0, 0), new(0, 30, 0), new(30, 0, 5)));

        Assert.Equal(up[35 * 40 + 5], down[35 * 40 + 5]);
        Assert.NotEqual(0, up[35 * 40 + 5]);
    }

    [Fact]
    public void The_light_makes_a_shape_of_a_slope()
    {
        // Light from the upper left: of two slopes, the one that faces it is brighter than the one that faces away.
        // Rising towards the right, a slope faces left; rising towards the bottom of the picture, it faces up.
        var (left, _) = Draw(Mesh(Green, new(0, 0, 0), new(30, 0, 10), new(30, 30, 10), new(0, 0, 0), new(30, 30, 10), new(0, 30, 0)));
        var (right, _) = Draw(Mesh(Green, new(0, 0, 10), new(30, 0, 0), new(30, 30, 0), new(0, 0, 10), new(30, 30, 0), new(0, 30, 10)));
        var (up, _) = Draw(Mesh(Green, new(0, 0, 10), new(30, 0, 10), new(30, 30, 0), new(0, 0, 10), new(30, 30, 0), new(0, 30, 0)));
        var (down, _) = Draw(Mesh(Green, new(0, 0, 0), new(30, 0, 0), new(30, 30, 10), new(0, 0, 0), new(30, 30, 10), new(0, 30, 10)));

        var pixel = 25 * 40 + 15;
        int Brightness(int[] p) => (p[pixel] >> 8) & 255;
        Assert.True(Brightness(left) > Brightness(right) + 20, $"{left[pixel]:X} and {right[pixel]:X}");
        Assert.True(Brightness(up) > Brightness(down) + 20, $"{up[pixel]:X} and {down[pixel]:X}");
    }

    [Fact]
    public void A_surface_seen_from_the_side_is_dark_but_not_black()
    {
        var pixel = SurfaceShader.ShadePixel(1, 0, 0, Color.FromRgb(255, 255, 255));

        Assert.InRange(pixel & 255, 40, 120);
    }

    [Fact]
    public void Colour_stays_premultiplied_with_transparency()
    {
        var pixel = SurfaceShader.ShadePixel(0, 0, 1, Color.FromArgb(128, 255, 255, 255));

        Assert.Equal(128, (pixel >> 24) & 255);
        Assert.InRange(pixel & 255, 1, 128);
    }

    [Fact]
    public void The_transform_of_the_object_counts()
    {
        var (_, owners) = Draw(Rectangle(0, 0, 5, 5, 0, Green, Matrix4.Translation(20, 10)));

        Assert.Equal(1, owners[27 * 40 + 22]);
        Assert.Equal(0, owners[37 * 40 + 2]);
    }

    [Fact]
    public void Rendering_scale_gives_more_pixels()
    {
        var (pixels, _) = Draw(TopView(), 80, 2, Rectangle(5, 10, 15, 20, 0, Green));

        Assert.Equal(400, pixels.Count(p => p != 0));
    }

    [Theory]
    [InlineData(-1000, -1000, -900, -900)] // beside the picture
    [InlineData(5000, 5000, 6000, 6000)]
    public void What_lies_outside_is_not_drawn(double x0, double y0, double x1, double y1)
    {
        var (pixels, _) = Draw(Rectangle(x0, y0, x1, y1, 0, Green));

        Assert.All(pixels, p => Assert.Equal(0, p));
    }

    [Fact]
    public void A_surface_much_larger_than_the_picture_fills_it()
    {
        var (pixels, _) = Draw(Rectangle(-1e6, -1e6, 1e6, 1e6, 0, Green));

        Assert.All(pixels, p => Assert.NotEqual(0, p));
    }

    [Fact]
    public void Broken_or_empty_meshes_do_no_harm()
    {
        var flat = Mesh(Green, new(0, 0, 0), new(10, 0, 0), new(20, 0, 0)); // on a line
        var nan = Mesh(Green, new(double.NaN, 0, 0), new(10, 0, 0), new(0, 10, 0));
        var empty = new ShadedMesh([], [], Matrix4.Identity, Green);

        var (pixels, _) = Draw(flat, nan, empty);

        Assert.All(pixels, p => Assert.Equal(0, p));
    }

    [Fact]
    public void A_click_finds_its_own_surface_only()
    {
        var owners = new int[100];
        owners[5 * 10 + 5] = 2;

        Assert.Equal(0, SurfaceShader.NearestOwned(owners, 10, 10, 2, 5.5, 5.5, 3));
        Assert.Equal(2, SurfaceShader.NearestOwned(owners, 10, 10, 2, 7.2, 5.1, 3));
        Assert.True(double.IsInfinity(SurfaceShader.NearestOwned(owners, 10, 10, 1, 5.5, 5.5, 3)));
        Assert.True(double.IsInfinity(SurfaceShader.NearestOwned(owners, 10, 10, 2, -50, 500, 3)));
    }

    [Fact]
    public void A_line_on_a_surface_is_picked_before_the_surface()
    {
        // A line at 4 pixels with tolerance 5 still comes first, also over a surface right under the mouse.
        Assert.True(4 < SurfaceShader.HitDistance(0, 5, 5));
        // Among surfaces, the one under the mouse comes first, and all count as hits.
        Assert.True(SurfaceShader.HitDistance(0, 5, 5) < SurfaceShader.HitDistance(2, 5, 5));
        Assert.True(SurfaceShader.HitDistance(5, 5, 5) <= 5);
    }

    [Fact]
    public void Shaded_is_the_default_and_the_choice_is_kept()
    {
        var settings = new AppSettings();
        var view = new ViewSettings();
        Assert.True(settings.ShadeSurfaces);
        Assert.True(view.ShadeSurfaces);

        view.ShadeSurfaces = false;
        settings.CaptureFrom(view);
        Assert.False(settings.ShadeSurfaces);

        var other = new ViewSettings();
        settings.ApplyTo(other, Core.Model.DrawingUnits.Millimeters);
        Assert.False(other.ShadeSurfaces);
    }
}
