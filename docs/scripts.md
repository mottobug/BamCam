# Script objects

A script object is a drawing object whose outlines come from a small C# script stored in the drawing. Change the
script and the shape follows; move, rotate or scale the object like any other.

- **Draw ▸ Script Object** inserts one with an example script and opens the editor.
- **Edit ▸ Edit Script** edits the selected one: **Run** (`Ctrl+Enter`) shows the result in the drawing at once and
  jumps to the line of a compile error; **OK** keeps the script (one undoable step), **Cancel** restores the old one.
- The *Script Status* property shows "OK" or why there is no geometry.

## Writing scripts

The script is plain C# (top-level statements: no class or `Main` needed). It adds outlines with `Add(...)`; lengths
are in drawing units, angles in degrees.

| Helper | Result |
|---|---|
| `Circle(x, y, r)` | circle around (x, y) |
| `Rect(x, y, w, h)` | rectangle with the lower left corner at (x, y) |
| `Line(x1, y1, x2, y2)` | open line |
| `Arc(x, y, r, start, sweep)` | arc around (x, y), positive sweep counter-clockwise |
| `Polyline(closed, x1, y1, x2, y2, ...)` | polyline through the points |
| `P(x, y)` | a point (`Point2`) |
| `Add(path)`, `Add(paths)` | adds one or several outlines |

`System`, `System.Linq`, `System.Collections.Generic`, `BamCam.Core.Geometry` and `BamCam.Core.Model` are imported,
so `Math`, LINQ, `Path2`, `Point2`, `Vertex` and `Arc2` can be used directly. `Path2.FromPoints(points, closed)`
builds a polyline from a list of points; a `Vertex` with a bulge makes arc segments.

## Examples

Hole circle on a flange:

```csharp
var holes = 8;
Add(Circle(0, 0, 40));
for (var i = 0; i < holes; i++)
{
    var a = i * 2 * Math.PI / holes;
    Add(Circle(30 * Math.Cos(a), 30 * Math.Sin(a), 3));
}
```

Slot (two half circles and two lines as one closed outline):

```csharp
double length = 40, width = 8, r = width / 2;
Add(new Path2([
    new Vertex(new Point3(0, -r, 0)), new Vertex(new Point3(length, -r, 0), 1),
    new Vertex(new Point3(length, r, 0)), new Vertex(new Point3(0, r, 0), 1),
], closed: true));
```

Grid of holes:

```csharp
for (var x = 0; x < 5; x++)
for (var y = 0; y < 3; y++)
    Add(Circle(x * 15, y * 15, 2.5));
```

Wave line:

```csharp
var points = Enumerable.Range(0, 101).Select(i => P(i, 5 * Math.Sin(i * Math.PI / 25)));
Add(Path2.FromPoints(points, closed: false));
```

## Safety

A script can do anything a program can do on your computer. BamCam therefore runs a script only after you allowed
it: scripts you write or edit are allowed at once; when you open a drawing from someone else that contains scripts,
BamCam asks once. Until then (or if you say no) it shows the outlines that were saved with the drawing. BamCam stops
waiting for a script after 5 seconds.

## Compatibility

Drawings store the computed outlines as plain polylines as well, so CamBam and older BamCam versions show and
machine them. CamBam's own scripts (Python, using CamBam's programming interface) do not run in BamCam.
