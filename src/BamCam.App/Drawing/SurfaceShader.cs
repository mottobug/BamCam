using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.App.Drawing;

/// <summary>A mesh to shade: its geometry and base colour.</summary>
public readonly record struct ShadedMesh(List<Point3> Vertices, List<int> Faces, Matrix4 Transform, Color Colour);

/// <summary>
/// All visible meshes drawn as shaded surfaces into one bitmap, with one depth buffer, so that they hide each other
/// correctly. A wireframe of a fine mesh only shows its outline. The bitmap is only drawn again when the view or a mesh
/// changed, and everything else (lines, toolpaths) is drawn over it.
/// </summary>
public sealed class SurfaceShader
{
    /// <summary>Calculation memory that is used again for the next picture: a large mesh would fill the heap at every frame.</summary>
    public sealed class Scratch
    {
        public double[] Xs = [], Ys = [], Zs = [];
        public int[] RowMin = [], RowMax = [], Colours = [];

        public void Ensure(int vertices, int triangles)
        {
            if (Xs.Length < vertices)
            {
                Xs = new double[vertices];
                Ys = new double[vertices];
                Zs = new double[vertices];
            }
            if (RowMin.Length < triangles)
            {
                RowMin = new int[triangles];
                RowMax = new int[triangles];
                Colours = new int[triangles];
            }
        }
    }

    private const double Ambient = 0.28, Diffuse = 0.72, Shine = 0.22;
    private const int ShineSharpness = 24;

    // In camera space (x right, y up, z towards the viewer): the light comes from the upper left, behind the viewer,
    // so that the shape can be read from every direction, as the view turns with the light.
    private static readonly (double X, double Y, double Z) Light = Normalized(-0.35, 0.45, 0.82);
    private static readonly (double X, double Y, double Z) HalfWay = Normalized(Light.X, Light.Y, Light.Z + 1);

    private readonly PixelLayer _layer = new();
    private readonly Scratch _scratch = new();
    private float[] _depth = [];
    private int[] _owners = [];
    private (int Width, int Height, double Scale, Point Pan, double Yaw, double Pitch, double Scaling)? _viewKey;
    private ShadedMesh[] _meshes = [];

    private static (double X, double Y, double Z) Normalized(double x, double y, double z)
    {
        var length = Math.Sqrt(x * x + y * y + z * z);
        return (x / length, y / length, z / length);
    }

    /// <summary>The pixel of a surface, premultiplied, for a normal in camera space of any length and direction.</summary>
    public static int ShadePixel(double nx, double ny, double nz, Color colour)
    {
        var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (length <= 0) return PixelLayer.Pixel(colour);
        // Both sides are lit: the triangles of many STL files do not all point outwards.
        if (nz < 0) length = -length;
        nx /= length;
        ny /= length;
        nz /= length;
        var light = Ambient + Diffuse * Math.Max(nx * Light.X + ny * Light.Y + nz * Light.Z, 0);
        var half = Math.Max(nx * HalfWay.X + ny * HalfWay.Y + nz * HalfWay.Z, 0);
        var shine = half * half;
        for (var i = 2; i < ShineSharpness; i *= 2) shine *= shine;
        shine *= Shine * 255;
        int Part(byte value) => (int)Math.Min(value * light + shine, 255) * colour.A / 255;
        return (colour.A << 24) | (Part(colour.R) << 16) | (Part(colour.G) << 8) | Part(colour.B);
    }

    /// <summary>
    /// Draws the meshes. <paramref name="pixels"/> and <paramref name="owners"/> (index of the mesh + 1 for each pixel, 0
    /// for none) are width x height and cleared first.
    /// </summary>
    public static void Rasterize(IReadOnlyList<ShadedMesh> meshes, ViewTransform view, double scaling, int width, int height,
        int[] pixels, float[] depth, int[] owners, Scratch? scratch = null)
    {
        scratch ??= new Scratch();
        Array.Clear(pixels);
        Array.Clear(owners);
        Array.Fill(depth, float.NegativeInfinity);
        // Each band of rows belongs to one thread, so that no pixel is touched by two. More bands than processors: the
        // model usually fills only a part of the picture, and a free thread then takes the next band.
        var bands = Math.Clamp(Environment.ProcessorCount * 4, 1, Math.Max(height, 1));
        for (var m = 0; m < meshes.Count; m++)
        {
            var mesh = meshes[m];
            var triangles = mesh.Faces.Count / 3;
            if (triangles == 0) continue;
            scratch.Ensure(mesh.Vertices.Count, triangles);
            Project(mesh, view, scaling, scratch);
            Prepare(mesh, triangles, view.Scale * scaling, width, height, scratch);
            var owner = m + 1;
            Parallel.For(0, bands, band =>
            {
                var from = (int)((long)height * band / bands);
                var to = (int)((long)height * (band + 1) / bands);
                DrawBand(mesh.Faces, triangles, from, to, width, pixels, depth, owners, owner, scratch);
            });
        }
    }

    private static void Project(in ShadedMesh mesh, ViewTransform view, double scaling, Scratch scratch)
    {
        var (vertices, transform) = (mesh.Vertices, mesh.Transform);
        var identity = transform.IsIdentity;
        var (xs, ys, zs) = (scratch.Xs, scratch.Ys, scratch.Zs);
        var (scale, panX, panY) = (view.Scale, view.Pan.X, view.Pan.Y);
        Parallel.ForEach(Partitioner.Create(0, vertices.Count), range =>
        {
            for (var i = range.Item1; i < range.Item2; i++)
            {
                var (x, y, z) = view.Rotate(identity ? vertices[i] : transform.Transform(vertices[i]));
                xs[i] = (x * scale + panX) * scaling;
                ys[i] = (-y * scale + panY) * scaling;
                zs[i] = z;
            }
        });
    }

    /// <summary>Per triangle: its rows in the picture (none when it is not to be seen) and its colour.</summary>
    private static void Prepare(in ShadedMesh mesh, int triangles, double pixelsPerUnit, int width, int height, Scratch scratch)
    {
        var (faces, colour) = (mesh.Faces, mesh.Colour);
        var (xs, ys, zs, rowMin, rowMax, colours) = (scratch.Xs, scratch.Ys, scratch.Zs, scratch.RowMin, scratch.RowMax, scratch.Colours);
        Parallel.ForEach(Partitioner.Create(0, triangles), range =>
        {
            for (var t = range.Item1; t < range.Item2; t++)
            {
                rowMin[t] = int.MaxValue;
                rowMax[t] = int.MinValue;
                int a = faces[3 * t], b = faces[3 * t + 1], c = faces[3 * t + 2];
                double ax = xs[a], ay = ys[a], bx = xs[b], by = ys[b], cx = xs[c], cy = ys[c];
                var minY = Math.Min(ay, Math.Min(by, cy));
                var maxY = Math.Max(ay, Math.Max(by, cy));
                var minX = Math.Min(ax, Math.Min(bx, cx));
                var maxX = Math.Max(ax, Math.Max(bx, cx));
                // Written so that a point that is not a number leaves the triangle out.
                if (!(maxX >= 0 && maxY >= 0 && minX < width && minY < height)) continue;
                double ux = bx - ax, uy = by - ay, vx = cx - ax, vy = cy - ay;
                var area = ux * vy - vx * uy;
                if (area == 0 || !double.IsFinite(area)) continue;
                rowMin[t] = (int)Math.Max(Math.Ceiling(minY - 0.5), 0);
                rowMax[t] = (int)Math.Min(Math.Floor(maxY - 0.5), height - 1);
                // The normal in camera space. Screen Y runs down, and depth is in drawing units.
                double uz = (zs[b] - zs[a]) * pixelsPerUnit, vz = (zs[c] - zs[a]) * pixelsPerUnit;
                colours[t] = ShadePixel(-uy * vz + uz * vy, uz * vx - ux * vz, -(ux * vy - uy * vx), colour);
            }
        });
    }

    private static void DrawBand(List<int> faces, int triangles, int from, int to, int width,
        int[] pixels, float[] depth, int[] owners, int owner, Scratch scratch)
    {
        var (xs, ys, zs, rowMin, rowMax, colours) = (scratch.Xs, scratch.Ys, scratch.Zs, scratch.RowMin, scratch.RowMax, scratch.Colours);
        for (var t = 0; t < triangles; t++)
        {
            if (rowMax[t] < from || rowMin[t] >= to) continue;
            int a = faces[3 * t], b = faces[3 * t + 1], c = faces[3 * t + 2];
            double ax = xs[a], ay = ys[a], az = zs[a], bx = xs[b], by = ys[b], bz = zs[b], cx = xs[c], cy = ys[c], cz = zs[c];
            var area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            if (area < 0)
            {
                (bx, by, bz, cx, cy, cz) = (cx, cy, cz, bx, by, bz);
                area = -area;
            }
            var firstX = (int)Math.Max(Math.Ceiling(Math.Min(ax, Math.Min(bx, cx)) - 0.5), 0);
            var lastX = (int)Math.Min(Math.Floor(Math.Max(ax, Math.Max(bx, cx)) - 0.5), width - 1);
            if (firstX > lastX) continue;
            var colour = colours[t];
            double stepA = cy - by, stepB = ay - cy, stepC = by - ay;
            var lastRow = Math.Min(rowMax[t], to - 1);
            for (var y = Math.Max(rowMin[t], from); y <= lastRow; y++)
            {
                double px = firstX + 0.5, py = y + 0.5;
                // Each is twice the area of the triangle from this point to one edge; all positive means inside. The
                // edge itself belongs to both of its triangles, so that there is no gap between them.
                var wa = (cx - bx) * (py - by) - (cy - by) * (px - bx);
                var wb = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);
                var wc = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
                var row = y * width;
                var entered = false;
                for (var x = firstX; x <= lastX; x++, wa -= stepA, wb -= stepB, wc -= stepC)
                {
                    if (wa < 0 || wb < 0 || wc < 0)
                    {
                        if (entered) break;
                        continue;
                    }
                    entered = true;
                    var z = (float)((wa * az + wb * bz + wc * cz) / area);
                    if (z <= depth[row + x]) continue;
                    depth[row + x] = z;
                    pixels[row + x] = colour;
                    owners[row + x] = owner;
                }
            }
        }
    }

    /// <summary>Distance in pixels from a position to the nearest pixel of a mesh within <paramref name="radius"/>, or infinity.</summary>
    public static double NearestOwned(int[] owners, int width, int height, int owner, double x, double y, int radius)
    {
        var best = double.PositiveInfinity;
        int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y);
        for (var py = Math.Max(cy - radius, 0); py <= Math.Min(cy + radius, height - 1); py++)
        for (var px = Math.Max(cx - radius, 0); px <= Math.Min(cx + radius, width - 1); px++)
        {
            if (owners[py * width + px] != owner) continue;
            double dx = px - cx, dy = py - cy;
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
        }
        return best;
    }

    /// <summary>
    /// The distance a hit on a shaded surface counts as: behind anything else within the tolerance, because lines lie over
    /// the surface and would otherwise never be picked. The nearer of two surfaces still comes first.
    /// </summary>
    public static double HitDistance(double pixels, double tolerance, int radius) =>
        tolerance * (0.9 + 0.1 * pixels / Math.Max(radius, 1));

    private void Update(IReadOnlyList<ShadedMesh> meshes, ViewTransform view, Size size, double scaling)
    {
        var width = Math.Max((int)Math.Ceiling(size.Width * scaling), 1);
        var height = Math.Max((int)Math.Ceiling(size.Height * scaling), 1);
        var key = (width, height, view.Scale, view.Pan, view.Yaw, view.Pitch, scaling);
        if (_viewKey == key && _meshes.SequenceEqual(meshes)) return;
        _layer.Resize(width, height);
        if (_depth.Length != width * height)
        {
            _depth = new float[width * height];
            _owners = new int[width * height];
        }
        Rasterize(meshes, view, scaling, width, height, _layer.Pixels, _depth, _owners, _scratch);
        _layer.MarkChanged();
        _viewKey = key;
        _meshes = [.. meshes];
    }

    /// <summary>Shows the meshes; they are only drawn again when something they depend on changed.</summary>
    public void Draw(DrawingContext dc, IReadOnlyList<ShadedMesh> meshes, ViewTransform view, Size size, double scaling)
    {
        Update(meshes, view, size, scaling);
        _layer.Draw(dc, scaling);
    }

    /// <summary>Hit test of one of the meshes, as an order among the other hits (see <see cref="HitDistance"/>); otherwise infinity.</summary>
    public double DistanceTo(IReadOnlyList<ShadedMesh> meshes, int index, ViewTransform view, Size size, double scaling, Point screen, double tolerance)
    {
        Update(meshes, view, size, scaling);
        var radius = (int)Math.Ceiling(tolerance * scaling);
        var pixels = NearestOwned(_owners, _layer.Width, _layer.Height, index + 1, screen.X * scaling, screen.Y * scaling, radius);
        return double.IsInfinity(pixels) ? pixels : HitDistance(pixels, tolerance, radius);
    }
}
