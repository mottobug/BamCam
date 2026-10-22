using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Localization;
using BamCam.Core.Model;

namespace BamCam.Core.Post;

/// <summary>What to post: whole drawing, one part or one operation.</summary>
public sealed record PostScope(Part? Part = null, MachineOp? Operation = null)
{
    public static readonly PostScope All = new();
}

public sealed class PostResult
{
    public string Text { get; init; } = "";
    public List<string> Warnings { get; } = [];
    public int LineCount { get; init; }
}

/// <summary>
/// Expands post processor templates over generated toolpaths to produce G-code.
/// Implements the {$macro} language of CamBam-style post processors.
/// </summary>
public sealed partial class GCodePoster
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly CadDocument _doc;
    private readonly PostProcessorDefinition _post;
    private readonly List<Job> _jobs;
    private readonly Dictionary<string, string> _userMacros = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];
    private readonly DateTime _now;

    // Modal output state: last emitted formatted value per register (null = unknown).
    private readonly Dictionary<char, string?> _last = [];
    private string? _lastG;
    private double _posZ = double.NaN;
    private int _currentTool = -1;
    private string? _spindleState;
    private string? _workplaneState;
    private string? _velocityState;
    private bool _cutting;

    private Job? _job;
    private ToolInfo? _toolContext;

    private sealed record Job(Part Part, MachineOp Op, Toolpath Toolpath, Matrix4 Copy, Point2 Origin);
    private sealed record ToolInfo(int Index, double Diameter, ToolProfile Profile, string Comment = "");

    private GCodePoster(CadDocument doc, PostProcessorDefinition post, List<Job> jobs, DateTime now)
    {
        _doc = doc;
        _post = post;
        _jobs = jobs;
        _now = now;
        foreach (var src in new[] { post.PostProcessorMacros, doc.Machining.PostProcessorMacros })
        foreach (var line in (src ?? "").Split(['\r', '\n', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith('#') || !t.StartsWith('$')) continue;
            var eq = t.IndexOf('=');
            if (eq > 1) _userMacros[t[1..eq].Trim()] = t[(eq + 1)..].Trim();
        }
    }

    public static PostResult Post(CadDocument doc, IReadOnlyDictionary<MachineOp, Toolpath> toolpaths,
        PostProcessorDefinition post, PostScope? scope = null, DateTime? now = null)
    {
        scope ??= PostScope.All;
        var jobs = new List<Job>();
        foreach (var part in doc.Parts)
        {
            if (scope.Part != null && part != scope.Part) continue;
            if (!part.Enabled && scope.Part == null && scope.Operation == null) continue;
            var origin = part.MachiningOrigin.Point?.XY ?? doc.Machining.MachiningOrigin.Point?.XY ?? Point2.Zero;
            var copies = part.Nesting.NestMethod != NestMethod.None && part.NestingItems.Count > 0
                ? part.NestingItems.Select(i => i.Transform * Matrix4.Translation(i.Location.X, i.Location.Y)).ToList()
                : [Matrix4.Identity];
            foreach (var op in part.Operations)
            {
                if (scope.Operation != null && op != scope.Operation) continue;
                if (!op.Enabled && scope.Operation == null) continue;
                if (!toolpaths.TryGetValue(op, out var tp) || tp.Moves.Count == 0) continue;
                foreach (var copy in copies) jobs.Add(new Job(part, op, tp, copy, origin));
            }
        }

        var poster = new GCodePoster(doc, post, jobs, now ?? DateTime.Now);
        var raw = poster.Expand(post.PostFile);
        var lines = poster.Normalize(raw);
        var eol = post.EndOfLineText;
        var result = new PostResult { Text = string.Join(eol, lines) + eol, LineCount = lines.Count };
        result.Warnings.AddRange(poster._warnings);
        if (jobs.Count == 0) result.Warnings.Add(Tr.T("No toolpaths to post. Generate toolpaths first."));
        return result;
    }

    // ------------------------------------------------------------------ text handling

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultiSpace();

    private List<string> Normalize(string raw)
    {
        var result = new List<string>();
        var lineNo = _post.LineNumberStart;
        foreach (var l in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = MultiSpace().Replace(l, " ").Trim(' ', '\r');
            if (line.Trim().Length == 0) continue;
            if (_post.UpperCase) line = line.ToUpperInvariant();
            if (_post.AddLineNumbers && (line.Length == 0 || !_post.LineNumberSkip.Contains(line[0])))
            {
                line = _post.LineNumberPrefix + lineNo.ToString(_post.LineNumberFormat, Inv) + (_post.LineNumberSpaceAfter ? " " : "") + line;
                lineNo += _post.LineNumberIncrement;
            }
            result.Add(line);
        }
        return result;
    }

    /// <summary>Expands all {$...} macros in a template, left to right.</summary>
    private string Expand(string template)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var sb = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var start = template.IndexOf("{$", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            sb.Append(template, i, start - i);
            var end = FindMacroEnd(template, start + 2);
            if (end < 0)
            {
                sb.Append(template, start, template.Length - start);
                break;
            }
            var body = template.Substring(start + 2, end - start - 2);
            sb.Append(EvalMacro(body));
            i = end + 1;
        }
        return sb.ToString();
    }

    private static int FindMacroEnd(string s, int from)
    {
        var depth = 0;
        for (var i = from; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '(': depth++; break;
                case ')': depth--; break;
                case '}' when depth <= 0: return i;
            }
        }
        return -1;
    }

    private string Num(double v)
    {
        var s = v.ToString(_post.NumberFormat, Inv);
        // Avoid "-0.0".
        if (s.StartsWith('-') && s.Trim('-', '0', '.').Length == 0) s = s[1..];
        return s;
    }

    // ------------------------------------------------------------------ macro evaluation

    private string EvalMacro(string body)
    {
        string name = body.Trim();
        string? args = null;
        var paren = name.IndexOf('(');
        if (paren > 0 && name.EndsWith(')'))
        {
            args = name[(paren + 1)..^1];
            name = name[..paren];
        }
        var key = name.ToLowerInvariant();
        var op = _job?.Op;

        switch (key)
        {
            case "header": return Expand(_post.Header);
            case "footer": return Expand(_post.Footer);
            case "mops": return Mops();
            case "blocks": return _job == null ? "" : Blocks(_job);
            case "comment": return _post.Comment;
            case "endcomment": return _post.EndComment;
            case "cbfile.name": return _doc.Name;
            case "cbfile.header": return Expand(_doc.Machining.CustomFileHeader.Replace("|", "\r\n"));
            case "cbfile.footer": return Expand(_doc.Machining.CustomFileFooter.Replace("|", "\r\n"));
            case "date": return _now.ToString("G", CultureInfo.GetCultureInfo("en-US"));
            case "units": return _doc.Units == DrawingUnits.Inches ? _post.UnitsInches : _post.UnitsMetric;
            case "distancemode": return _post.DistanceAbsolute;
            case "cuttercomp":
                return (args ?? "off").Trim().ToLowerInvariant() switch
                {
                    "l" or "left" => _post.CutterCompLeft,
                    "r" or "right" => _post.CutterCompRight,
                    _ => _post.CutterCompOff,
                };
            case "arccentermode":
                return _doc.Machining.ArcCenterMode == ArcCenterMode.Absolute ? _post.ArcCenterAbsolute : _post.ArcCenterIncremental;
            case "lathexmode": return _post.LatheXMode == LatheXMode.Diameter ? _post.XModeDiameter : _post.XModeRadius;
            case "velocitymode": return VelocityMode();
            case "workplane": return Workplane();
            case "spindle": return Spindle(args);
            case "s": return SpindleSpeed();
            case "endrewind": return _post.EndRewind;
            case "repeat": return _post.Repeat;
            case "stop": return _post.Stop;
            case "clearance": return Clearance();
            case "toolchange": return ToolChange(args?.Trim().Equals("first", StringComparison.OrdinalIgnoreCase) == true);
            case "post.toolchange": return Expand(_post.ToolChange);
            case "tooltable": return ToolTable();
            case "set": return SetRegister(args);
            case "check": return "";

            case "tool.index": return Tool()?.Index.ToString(Inv) ?? "";
            case "tool.diameter": return Tool() is { } td ? Num(td.Diameter) : "";
            case "tool.radius": return Tool() is { } tr ? Num(tr.Profile is ToolProfile.BallNose or ToolProfile.BullNose or ToolProfile.Lathe ? tr.Diameter / 2 : 0) : "";
            case "tool.length": return Num(0);
            case "tool.profile": return Tool()?.Profile.ToString() ?? "";
            case "tool.name": return Tool() is { } tn ? $"{Num(tn.Diameter)} {tn.Profile}" : "";
            // Free text of the tool library entry; it may contain macros ({$comment}, {$tool.radius}, ...).
            case "tool.comment": return Tool() is { Comment.Length: > 0 } tc ? Expand(tc.Comment) : "";
            case "tool.veeangle": return Num(0);

            case "mop.name": return op?.Name ?? "";
            case "mop.tag": return op?.Tag ?? "";
            case "mop.header": return op == null ? "" : Expand(op.CustomMOPHeader.Value.Replace("|", "\r\n"));
            case "mop.footer": return op == null ? "" : Expand(op.CustomMOPFooter.Value.Replace("|", "\r\n"));
            case "mop.clearanceplane": return op == null ? "" : Num(op.ClearancePlane.Value);
            case "mop.cutfeedrate": return op == null ? "" : Num(op.CutFeedrate.Value);
            case "mop.plungefeedrate": return op == null ? "" : Num(op.PlungeFeedrate.Value);
            case "mop.stocksurface": return op == null ? "" : Num(op.StockSurface.Value);
            case "mop.targetdepth": return op is DepthOp d1 ? Num(d1.TargetDepth.Value) : "";
            case "mop.depthincrement": return op is DepthOp d2 ? Num(d2.DepthIncrement.Value) : "";
            case "mop.retractheight": return op is DrillOp dr ? Num(dr.RetractHeight.Value) : "";
            case "mop.peckdistance": return op is DrillOp dp ? Num(dp.PeckDistance.Value) : "";
            case "mop.dwell": return op is DrillOp dw ? Num(dw.Dwell.Value) : "";
            case "mop.holediameter": return op is DrillOp dh ? Num(dh.HoleDiameter.Value) : "";
            case "mop.first.x": return First()?.X is { } fx ? Num(fx) : "";
            case "mop.first.y": return First()?.Y is { } fy ? Num(fy) : "";
            case "mop.first.z": return First()?.Z is { } fz ? Num(fz) : "";
            case "part.name": return _job?.Part.Name ?? "";
        }

        if (key.StartsWith("stock")) return StockMacro(key);

        // G-code word macros, modal with leading underscore.
        var modal = key.StartsWith('_');
        var word = modal ? key[1..] : key;
        if (word is "g0" or "g1" or "g2" or "g3" or "g81" or "g82" or "g83")
        {
            var text = word switch
            {
                "g0" => _post.G0, "g1" => _post.G1, "g2" => _post.G2, "g3" => _post.G3,
                "g81" => _post.G81, "g82" => _post.G82, _ => _post.G83,
            };
            if (modal && text == _lastG) return "";
            _lastG = text;
            return text;
        }

        if (_userMacros.TryGetValue(name, out var user)) return Expand(user);

        if (!_post.SuppressParserErrors) _warnings.Add(Tr.F("Unknown post processor macro: {{${0}}}", name));
        return "";
    }

    private string StockMacro(string key)
    {
        var s = _job?.Part.Stock is { IsEmpty: false } ps ? ps : _doc.Machining.Stock;
        var size = s.Size;
        return key switch
        {
            "stock.xsize" or "stock.width" or "stock_width" => Num(size.X),
            "stock.ysize" or "stock.length" or "stock_length" => Num(size.Y),
            "stock.zsize" or "stock.height" or "stock_height" => Num(size.Z),
            "stock.xoffset" => Num(s.PMin.X),
            "stock.yoffset" => Num(s.PMin.Y),
            "stock_x" => Num(-s.PMin.X),
            "stock_y" => Num(-s.PMin.Y),
            "stock_z" => Num(-s.PMin.Z),
            _ => "",
        };
    }

    private Point3? First()
    {
        if (_job == null) return null;
        foreach (var m in _job.Toolpath.Moves)
            if (m.IsMotion || m.Kind == MoveKind.DrillCycle) return Out(m.To);
        return null;
    }

    private ToolInfo? Tool() => _toolContext ?? (_job is { } j ? ToolOf(j) : null);

    private ToolInfo ToolOf(Job j)
    {
        var number = j.Op.ToolNumber.Value;
        var part = _doc.Parts.FirstOrDefault(p => p.Operations.Contains(j.Op));
        var comment = CamLibraries.FindTool(_doc, part, number)?.Comment ?? "";
        return new(number, j.Op.EffectiveToolDiameter(_doc), j.Op.ToolProfile.Value, comment);
    }

    private string Mops()
    {
        var sb = new StringBuilder();
        foreach (var job in _jobs)
        {
            _job = job;
            sb.Append(Expand(_post.MOP)).Append("\r\n");
        }
        _job = null;
        return sb.ToString();
    }

    private string VelocityMode()
    {
        var mode = _job?.Op.VelocityMode.Value is { } vm and not Cam.VelocityMode.Undefined and not Cam.VelocityMode.Default
            ? vm
            : _doc.Machining.VelocityMode is Cam.VelocityMode.ExactStop ? Cam.VelocityMode.ExactStop : Cam.VelocityMode.ConstantVelocity;
        var text = mode == Cam.VelocityMode.ExactStop ? _post.VelocityModeExactStop : _post.VelocityModeConstantVelocity;
        if (text == _velocityState) return "";
        _velocityState = text;
        return text;
    }

    private string Workplane()
    {
        var plane = _job?.Op.WorkPlane.Value ?? Cam.WorkPlane.XY;
        var text = plane switch { Cam.WorkPlane.XZ => _post.WorkplaneXZ, Cam.WorkPlane.YZ => _post.WorkplaneYZ, _ => _post.WorkplaneXY };
        if (text == _workplaneState) return "";
        _workplaneState = text;
        return text;
    }

    private string Spindle(string? args)
    {
        string text;
        var a = args?.Trim().ToLowerInvariant();
        if (a == "off") text = _post.SpindleOff;
        else if (a == "cw") text = _post.SpindleCW;
        else if (a == "ccw") text = _post.SpindleCCW;
        else
        {
            var dir = _job?.Op.SpindleDirection.Value ?? SpindleDirection.CW;
            text = dir switch { SpindleDirection.CCW => _post.SpindleCCW, SpindleDirection.Off => _post.SpindleOff, _ => _post.SpindleCW };
        }
        if (args == null && text == _spindleState) return "";
        _spindleState = text;
        return text;
    }

    private string SpindleSpeed()
    {
        if (_job == null) return "";
        // Not modal: the reference output repeats the S word in every operation.
        return "S" + _job.Op.SpindleSpeed.Value.ToString("0.###", Inv);
    }

    private double ClearanceZ() => (_job?.Op ?? _jobs.FirstOrDefault()?.Op)?.ClearancePlane.Value ?? 0;

    private string Clearance()
    {
        if (_post.ClearancePlaneAxis.Trim().Equals("X", StringComparison.OrdinalIgnoreCase))
        {
            // Turning: retract radially; the clearance plane is a radius.
            return MoveLine(_post.Rapid, MoveKind.Rapid, new Dictionary<char, double> { ['X'] = LatheX(ClearanceZ()) });
        }
        var z = ClearanceZ();
        if (!double.IsNaN(_posZ) && _posZ >= z - 1e-9) return "";
        return MoveLine(_post.Rapid, MoveKind.Rapid, new Dictionary<char, double> { ['Z'] = z }, forceEmit: true);
    }

    private string ToolChange(bool first)
    {
        var job = first ? _jobs.FirstOrDefault() : _job;
        if (job == null) return "";
        var tool = ToolOf(job);
        // Tool 0 still gets the initial tool change (reference output: "T0 M6"); later a 0 means "no change".
        if (tool.Index == _currentTool || tool.Index < 0 || (tool.Index == 0 && !first)) return "";
        _currentTool = tool.Index;
        _toolContext = tool;
        var saved = _job;
        _job ??= job;
        var text = Expand(_post.ToolChange);
        _job = saved;
        _toolContext = null;
        return text;
    }

    private string ToolTable()
    {
        var sb = new StringBuilder();
        var seen = new HashSet<int>();
        foreach (var job in _jobs)
        {
            var t = ToolOf(job);
            if (t.Index < 0 || !seen.Add(t.Index)) continue;
            _toolContext = t;
            sb.Append(Expand(_post.ToolTableItem)).Append("\r\n");
        }
        _toolContext = null;
        return sb.ToString();
    }

    private string SetRegister(string? args)
    {
        if (args == null) return "";
        var parts = args.Split(',');
        if (parts.Length != 2) return "";
        var reg = char.ToUpperInvariant(parts[0].Trim()[0]);
        var val = parts[1].Trim();
        if (val.Equals("NaN", StringComparison.OrdinalIgnoreCase))
        {
            _last[reg] = null;
            if (reg == 'Z') _posZ = double.NaN;
        }
        else if (double.TryParse(val, NumberStyles.Float, Inv, out var d))
        {
            _last[reg] = Num(d);
            if (reg == 'Z') _posZ = d;
        }
        return "";
    }

    // ------------------------------------------------------------------ motion blocks

    private Point3 Out(Point3 p)
    {
        var j = _job!;
        var t = j.Copy.Transform(p);
        return new Point3(t.X - j.Origin.X, t.Y - j.Origin.Y, t.Z);
    }

    private Point2 OutXY(Point2 p)
    {
        var j = _job!;
        var t = j.Copy.Transform(p);
        return new Point2(t.X - j.Origin.X, t.Y - j.Origin.Y);
    }

    private string Blocks(Job job)
    {
        if (job.Op is NcFileOp nc)
        {
            // NC file operations are copied verbatim; the controller state afterwards is unknown.
            var path = ToolpathGenerator.ResolveNcPath(_doc, nc);
            _last.Clear();
            _posZ = double.NaN;
            _lastG = null;
            return path == null ? "" : File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n').Replace("\n", "\r\n") + "\r\n";
        }
        if (job.Op is LatheOp) return LatheBlocks(job);
        var sb = new StringBuilder();
        var mirrored = job.Copy.DeterminantXY < 0;
        var stockSurface = job.Op.StockSurface.Value;
        var inCanned = false;
        Point3? pos = null;

        void Line(string s)
        {
            if (s.Length > 0) sb.Append(s).Append("\r\n");
        }

        foreach (var m in job.Toolpath.Moves)
        {
            if (m.Kind != MoveKind.DrillCycle && inCanned)
            {
                Line(Expand(_post.CannedCycleEnd));
                inCanned = false;
            }
            switch (m.Kind)
            {
                case MoveKind.Comment:
                    Line($"{_post.Comment} {m.Text} {_post.EndComment}");
                    continue;
                case MoveKind.Dwell:
                    continue;
                case MoveKind.DrillCycle:
                {
                    if (!inCanned)
                    {
                        Line(Expand(_post.CannedCycleStart));
                        inCanned = true;
                    }
                    var p = Out(m.To);
                    var regs = new Dictionary<char, double> { ['X'] = p.X, ['Y'] = p.Y, ['Z'] = p.Z, ['R'] = m.Cycle.RetractZ, ['F'] = m.Feed };
                    var tpl = _post.Drill;
                    if (m.Cycle.Peck > 0) { tpl = _post.DrillPeck; regs['Q'] = m.Cycle.Peck; }
                    else if (m.Cycle.Dwell > 0) { tpl = _post.DrillDwell; regs['P'] = m.Cycle.Dwell; }
                    Line(MoveLine(tpl, MoveKind.DrillCycle, regs, forceEmit: true, keepZ: true));
                    pos = m.To with { Z = _posZ };
                    continue;
                }
            }

            var target = Out(m.To);
            CutState(m, target.Z, stockSurface, Line);

            if (m.Kind == MoveKind.Rapid)
            {
                var cur = _last.TryGetValue('X', out _) ? (double?)_posZ : null;
                var xyChanges = !SameXY(target);
                var zChanges = double.IsNaN(_posZ) || Math.Abs(_posZ - target.Z) > 1e-9;
                if (xyChanges && zChanges && !double.IsNaN(_posZ) && target.Z > _posZ)
                {
                    // Retract first, then move across.
                    Line(MoveLine(_post.Rapid, MoveKind.Rapid, new() { ['Z'] = target.Z }));
                    Line(MoveLine(_post.Rapid, MoveKind.Rapid, new() { ['X'] = target.X, ['Y'] = target.Y }));
                }
                else if (xyChanges && zChanges)
                {
                    Line(MoveLine(_post.Rapid, MoveKind.Rapid, new() { ['X'] = target.X, ['Y'] = target.Y }));
                    Line(MoveLine(_post.Rapid, MoveKind.Rapid, new() { ['Z'] = target.Z }));
                }
                else
                {
                    Line(MoveLine(_post.Rapid, MoveKind.Rapid, new() { ['X'] = target.X, ['Y'] = target.Y, ['Z'] = target.Z }));
                }
                _ = cur;
            }
            else if (m.Kind == MoveKind.Feed)
            {
                Line(MoveLine(_post.FeedMove, MoveKind.Feed, new() { ['X'] = target.X, ['Y'] = target.Y, ['Z'] = target.Z, ['F'] = m.Feed }));
            }
            else if (m.IsArc && pos is { } from)
            {
                EmitArc(m, from, target, mirrored, Line);
            }
            else if (m.IsArc)
            {
                Line(MoveLine(_post.FeedMove, MoveKind.Feed, new() { ['X'] = target.X, ['Y'] = target.Y, ['Z'] = target.Z, ['F'] = m.Feed }));
            }
            pos = m.To;
        }
        if (inCanned) Line(Expand(_post.CannedCycleEnd));
        if (_cutting && _post.EndCut.Length > 0)
        {
            Line(Expand(_post.EndCut));
            _cutting = false;
        }
        return sb.ToString();
    }

    /// <summary>Lathe X word value: radius, or diameter when the post is in diameter mode.</summary>
    private double LatheX(double radius) => _post.LatheXMode == LatheXMode.Diameter ? radius * 2 : radius;

    /// <summary>
    /// Turning moves: drawing X (minus origin) becomes lathe Z, the distance from the spindle axis lathe X.
    /// Arcs use I (along X) and K (along Z); their direction is taken in the Z-X view and flipped when the
    /// post sets Invert Arcs.
    /// </summary>
    private string LatheBlocks(Job job)
    {
        var sb = new StringBuilder();
        void Line(string s)
        {
            if (s.Length > 0) sb.Append(s).Append("\r\n");
        }

        // The profile lies on one side of the axis; the tool tip may cross it slightly (negative X).
        var motion = job.Toolpath.Moves.Where(m => m.IsMotion).Select(m => Out(m.To).Y).ToList();
        double side = motion.Count > 0 && motion.Average() < 0 ? -1 : 1;
        Point3? pos = null;
        foreach (var m in job.Toolpath.Moves)
        {
            if (m.Kind == MoveKind.Comment)
            {
                Line($"{_post.Comment} {m.Text} {_post.EndComment}");
                continue;
            }
            if (!m.IsMotion) continue;
            var t = Out(m.To);
            var x = LatheX(t.Y * side);
            var z = t.X;
            if (m.Kind == MoveKind.Rapid)
            {
                // Both axes: retract radially first (or travel along Z first when going in), like CamBam.
                var lastX = _last.TryGetValue('X', out var lx) ? lx : null;
                var lastZ = _last.TryGetValue('Z', out var lz) ? lz : null;
                if (lastX != null && lastZ != null && lastX != Num(x) && lastZ != Num(z))
                {
                    var outward = double.TryParse(lastX, NumberStyles.Float, Inv, out var px) && x > px;
                    if (outward)
                    {
                        Line(MoveLine(_post.Rapid, MoveKind.Rapid, new() { ['X'] = x }));
                        Line(MoveLine(_post.Rapid, MoveKind.Rapid, new() { ['Z'] = z }));
                    }
                    else
                    {
                        Line(MoveLine(_post.Rapid, MoveKind.Rapid, new() { ['Z'] = z }));
                        Line(MoveLine(_post.Rapid, MoveKind.Rapid, new() { ['X'] = x }));
                    }
                }
                else
                {
                    Line(MoveLine(_post.Rapid, MoveKind.Rapid, new() { ['X'] = x, ['Z'] = z }));
                }
            }
            else if (m.Kind == MoveKind.Feed || pos == null)
            {
                Line(MoveLine(_post.FeedMove, MoveKind.Feed, new() { ['X'] = x, ['Z'] = z, ['F'] = m.Feed }));
            }
            else
            {
                var from = Out(pos.Value);
                var center = OutXY(m.Center);
                // Below the axis the drawing is mirrored relative to the Z-X view.
                var cw = (m.Kind == MoveKind.ArcCW) ^ (side < 0) ^ (job.Copy.DeterminantXY < 0) ^ _post.InvertArcs;
                var regs = new Dictionary<char, double> { ['X'] = x, ['Z'] = z, ['F'] = m.Feed };
                var cx = center.Y * side;
                var fx = from.Y * side;
                if (_doc.Machining.ArcCenterMode == ArcCenterMode.Absolute)
                {
                    regs['I'] = LatheX(cx);
                    regs['K'] = center.X;
                }
                else
                {
                    regs['I'] = cx - fx;
                    regs['K'] = center.X - from.X;
                }
                Line(MoveLine(cw ? _post.ArcCW : _post.ArcCCW, m.Kind, regs, forceEmit: true));
            }
            pos = m.To;
        }
        return sb.ToString();
    }

    private void CutState(Move m, double z, double stockSurface, Action<string> line)
    {
        if (_post.StartCut.Length == 0 && _post.EndCut.Length == 0) return;
        var below = z < stockSurface - 1e-9;
        if (m.Kind != MoveKind.Rapid && below && !_cutting)
        {
            line(Expand(_post.StartCut));
            _cutting = true;
        }
        else if ((m.Kind == MoveKind.Rapid || !below) && _cutting)
        {
            line(Expand(_post.EndCut));
            _cutting = false;
        }
    }

    private bool SameXY(Point3 target) =>
        _last.TryGetValue('X', out var x) && x == Num(target.X) &&
        _last.TryGetValue('Y', out var y) && y == Num(target.Y);

    private void EmitArc(Move m, Point3 fromWorld, Point3 target, bool mirrored, Action<string> line)
    {
        var from = Out(fromWorld);
        var center = OutXY(m.Center);
        var cw = (m.Kind == MoveKind.ArcCW) ^ mirrored;
        var radius = Point2.Distance(from.XY, center);
        var a0 = (from.XY - center).Angle;
        var a1 = (target.XY - center).Angle;
        var sweep = cw ? -Angles.Normalize(a0 - a1) : Angles.Normalize(a1 - a0);
        if (Math.Abs(sweep) < 1e-9) sweep = cw ? -Angles.TwoPi : Angles.TwoPi;
        var length = Math.Abs(sweep) * radius;

        var asLines = _post.ArcOutput != ArcOutputMode.Normal || length < _post.MinimumArcLength || radius > _post.MaximumArcRadius;
        if (asLines)
        {
            var arc = new Arc2(center, radius, a0, sweep);
            var n = _post.ArcOutput == ArcOutputMode.Normal ? 1 : arc.SegmentCount(Math.Max(_post.ArcToLinesTolerance, 1e-4));
            for (var i = 1; i <= n; i++)
            {
                var t = (double)i / n;
                var p = i == n ? target.XY : arc.PointAt(t);
                var z = from.Z + (target.Z - from.Z) * t;
                line(MoveLine(_post.FeedMove, MoveKind.Feed, new() { ['X'] = p.X, ['Y'] = p.Y, ['Z'] = z, ['F'] = m.Feed }));
            }
            return;
        }

        var regs = new Dictionary<char, double> { ['X'] = target.X, ['Y'] = target.Y, ['Z'] = target.Z, ['F'] = m.Feed };
        if (_doc.Machining.ArcCenterMode == ArcCenterMode.Absolute)
        {
            regs['I'] = center.X;
            regs['J'] = center.Y;
        }
        else
        {
            regs['I'] = center.X - from.X;
            regs['J'] = center.Y - from.Y;
        }
        line(MoveLine(cw ? _post.ArcCW : _post.ArcCCW, m.Kind, regs, forceEmit: true));
    }

    /// <summary>
    /// Expands a move template with pending register values. Modal registers ({$_x}) are emitted only
    /// when their formatted value changes. Linear moves that change nothing are dropped.
    /// </summary>
    private string MoveLine(string template, MoveKind kind, Dictionary<char, double> regs, bool forceEmit = false, bool keepZ = false)
    {
        // Registers not set by this move keep their last values (so modal tokens print nothing).
        if (!forceEmit && kind is MoveKind.Rapid or MoveKind.Feed)
        {
            var changes = regs.Where(r => r.Key is 'X' or 'Y' or 'Z')
                .Any(r => !_last.TryGetValue(r.Key, out var last) || last != Num(r.Value));
            if (!changes) return "";
        }

        var sb = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var start = template.IndexOf("{$", i, StringComparison.Ordinal);
            if (start < 0) { sb.Append(template, i, template.Length - i); break; }
            sb.Append(template, i, start - i);
            var end = FindMacroEnd(template, start + 2);
            if (end < 0) { sb.Append(template, start, template.Length - start); break; }
            var token = template.Substring(start + 2, end - start - 2).Trim();
            sb.Append(RegisterToken(token, regs));
            i = end + 1;
        }

        if (regs.TryGetValue('Z', out var z) && !keepZ) _posZ = z;
        return sb.ToString();
    }

    private string RegisterToken(string token, Dictionary<char, double> regs)
    {
        var key = token.ToLowerInvariant();
        var modal = key.StartsWith('_');
        var core = modal ? key[1..] : key;
        if (core.Length == 1 && "xyzabcfijkrpq".Contains(core[0]))
        {
            var reg = char.ToUpperInvariant(core[0]);
            if (!regs.TryGetValue(reg, out var value))
            {
                // Not part of this move: modal prints nothing; non-modal prints the last value if known.
                if (modal) return "";
                return _last.TryGetValue(reg, out var lv) && lv != null ? reg + lv : "";
            }
            var text = Num(value);
            if (modal && _last.TryGetValue(reg, out var last) && last == text) return "";
            _last[reg] = text;
            return reg + text;
        }
        // Sign variants: {$xneg}, {$xabs}
        if (core.Length > 1 && "xyz".Contains(core[0]) && regs.TryGetValue(char.ToUpperInvariant(core[0]), out var v2))
        {
            if (core.EndsWith("neg")) return char.ToUpperInvariant(core[0]) + Num(-v2);
            if (core.EndsWith("abs")) return char.ToUpperInvariant(core[0]) + Num(Math.Abs(v2));
        }
        return EvalMacro(token);
    }
}
