// ============================================================================
// Revolve XY Polyline - BamCam plugin (C#, .NET 10)
//
// Takes a polyline drawn in the XY plane, rotates it into the XZ plane
// (90 deg about X: (x, y, 0) -> (x, 0, y)), then revolves it about the Z axis
// through a specified angle, producing a mesh surface (SurfaceEntity).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BamCam.Core.Geometry;   // Point2, Point3
using BamCam.Core.Model;      // SurfaceEntity
using BamCam.Plugins;         // IBamCamPlugin, IPluginHost, PluginCommand

namespace RevolvePolylinePlugin
{
    public sealed class RevolvePlugin : IBamCamPlugin
    {
        public void Initialize(IPluginHost host)
        {
            var german = host.Language == "de";
            host.AddCommand(new PluginCommand
            {
                Header = german ? "_Rotationsfläche XY..." : "_Revolve XY...",
                Menu = "Plugins",                  // "Plugins", "Draw", "Edit" or "Machining"
                Description = german ? "Erzeugt eine Fläche, indem eine Form um die Z-Achse gedreht wird" : "Creates a surface by revolving a shape about the Z axis",
                RequiresSelection = true,
                Execute = async ctx => await RevolveCommand.Run(ctx, german),
            });
        }
    }

    internal static class RevolveCommand
    {
        public static async Task Run(ICommandContext ctx, bool german)
        {
            // 1. First selected entity with geometry. ToPaths() works for any entity type
            //    and returns drawing coordinates.
            var profile = ProfileReader.Read(ctx);
            if (profile == null || profile.Count < 2)
            {
                ctx.Log(german
                    ? "Rotationsfläche: Wählen Sie eine Polylinie (oder Rechteck, Linie, Bogen, Kreis, Spline) mit mindestens zwei Punkten."
                    : "Revolve: select a polyline (or rectangle/line/arc/circle/spline) with at least two points.");
                return;
            }

            // 2. Angle.
            var angle = await ctx.AskNumber(german ? "Rotationsfläche XY" : "Revolve XY",
                german ? "Drehwinkel (Grad, 0-360):" : "Revolve angle (degrees, 0-360):", 360);
            if (angle == null) return;                         // cancelled
            if (angle <= 0 || angle > 360)
            {
                ctx.Log(german
                    ? "Rotationsfläche: Der Winkel muss größer als 0 und höchstens 360 Grad sein."
                    : "Revolve: angle must be greater than 0 and at most 360 degrees.");
                return;
            }

            if (RevolveMath.CrossesAxis(profile))
                ctx.Log(german
                    ? "Rotationsfläche: Warnung - die Form kreuzt die Z-Achse; die Fläche überschneidet sich selbst."
                    : "Revolve: warning - the profile crosses the Z axis; the surface will overlap itself.");

            // 3. XY -> XZ happens inside the revolve: (x, y) becomes radius x, height y.
            var (verts, tris) = RevolveMath.BuildMesh(profile, angle.Value);

            // 4. One undoable step, added to the active layer and selected.
            ctx.AddEntities(german ? "Rotationsfläche XY" : "Revolve XY polyline", new[] { MeshFactory.Create(verts, tris) });
        }
    }

    // ------------------------------------------------------------------------
    // Reads the selected entity's first path as XY points, with arcs
    // flattened into short chords.
    // ------------------------------------------------------------------------
    internal static class ProfileReader
    {
        private const double ChordTol = 0.01;   // max deviation when flattening arcs (drawing units)

        public static List<(double X, double Y)>? Read(ICommandContext ctx)
        {
            var entity = ctx.Selection.FirstOrDefault();
            if (entity == null) return null;

            var path = entity.ToPaths().FirstOrDefault();
            if (path == null || path.Vertices.Count == 0) return null;

            var pts = new List<(double X, double Y)>();
            foreach (Point2 p in path.Flatten(ChordTol))
                AddPoint(pts, p.X, p.Y);

            // A closed path ends where it starts (Flatten leaves out the closing point).
            var last = path.Closed ? path.Vertices[0] : path.Vertices[^1];
            AddPoint(pts, last.Point.X, last.Point.Y);
            return pts;
        }

        private static void AddPoint(List<(double X, double Y)> pts, double x, double y)
        {
            if (pts.Count == 0 ||
                Math.Abs(pts[^1].X - x) > RevolveMath.Tol || Math.Abs(pts[^1].Y - y) > RevolveMath.Tol)
                pts.Add((x, y));
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

            // A full turn shares the seam: the last ring is the first one.
            bool full = Math.Abs(angleDeg - 360) < 1e-9;
            int rings = full ? steps : steps + 1;

            var verts = new List<(double X, double Y, double Z)>();
            for (int i = 0; i < profile.Count; i++)
            {
                double r = profile[i].X, z = profile[i].Y;
                for (int j = 0; j < rings; j++)
                {
                    double t = angleDeg * Math.PI / 180.0 * j / steps;
                    verts.Add((r * Math.Cos(t), r * Math.Sin(t), z));
                }
            }

            int Idx(int i, int j) => i * rings + (full ? j % steps : j);
            var tris = new List<(int, int, int)>();
            for (int i = 0; i < profile.Count - 1; i++)
            {
                bool r0Zero = Math.Abs(profile[i].X) < Tol;
                bool r1Zero = Math.Abs(profile[i + 1].X) < Tol;
                if (r0Zero && r1Zero) continue;                  // segment lies on the axis

                for (int j = 0; j < steps; j++)
                {
                    int a = Idx(i, j), b = Idx(i, j + 1), c = Idx(i + 1, j + 1), d = Idx(i + 1, j);
                    if (r0Zero) tris.Add((a, c, d));             // a == b
                    else if (r1Zero) tris.Add((a, b, c));        // c == d
                    else { tris.Add((a, b, c)); tris.Add((a, c, d)); }
                }
            }
            return (verts, tris);
        }
    }

    // ------------------------------------------------------------------------
    // Builds the SurfaceEntity. BamCam shades both sides of a mesh alike,
    // so triangle winding is not critical for display.
    // ------------------------------------------------------------------------
    internal static class MeshFactory
    {
        public static SurfaceEntity Create(
            List<(double X, double Y, double Z)> verts, List<(int A, int B, int C)> tris)
        {
            var mesh = new SurfaceEntity();
            mesh.ShareMesh(
                verts.Select(v => new Point3(v.X, v.Y, v.Z)).ToList(),
                tris.SelectMany(t => new[] { t.A, t.B, t.C }).ToList());
            return mesh;
        }
    }
}
