using System.ComponentModel;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace InvoluteGear;

/// <summary>Draw ▸ Involute Gear: inserts a spur gear, whose values are then changed in the properties.</summary>
public sealed class GearPlugin : IBamCamPlugin
{
    public void Initialize(IPluginHost host)
    {
        var german = host.Language == "de";
        host.AddEntityType<GearEntity>();
        host.AddCommand(new PluginCommand
        {
            Header = german ? "_Evolventen-Zahnrad" : "_Involute Gear",
            Menu = "Draw",
            Description = german
                ? "Stirnrad mit Evolventenverzahnung: Zähnezahl, Modul und weitere Werte im Eigenschaftenfenster ändern"
                : "Spur gear with involute teeth: change teeth, module and other values in the properties",
            Execute = context =>
            {
                context.AddEntities(german ? "Zahnrad zeichnen" : "Draw gear", [new GearEntity()]);
                return Task.CompletedTask;
            },
        });
    }
}

/// <summary>An external spur gear around the origin, with involute flanks and the root that a rack cutter leaves.</summary>
public sealed class GearEntity : ParametricEntity
{
    public override string ParametricType => "bamcam.involutegear";
    public override string TypeName => "Involute Gear";

    [Category("Gear"), Description("Number of teeth.")]
    public int Teeth { get; set; } = 20;

    [Category("Gear"), Description("Tooth size: pitch diameter = module x teeth. Two gears that run together need the same module and pressure angle.")]
    public double Module { get; set; } = 2;

    [Category("Gear"), DisplayName("Pressure Angle"), Description("In degrees; 20 is the usual value.")]
    public double PressureAngle { get; set; } = 20;

    [Category("Gear"), DisplayName("Profile Shift"), Description("Moves the cutter outwards, in modules: positive values give stronger teeth and avoid undercut, and change the centre distance unless the shifts of both gears add up to 0.")]
    public double ProfileShift { get; set; }

    [Category("Gear"), Description("Makes each tooth thinner by this length at the pitch circle. For a pair, the sum of both values is the play between the flanks.")]
    public double Backlash { get; set; }

    [Category("Gear"), DisplayName("Bore Diameter"), Description("Hole in the centre; 0 for none.")]
    public double BoreDiameter { get; set; } = 8;

    [Category("Tooth Form"), Description("Height of the tooth above the pitch circle, in modules; usually 1.")]
    public double Addendum { get; set; } = 1;

    [Category("Tooth Form"), Description("Depth of the tooth below the pitch circle, in modules; usually 1.25.")]
    public double Dedendum { get; set; } = 1.25;

    [Category("Tooth Form"), DisplayName("Cutter Tip Radius"), Description("Radius of the rounded tip of the rack cutter, in modules; it makes the rounding at the tooth root. Usually 0.38.")]
    public double CutterTipRadius { get; set; } = 0.38;

    [Category("Tooth Form"), DisplayName("Points per Flank"), Description("Straight segments of each flank and of each root rounding.")]
    public int PointsPerFlank { get; set; } = 16;

    private GearGeometry Geometry => new(Teeth, Module, PressureAngle, ProfileShift, Backlash, Addendum, Dedendum, CutterTipRadius, PointsPerFlank);

    [Category("Dimensions"), DisplayName("Pitch Diameter")] public double PitchDiameter => 2 * Geometry.PitchRadius;
    [Category("Dimensions"), DisplayName("Base Diameter")] public double BaseDiameter => 2 * Geometry.BaseRadius;
    [Category("Dimensions"), DisplayName("Tip Diameter")] public double TipDiameter => 2 * Geometry.TipRadius;
    [Category("Dimensions"), DisplayName("Root Diameter")] public double RootDiameter => 2 * Geometry.RootRadius;

    [Category("Dimensions"), DisplayName("Tooth Thickness"), Description("Along the pitch circle.")]
    public double ToothThickness => Geometry.ToothThickness;

    [Category("Dimensions"), DisplayName("Largest Tool Diameter"), Description("A larger end mill leaves material in the tooth gaps or at the root.")]
    public double LargestToolDiameter => Geometry.LargestToolDiameter;

    [Category("Dimensions"), DisplayName("Note")]
    public string Note => Geometry.Note;

    public override IReadOnlySet<string> HiddenParameters =>
        Note.Length == 0 ? new HashSet<string> { nameof(Note) } : new HashSet<string>();

    protected override IEnumerable<Path2>? Generate()
    {
        var geometry = Geometry;
        yield return geometry.Outline();
        if (BoreDiameter > 0 && BoreDiameter / 2 < geometry.RootRadius)
            yield return new Path2([new Vertex(new Point3(BoreDiameter / 2, 0, 0), 1), new Vertex(new Point3(-BoreDiameter / 2, 0, 0), 1)], true);
    }
}

/// <summary>
/// The tooth of a gear cut by a rack: rolling the rack over the pitch circle gives the involute flank with its straight
/// flank, and the trochoid at the root with its rounded tip. With few teeth the trochoid cuts into the flank (undercut).
/// Rack coordinates: u along the rolling line with a gap centred at u = 0, v above it, away from the gear centre.
/// </summary>
public sealed class GearGeometry
{
    private const int MeasureFactor = 4;

    private readonly double _module, _tanAlpha, _alpha, _flankOffset, _tipCentreU, _tipCentreV, _tipRadius;
    private readonly int _teeth;
    private readonly int _flankPoints;
    private readonly List<string> _notes = [];

    public double PitchRadius { get; }
    public double BaseRadius { get; }
    public double TipRadius { get; }
    public double RootRadius { get; }
    public double ToothThickness { get; }
    public bool IsUndercut { get; }
    public bool IsPointed { get; }

    /// <summary>One side of a tooth, on its positive-angle side: from the tip circle down to the root circle.</summary>
    public IReadOnlyList<Point2> Side { get; }

    public double LargestToolDiameter { get; }
    public string Note => string.Join(" ", _notes);

    public GearGeometry(int teeth, double module, double pressureAngle, double shift, double backlash, double addendum, double dedendum, double cutterTip, int pointsPerFlank)
    {
        _teeth = Math.Clamp(teeth, 4, 2000);
        _module = Math.Max(Finite(module, 2), 1e-3);
        _alpha = Angles.ToRadians(Math.Clamp(Finite(pressureAngle, 20), 5, 40));
        _tanAlpha = Math.Tan(_alpha);
        _flankPoints = Math.Clamp(pointsPerFlank, 3, 500);
        var m = _module;
        var ha = Math.Clamp(Finite(addendum, 1), 0.1, 3);
        var hf = Math.Clamp(Finite(dedendum, 1.25), 0.1, 3);
        PitchRadius = m * _teeth / 2;
        BaseRadius = PitchRadius * Math.Cos(_alpha);

        // A shift must leave a root inside the rolling line, a root circle around the centre, and a tip outside the base circle.
        var wanted = Finite(shift, 0);
        var lowest = Math.Max((hf * m + 0.1 * m - PitchRadius) / m, (BaseRadius * 1.001 - PitchRadius) / m - ha);
        var x = Math.Clamp(wanted, lowest, hf - 0.05);
        if (Math.Abs(x - wanted) > 1e-9) _notes.Add($"Profile shift limited to {x:0.###}.");

        // Half the thickness of the gap of the rack on the rolling line: half of a tooth of the gear.
        var halfThickness = Math.PI * m / 4 + x * m * _tanAlpha - Math.Max(Finite(backlash, 0), 0) / 2;
        halfThickness = Math.Max(halfThickness, 0.02 * m);
        _flankOffset = halfThickness;
        ToothThickness = 2 * halfThickness;
        RootRadius = PitchRadius + x * m - hf * m;

        // The rounded tip of the rack's tooth: it touches the tip line and the flank.
        var tipV = x * m - hf * m;
        var secMinusTan = 1 / Math.Cos(_alpha) - _tanAlpha;
        var widest = (Math.PI * m / 2 * 0.999 - halfThickness + tipV * _tanAlpha) / secMinusTan; // tip without a flat part
        _tipRadius = Math.Clamp(Finite(cutterTip, 0.38) * m, 1e-4 * m, Math.Max(Math.Min(widest, -tipV * 0.999), 1e-4 * m));
        _tipCentreV = tipV + _tipRadius;
        _tipCentreU = halfThickness - _tipCentreV * _tanAlpha + _tipRadius / Math.Cos(_alpha);

        // Tip circle, limited where the two flanks of a tooth meet.
        var tip = PitchRadius + (ha + x) * m;
        var smallest = 0.002 * Math.PI / _teeth;
        if (HalfAngle(tip) < smallest)
        {
            IsPointed = true;
            double low = BaseRadius, high = tip;
            for (var i = 0; i < 60; i++)
            {
                var middle = (low + high) / 2;
                if (HalfAngle(middle) < smallest) high = middle;
                else low = middle;
            }
            tip = low;
            _notes.Add("Pointed teeth: the tip diameter is limited.");
        }
        TipRadius = Math.Max(tip, RootRadius + 1e-3 * m);

        // The straight flank of the rack reaches below the point where its line of action touches the base circle.
        var flankEndV = _tipCentreV - _tipRadius * Math.Sin(_alpha);
        var touchV = -PitchRadius * Math.Pow(Math.Sin(_alpha), 2);
        IsUndercut = flankEndV < touchV - 1e-9 * m;
        var (side, narrowest) = BuildSide();
        Side = side;
        LargestToolDiameter = narrowest;
        if (IsUndercut)
            _notes.Add($"Undercut: the root cuts into the flank. A profile shift of {x + (touchV - flankEndV) / m:0.###} or more avoids it.");
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    private static double Involute(double angle) => Math.Tan(angle) - angle;

    /// <summary>Angle from the tooth centre to the involute flank at a radius.</summary>
    public double HalfAngle(double radius)
    {
        var at = Math.Acos(Math.Clamp(BaseRadius / radius, -1, 1));
        return _flankOffset / PitchRadius + Involute(_alpha) - Involute(at);
    }

    /// <summary>A point of the rack in the coordinates of the gear when the gear has rolled by an angle.</summary>
    private Point2 RackPoint(double u, double v, double roll)
    {
        var contact = Point2.FromAngle(roll, PitchRadius);
        var normal = Point2.FromAngle(roll, 1);
        var tangent = new Point2(-normal.Y, normal.X);
        return contact + tangent * (u - PitchRadius * roll) + normal * v;
    }

    /// <summary>Where the rounded tip of the rack touches the gear at a roll angle: on the line from the rolling point.</summary>
    private Point2 TipContact(double roll)
    {
        var centre = RackPoint(_tipCentreU, _tipCentreV, roll);
        var towards = Point2.FromAngle(roll, PitchRadius) - centre;
        return centre - towards * (_tipRadius / towards.Length);
    }

    /// <summary>
    /// The point of the straight flank of the rack that touches the gear at a roll angle: the foot of the perpendicular from
    /// the rolling point. Above the base circle this is the involute; below it, the branch that an undercut leaves.
    /// </summary>
    private Point2 FlankContact(double roll)
    {
        var distance = (PitchRadius * roll - _flankOffset) * Math.Cos(_alpha);
        return RackPoint(PitchRadius * roll - distance * Math.Cos(_alpha), -distance * Math.Sin(_alpha), roll);
    }

    private (List<Point2> Side, double Narrowest) BuildSide()
    {
        // The trochoid, from where the rounding meets the flank down to the root.
        var rollAtRoot = _tipCentreU / PitchRadius;
        var rollAtFlank = (_tipCentreU - _tipCentreV / _tanAlpha) / PitchRadius;
        var samples = _flankPoints * MeasureFactor;
        var lower = new List<Point2>(samples + 1);
        for (var i = 0; i <= samples; i++) lower.Add(TipContact(rollAtFlank + (rollAtRoot - rollAtFlank) * i / samples));

        // With an undercut, the flank of the rack cuts the branch from the base circle down to the trochoid as well.
        if (IsUndercut)
        {
            var rollAtBase = (_flankOffset + PitchRadius * _tanAlpha) / PitchRadius;
            var branch = new List<Point2>(samples);
            for (var i = 0; i < samples; i++) branch.Add(FlankContact(rollAtBase + (rollAtFlank - rollAtBase) * i / samples));
            lower.InsertRange(0, branch);
        }

        // The involute from the tip circle down: to the base circle, or to where the rounding of the rack begins.
        var fine = _flankPoints * MeasureFactor;
        double RollOf(double radius) => Math.Sqrt(Math.Max(radius * radius / (BaseRadius * BaseRadius) - 1, 0));
        var rollTip = RollOf(TipRadius);
        var rollEnd = IsUndercut ? 0 : Math.Min(RollOf(lower[0].Length), rollTip);
        var flank = new List<Point2>(fine + 1);
        for (var i = 0; i <= fine; i++)
        {
            var t = rollTip + (rollEnd - rollTip) * i / fine;
            var radius = BaseRadius * Math.Sqrt(1 + t * t);
            flank.Add(Point2.FromAngle(HalfAngle(radius), radius));
        }

        // The first crossing, walking down the flank.
        for (var i = 0; i + 1 < flank.Count; i++)
        for (var j = lower.Count - 2; j >= 0; j--)
            if (Crossing(flank[i], flank[i + 1], lower[j], lower[j + 1]) is { } point)
            {
                // A crossing right at the end of the flank is only where the two curves join.
                if (i + 2 >= flank.Count && Point2.Distance(point, flank[^1]) < 1e-6 * _module) continue;
                var cutFlank = flank.Take(i + 1).Append(point).ToList();
                var cutLower = lower.Skip(j + 1).ToList();
                return Finish(cutFlank, cutLower);
            }
        return Finish(flank, lower);
    }

    private (List<Point2>, double) Finish(List<Point2> flank, List<Point2> lower)
    {
        var whole = flank.Concat(lower).ToList();
        var side = new List<Point2>();
        for (var i = 0; i < flank.Count; i += MeasureFactor) side.Add(flank[i]);
        if (side[^1] != flank[^1]) side.Add(flank[^1]);
        for (var i = MeasureFactor - 1; i < lower.Count; i += MeasureFactor) side.Add(lower[i]);
        if (lower.Count > 0 && side[^1] != lower[^1]) side.Add(lower[^1]);
        return (side, 2 * LargestToolRadius(whole));
    }

    /// <summary>
    /// The largest circle that reaches every point of a tooth gap: for each point, the circle touching it from the gap
    /// grows until it meets another point of the gap, on this flank, on the root or on the opposite flank.
    /// </summary>
    private double LargestToolRadius(List<Point2> walkedDown)
    {
        var gapAngle = Math.PI / _teeth;
        var boundary = new List<Point2>(walkedDown);
        var rootEnd = walkedDown[^1].Angle;
        for (var i = 1; i <= 8; i++) boundary.Add(Point2.FromAngle(rootEnd + (gapAngle - rootEnd) * i / 8, RootRadius));
        var count = boundary.Count;
        for (var i = count - 1; i >= 0; i--) // the opposite side of the gap, mirrored at its middle
            boundary.Add(Point2.FromAngle(2 * gapAngle - boundary[i].Angle, boundary[i].Length));

        var smallest = double.PositiveInfinity;
        for (var i = 1; i + 1 < count; i++)
        {
            // The tangent at the point itself: from its two neighbours.
            var heading = boundary[i + 1] - boundary[i - 1];
            if (heading.Length < 1e-12 * _module) continue;
            // Walking with the material on the left: the gap is on the right.
            var towardsGap = new Point2(heading.Y, -heading.X) * (1 / heading.Length);
            var largest = double.PositiveInfinity;
            foreach (var other in boundary)
            {
                var to = other - boundary[i];
                var along = Point2.Dot(towardsGap, to);
                var squared = Point2.Dot(to, to);
                // Very short bends, as where an undercut meets the flank, leave no material worth mentioning: they do not count.
                if (along > 1e-12 * _module && squared > 0.0025 * _module * _module) largest = Math.Min(largest, squared / (2 * along));
            }
            smallest = Math.Min(smallest, largest);
        }
        return double.IsFinite(smallest) ? smallest : RootRadius;
    }

    private static Point2? Crossing(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        var r = b - a;
        var s = d - c;
        var denominator = Point2.Cross(r, s);
        if (Math.Abs(denominator) < 1e-18) return null;
        var t = Point2.Cross(c - a, s) / denominator;
        var u = Point2.Cross(c - a, r) / denominator;
        return t is >= 0 and <= 1 && u is >= 0 and <= 1 ? a + r * t : null;
    }

    /// <summary>The closed outline, counter-clockwise, with a tooth centred on the X axis. Tip and root are real arcs.</summary>
    public Path2 Outline()
    {
        var vertices = new List<Vertex>((Side.Count * 2 + 2) * _teeth);
        var tipAngle = Side[0].Angle;
        var rootAngle = Side[^1].Angle;
        var tipBulge = Math.Tan(2 * tipAngle / 4);
        var rootBulge = Math.Tan(Math.Max(2 * Math.PI / _teeth - 2 * rootAngle, 0) / 4);
        for (var tooth = 0; tooth < _teeth; tooth++)
        {
            var angle = 2 * Math.PI * tooth / _teeth;
            Point3 Placed(Point2 p) => p.Rotate(angle, Point2.Zero).ToPoint3(0);
            for (var i = Side.Count - 1; i > 0; i--) vertices.Add(new Vertex(Placed(new Point2(Side[i].X, -Side[i].Y))));
            vertices.Add(new Vertex(Placed(new Point2(Side[0].X, -Side[0].Y)), tipBulge));
            for (var i = 0; i < Side.Count - 1; i++) vertices.Add(new Vertex(Placed(Side[i])));
            vertices.Add(new Vertex(Placed(Side[^1]), rootBulge));
        }
        return new Path2(vertices, true);
    }
}
