using System.Globalization;
using BamCam.Core.Geometry;

namespace BamCam.Core.Cam;

/// <summary>
/// Parses basic G-code (G0/G1/G2/G3, G17, G20/G21, G90/G91, G90.1/G91.1, F, comments) into a toolpath
/// for back-plotting. Subroutines and canned cycles beyond G81-G83 are not interpreted.
/// </summary>
public static class GCodeParser
{
    public static Toolpath Parse(string text, bool absoluteArcCenters = false)
    {
        var tp = new Toolpath();
        var pos = new Point3(0, 0, 0);
        var absolute = true;
        var arcAbs = absoluteArcCenters;
        var motion = 0;
        double feed = 0;
        var started = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripComments(rawLine).ToUpperInvariant();
            if (line.Length == 0) continue;
            var words = Words(line);
            double? x = null, y = null, z = null, i = null, j = null, r = null;
            var hasMotionWord = false;
            foreach (var (letter, value) in words)
            {
                switch (letter)
                {
                    case 'G':
                        var g = Math.Round(value, 1);
                        if (g is 0 or 1 or 2 or 3 or 81 or 82 or 83) { motion = (int)g; hasMotionWord = true; }
                        else if (g == 90) absolute = true;
                        else if (g == 91) absolute = false;
                        else if (g == 90.1) arcAbs = true;
                        else if (g == 91.1) arcAbs = false;
                        else if (g == 80) motion = 0;
                        break;
                    case 'X': x = value; break;
                    case 'Y': y = value; break;
                    case 'Z': z = value; break;
                    case 'I': i = value; break;
                    case 'J': j = value; break;
                    case 'R': r = value; break;
                    case 'F': feed = value; break;
                }
            }
            if (x == null && y == null && z == null) continue;
            _ = hasMotionWord;

            var target = absolute
                ? new Point3(x ?? pos.X, y ?? pos.Y, z ?? pos.Z)
                : new Point3(pos.X + (x ?? 0), pos.Y + (y ?? 0), pos.Z + (z ?? 0));
            if (!started)
            {
                tp.StartPosition = target with { Z = Math.Max(target.Z, pos.Z) };
                started = true;
            }

            switch (motion)
            {
                case 0:
                    tp.Add(Move.Rapid(target));
                    break;
                case 2:
                case 3:
                {
                    Point2 center;
                    if (i != null || j != null)
                        center = arcAbs ? new Point2(i ?? pos.X, j ?? pos.Y) : new Point2(pos.X + (i ?? 0), pos.Y + (j ?? 0));
                    else if (r is { } radius)
                        center = CenterFromRadius(pos.XY, target.XY, radius, motion == 2);
                    else
                    {
                        tp.Add(Move.Line(target, feed));
                        break;
                    }
                    tp.Add(Move.Arc(target, center, motion == 2, feed));
                    break;
                }
                case 81 or 82 or 83:
                    tp.Add(Move.Drill(target, feed, new DrillCycle(r ?? pos.Z, 0, 0)));
                    target = target with { Z = pos.Z };
                    break;
                default:
                    tp.Add(Move.Line(target, feed));
                    break;
            }
            pos = target;
        }
        return tp;
    }

    private static Point2 CenterFromRadius(Point2 a, Point2 b, double r, bool clockwise)
    {
        var mid = Point2.Lerp(a, b, 0.5);
        var half = Point2.Distance(a, b) / 2;
        var h = Math.Sqrt(Math.Max(r * r - half * half, 0));
        var dir = (b - a).Normalized().Perp;
        // Positive R = arc <= 180°, negative = > 180°.
        var sign = (clockwise ? -1 : 1) * (r >= 0 ? 1 : -1);
        return mid + dir * (h * sign);
    }

    private static string StripComments(string line)
    {
        var sb = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var c in line)
        {
            if (c == ';') break;
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (depth == 0) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static List<(char Letter, double Value)> Words(string line)
    {
        var result = new List<(char, double)>();
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (char.IsLetter(c))
            {
                var start = ++i;
                while (i < line.Length && (char.IsDigit(line[i]) || line[i] is '.' or '-' or '+' || line[i] == ' ' && i == start)) i++;
                var num = line[start..i].Trim();
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) result.Add((c, v));
            }
            else i++;
        }
        return result;
    }
}
