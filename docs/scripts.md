# Scripts

BamCam has two kinds of C# scripts:

- [Script objects](#script-objects) are part of a drawing and produce its outlines.
- [Scripts of the Scripts menu](#scripts-of-the-scripts-menu) are files on your computer that you run like a command;
  they can read and change the whole drawing.

# Script objects

A script object is a drawing object whose outlines come from a small C# script stored in the drawing. Change the
script and the shape follows; move, rotate or scale the object like any other.

- **Draw ▸ Script Object** inserts one with an example script and opens the editor.
- **Edit ▸ Edit Script** edits the selected one: **Check** (`F7`) lists all compile errors without running anything,
  and clicking an error goes to its line; **Run** (`Ctrl+Enter`) shows the result in the drawing at once;
  **OK** keeps the script (one undoable step), **Cancel** restores the old one.
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

# Scripts of the Scripts menu

A script of the Scripts menu is a `.csx` text file. Every such file in the `scripts` folder of the system folder
appears in the menu, and a subfolder becomes a submenu; a click runs the script. On Windows the folder is
`%APPDATA%\BamCam\scripts`, and **Scripts ▸ Open Script Folder** opens it. BamCam fills it with a few examples the
first time.

- **Scripts ▸ New Script** and **Scripts ▸ Edit Script** open the editor beside the drawing. It has **New**, **Save**
  (`Ctrl+S`), **Save As**, **Check** (`F7`) and **Run** (`Ctrl+Enter`), which checks first and runs only without errors.
- **Scripts ▸ Add Script File** copies a file into the folder, as does dropping a `.csx` file on the BamCam window, or
  copying it there by hand. This does not run it.
- **Check** finds what the compiler finds, such as wrong names, types or brackets. Errors that only show while the
  script runs, such as a division by zero, appear in the message window when it runs.

## Writing menu scripts

Everything of script objects works: top-level C#, the helpers `Circle`, `Rect`, `Line`, `Arc`, `Polyline` and `P`, and
`Add(...)`. The outlines added become polylines on the active layer when the script ends. In addition:

| Member | Result |
|---|---|
| `Document` | the open drawing: layers, parts, operations; change it inside `Change` only |
| `Selection` | the selected drawing objects |
| `Change("name", () => ...)` | changes the drawing |
| `AddEntities("name", objects)` | adds objects to the active layer and selects them |
| `Select(objects)` | replaces the selection |
| `Log("text")` | writes a line to the message window |
| `await AskNumber("title", "label", 10)` | asks for a number; null when cancelled |
| `await AskText("title", "label", "default")` | asks for a text; null when cancelled |
| `Cancel` | a `CancellationToken` set when the user stops the script |

`BamCam.Core.Cam` is imported as well, for parts and operations. All changes of one run are one undo step, unless you
do something else in the drawing in between.

Rectangle around the selected objects, with a margin:

```cs
var selected = Selection;
if (selected.Count == 0)
{
    Log("Select drawing objects first.");
    return;
}
var margin = await AskNumber("Rectangle", "Margin:", 5);
if (margin is not { } m) return;
var box = selected.Select(e => e.Bounds()).Aggregate((a, b) => a.Union(b));
Add(Rect(box.Min.X - m, box.Min.Y - m, box.Width + 2 * m, box.Height + 2 * m));
```

Disable all operations of the active part:

```cs
Change("Disable operations", () =>
{
    foreach (var op in Document.ActivePart.Operations) op.Enabled = false;
});
```

## Long scripts

Menu scripts have no time limit and run in the background, so BamCam stays usable; avoid changing the drawing yourself
while one runs. **Scripts ▸ Stop Running Script** stops it: from then on it cannot touch the drawing. .NET cannot end a
running loop from outside, though. A loop should therefore call `Cancel.ThrowIfCancellationRequested()`; one that
does not keeps a processor core busy until BamCam is closed. One script runs at a time.

## Safety

A script can do anything a program can do on your computer, such as reading, sending or deleting files. There is no
sandbox. Before a file runs for the first time, BamCam therefore shows its whole text with a warning. The approval
belongs to exactly this text: when the file changes, BamCam asks again. Scripts you save in the editor count as
yours and are not asked about. Importing a configuration brings the scripts along, but not their approvals.
