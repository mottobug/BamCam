// ============================================================================
// Revolve XY Polyline - BamCam plugin (C#, .NET 10)
//
// Takes a polyline drawn in the XY plane, rotates it into the XZ plane
// (90 deg about X: (x, y, 0) -> (x, 0, y)), then revolves it about the Z axis
// through a specified angle, producing a mesh surface (SurfaceEntity).
//
// Verified against BamCam's docs/plugins.md and porting guide:
//   IBamCamPlugin.Initialize(IPluginHost), host.AddCommand(new PluginCommand{...}),
//   ctx.Selection, ctx.AskNumber, ctx.AddEntities, ctx.Log, entity.ToPaths(),
//   Point2/Point3 (double), Path2 with Vertex(point, bulge), SurfaceEntity.
//
// NOT verified (the guides don't show these signatures) - marked UNVERIFIED:
//   * the exact member names on Path2 / Vertex (Vertices, Point, Bulge, Closed)
//   * the element type of ctx.Selection
//   * the SurfaceEntity constructor
//   * the exact `using` namespaces for IBamCamPlugin & co.
// Each is confined to a single place; fix them against the BamCam.Core /
// BamCam.PluginApi assemblies (or samples/BamCam.SamplePlugin) if the compiler
// complains. All maths below is plain C#.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BamCam.Core.Geometry;   // Point2, Point3, Path2, Vertex   (UNVERIFIED namespace list)
using BamCam.Core.Model;      // SurfaceEntity, PolylineEntity   (namespace per porting guide)
using BamCam.Plugins;       // IBamCamPlugin, IPluginHost, PluginCommand (UNVERIFIED)
//using BamCam.Core.Cam;

namespace RevolvePolylinePlugin
{
    public sealed class RevolvePlugin : IBamCamPlugin
    {
        
        public void Initialize(IPluginHost host)
        {
            var german = host.Language == "de";
            host.AddCommand(new PluginCommand
            {
                Header = german ? "_Revolve XY..." : "_Revolve XY...",
                Menu = "Plugins",                  // "Plugins", "Draw", "Edit" or "Machining"
                Description = german ? "Erzeugt eine Oberfläche, indem eine Form über die Z-Achse gedreht wird" : "Creates a surface by revolving a Shape about the Z-axis",
                RequiresSelection = true,
                Execute = async ctx => await RevolveCommand.Run(ctx),
            });
        }
    }

    internal static class RevolveCommand
    {
        public static async Task Run(ICommandContext ctx)      // ICommandContext per plugins.md
        {
            // 1. First selected entity with geometry. ToPaths() works for any entity type
            //    and returns drawing coordinates (porting guide).
            var profile = ProfileReader.Read(ctx);
            if (profile == null || profile.Count < 2)
            {
                ctx.Log("Revolve: select a polyline (or rectangle/line/arc/circle/spline) with at least two points.");
                return;
            }

            // 2. Angle.
            var angle = await ctx.AskNumber("Revolve XY", "Revolve angle (degrees, 0-360):", 360);
            if (angle == null) return;                         // cancelled
            if (angle <= 0 || angle > 360)
            {
                ctx.Log("Revolve: angle must be greater than 0 and at most 360 degrees.");
                return;
            }

            if (RevolveMath.CrossesAxis(profile))
                ctx.Log("Revolve: warning - the profile crosses the Z axis; the surface will overlap itself.");

            // 3. XY -> XZ happens inside the revolve: (x, y) becomes radius x, height y.
            var (verts, tris) = RevolveMath.BuildMesh(profile, angle.Value);

            // 4. One undoable step, added to the active layer and selected.
            ctx.AddEntities("Revolve XY polyline", new[] { MeshFactory.Create(verts, tris) });
        }
    }

    // ------------------------------------------------------------------------
    // Reads the selected entity's first path as XY points, expanding arc
    // (bulge) segments into short chords.
    // ------------------------------------------------------------------------
    internal static class ProfileReader
    {
        private const double ChordTol = 0.01;   // max deviation when flattening arcs (drawing units)

        public static List<(double X, double Y)>? Read(ICommandContext ctx)
        {
            // UNVERIFIED: assumes ctx.Selection enumerates entities that have ToPaths().
            var entity = ctx.Selection.FirstOrDefault();
            if (entity == null) return null;

            var path = entity.ToPaths().FirstOrDefault();      // Path2
            if (path == null) return null;



            // UNVERIFIED member names: path.Vertices, vertex.Point (Point2), vertex.Bulge, path.Closed.
            var v = path.Vertices.ToList();
            var pts = new List<(double X, double Y)>();
            int segCount = path.Closed ? v.Count : v.Count - 1;

            var tpts = new List<Point2>();
            tpts = path.Flatten(ChordTol); //  as per suggestion from TK
            foreach (Point2 p in tpts)
            {
                AddPoint(pts, p.X, p.Y);
            }

            //for (int i = 0; i < segCount; i++)
            //{
            //    var a = v[i];
            //    var b = v[(i + 1) % v.Count];
            //    AddPoint(pts, a.Point.X, a.Point.Y);
            //    if (Math.Abs(a.Bulge) > 1e-12)
            //    {
            //       FlattenArc(pts, a.Point.X, a.Point.Y, b.Point.X, b.Point.Y, a.Bulge);
            //    }
            //}
            var last = path.Closed ? v[0] : v[v.Count - 1];
            AddPoint(pts, last.Point.X, last.Point.Y);
            return pts;
        }

        private static void AddPoint(List<(double X, double Y)> pts, double x, double y)
        {
            if (pts.Count == 0 ||
                Math.Abs(pts[^1].X - x) > RevolveMath.Tol || Math.Abs(pts[^1].Y - y) > RevolveMath.Tol)
                pts.Add((x, y));
        }

        // Bulge = tan(theta / 4), theta = included arc angle, positive = counter-clockwise.
        private static void FlattenArc(List<(double X, double Y)> pts,
                                       double x1, double y1, double x2, double y2, double bulge)
        {
            double theta = 4.0 * Math.Atan(bulge);
            double chord = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            if (chord < RevolveMath.Tol) return;

            // Standard bulge geometry: R = chord(1+b^2)/(4|b|); the centre lies on the left normal of
            // the chord at signed distance chord(1-b^2)/(4b) from the midpoint (the sign of b
            // flips the side for clockwise arcs, and the sign of (1-b^2) handles arcs over 180 deg).
            double radius = chord * (1.0 + bulge * bulge) / (4.0 * Math.Abs(bulge));
            double mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
            double nx = -(y2 - y1) / chord, ny = (x2 - x1) / chord;     // left normal
            double d = chord * (1.0 - bulge * bulge) / (4.0 * bulge);
            double cx = mx + nx * d;
            double cy = my + ny * d;

            double a0 = Math.Atan2(y1 - cy, x1 - cx);
            double stepMax = 2.0 * Math.Acos(Math.Max(0.0, 1.0 - ChordTol / radius));
            if (double.IsNaN(stepMax) || stepMax < 1e-3) stepMax = 1e-3;
            int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(theta) / stepMax));

            for (int k = 1; k < n; k++)
            {
                double a = a0 + theta * k / n;
                AddPoint(pts, cx + radius * Math.Cos(a), cy + radius * Math.Sin(a));
            }
        }
    }

    // ------------------------------------------------------------------------
    // Pure geometry: XY profile -> XZ -> revolve about Z -> triangle mesh.
    // ------------------------------------------------------------------------
    internal static class RevolveMath
    {
        public const double Tol = 1e-9;
        private const double DeviationTol = 0.01;     // max chord deviation of the revolved facets
        private const double MaxStepDeg = 10.0;

        public static bool CrossesAxis(IEnumerable<(double X, double Y)> pts)
        {
            bool neg = false, pos = false;
            foreach (var p in pts)
            {
                if (p.X < -Tol) neg = true;
                if (p.X > Tol) pos = true;
            }
            return neg && pos;
        }

        public static (List<(double X, double Y, double Z)> Verts, List<(int A, int B, int C)> Tris)
            BuildMesh(IReadOnlyList<(double X, double Y)> profile, double angleDeg)
        {
            // XY -> XZ: (x, y, 0) -> (x, 0, y). Radius r = x, height z = y.
            double rMax = profile.Max(p => Math.Abs(p.X));
            double stepDeg = MaxStepDeg;
            if (rMax > DeviationTol)
                stepDeg = Math.Min(stepDeg, 2.0 * Math.Acos(1.0 - DeviationTol / rMax) * 180.0 / Math.PI);
            int steps = Math.Clamp((int)Math.Ceiling(angleDeg / stepDeg), 2, 720);

            var verts = new List<(double X, double Y, double Z)>();

            // Changes suggest by TK to cleanly match seam at 360 deg wrap

            bool full = Math.Abs(angleDeg - 360) < 1e-9; //=== TK
            int rings = full ? steps: steps + 1;         //=== TK


            for (int i = 0; i < profile.Count; i++)
            {
                double r = profile[i].X, z = profile[i].Y;
                
              // for (int j = 0; j <= steps; j++)                 // seam vertices are duplicated
               for (int j = 0; j < rings; j++)          // === TK
                    {
                    double t = angleDeg * Math.PI / 180.0 * j / steps;
                   
                    verts.Add((r * Math.Cos(t), r * Math.Sin(t), z));
                    
               }
            }

            //int Idx(int i, int j) => i * (steps + 1) + j;
            int Idx(int i, int j) => i * rings + (full ? j % steps : j);  //=== TK
            var tris = new List<(int, int, int)>();
            for (int i = 0; i < profile.Count - 1; i++)
            {
                bool r0Zero = Math.Abs(profile[i].X) < Tol;
                bool r1Zero = Math.Abs(profile[i + 1].X) < Tol;
                if (r0Zero && r1Zero) continue;                  // segment lies on the axis

                for (int j = 0; j < steps; j++)
                {
                    int a = Idx(i, j), b = Idx(i, j + 1), c = Idx(i + 1, j + 1), d = Idx(i + 1, j);
                    if (r0Zero) tris.Add((a, c, d));        // a == b
                    else if (r1Zero) tris.Add((a, b, c));        // c == d
                    else { tris.Add((a, b, c)); tris.Add((a, c, d)); }
                }
            }

            


            return (verts, tris);
        }
    }

    // ------------------------------------------------------------------------
    // The only place that touches SurfaceEntity's constructor.
    // BamCam shades both sides of a mesh alike (release notes), so triangle
    // winding is not critical for display.
    // ------------------------------------------------------------------------
    internal static class MeshFactory
    {
        public static SurfaceEntity Create(
            List<(double X, double Y, double Z)> verts, List<(int A, int B, int C)> tris)
        {
            // UNVERIFIED: the guides only say "add a new SurfaceEntity". Adjust to the real
            // constructor (vertex list type, triangle index layout) from BamCam.Core.
            var points = verts.Select(v => new Point3(v.X, v.Y, v.Z)).ToList();
            var indices = tris.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();

            //return new SurfaceEntity(points, indices);
            var mesh = new SurfaceEntity();
            foreach (var p in points)
            {
                mesh.Vertices.Add(p);
            }
            foreach(var v in indices)
            {
                mesh.Faces.Add(v);
            }
           

            return mesh;
        }
    }
}
