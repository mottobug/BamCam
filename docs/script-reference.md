# Script reference

The classes a script of the Scripts menu works with. For how to create and run scripts, see [scripts.md](scripts.md).

This page covers what scripts need most, not every member. `BamCam.Core.Model`, `BamCam.Core.Geometry`,
`BamCam.Core.Cam`, `BamCam.Core.Cad`, `System`, `System.Linq` and `System.Collections.Generic` are imported in menu
scripts. Script objects only have the helpers of [`ScriptContext`](#scriptcontext) and the first two namespaces with the
`System` ones; they cannot reach the drawing.

**Units:** lengths are in the units of the drawing (`Document.Units`). Angles are in degrees in the script helpers and
drawing objects, but in **radians** in `Matrix4`, `Point2` and `Arc2`. `Angles.ToRadians` and `Angles.ToDegrees` convert.

**The stable interface** for scripts and plugins is `CommandScriptContext` and `ICommandContext`. The other classes
are BamCam's own model: they may change between versions, so check your scripts after an update.

## Rules

- Change the drawing inside `Change(...)` only. The script runs on a background thread, and `Change` carries the work
  over to the user interface, so that the drawing is not changed while it is being drawn.
- Use `Set(...)` for parameters of an operation, not `.Value = ...`. Values in the Default state are replaced from the
  style and the tool when toolpaths are generated. See [`CamParam`](#camparam).
- After a change to operations, toolpaths are out of date until the user generates them again.

## Examples

Message for each layer:

```cs
foreach (var layer in Document.Layers)
    Log($"{layer.Name}: {layer.EntityCount} objects, {(layer.Visible ? "visible" : "hidden")}");
```

Drilling operation for all circles of the drawing:

```cs
var circles = Document.AllEntities().OfType<CircleEntity>().ToList();
if (circles.Count == 0)
{
    Log("No circles in the drawing.");
    return;
}
Change("Drill circles", () =>
{
    var drill = new DrillOp { Name = "Drill circles" };
    drill.PrimitiveIds.AddRange(circles.Select(c => c.Id));
    drill.TargetDepth.Set(-5);
    drill.ToolNumber.Set(3);
    Document.ActivePart.Operations.Add(drill);
});
```

Move the selected objects by 10 in X and rotate them by 45 degrees around the origin:

```cs
var selected = Selection;
var move = Matrix4.Translation(10, 0) * Matrix4.RotationZ(Angles.ToRadians(45));
Change("Move selection", () => CadOps.Transform(selected, move));
```

Outline at a distance of 2 around the selected objects:

```cs
AddEntities("Offset", CadOps.Offset(Selection, 2));
```

Circles on a new layer:

```cs
Change("Holes", () =>
{
    var layer = new Layer(Document.UniqueLayerName("Holes"));
    Document.Layers.Add(layer);
    for (var i = 0; i < 5; i++)
        Document.AddEntity(new CircleEntity { Center = new Point3(i * 20, 0, 0), Diameter = 6 }, layer);
});
```

Feed rate of all profiles, and the diameter each one uses:

```cs
var profiles = Document.Parts.SelectMany(p => p.Operations).OfType<ProfileOp>().ToList();
Change("Feed rate", () =>
{
    foreach (var op in profiles) op.CutFeedrate.Set(600);
});
foreach (var op in profiles) Log($"{op.Name}: tool {op.EffectiveToolNumber(Document)}, diameter {op.EffectiveToolDiameter(Document)}");
```

Length of the outlines of the selected objects:

```cs
var length = Selection.SelectMany(e => e.ToPaths()).Sum(p => p.Length);
Log($"Length: {length:0.###}");
```

## The script

### `CommandScriptContext`

Members a menu script can use directly, without a name in front. It has those of `ScriptContext` as well.

| Member | Meaning |
|---|---|
| `Document` | the open drawing |
| `Selection` | the selected drawing objects at this moment |
| `Change(name, action)` | runs a change of the drawing; all changes of one run are one undo step |
| `AddEntities(name, objects)` | adds objects to the active layer and selects them |
| `Select(objects)` | replaces the selection |
| `Log(text)` | writes a line to the message window |
| `AskNumber(title, label, value)` | asks for a number with `await`; null when cancelled |
| `AskText(title, label, value)` | asks for a text with `await`; null when cancelled |
| `Cancel` | `CancellationToken`, set when the user stops the script |

### `ScriptContext`

| Member | Meaning |
|---|---|
| `Add(path)` | adds an outline; a menu script's outlines become polylines when it ends |
| `Paths` | the outlines added so far |
| `P(x, y)` | a point |
| `Circle(x, y, r)` | circle around (x, y) |
| `Rect(x, y, w, h)` | rectangle with the lower left corner at (x, y) |
| `Line(x1, y1, x2, y2)` | open line |
| `Arc(x, y, r, start, sweep)` | arc around (x, y); degrees, positive sweep counter-clockwise |
| `Polyline(closed, x1, y1, x2, y2, ...)` | polyline through the points |

## The drawing

### `CadDocument`

| Member | Meaning |
|---|---|
| `Name`, `FilePath` | name of the drawing and its file (null until saved) |
| `Units` | `DrawingUnits`: `Millimeters`, `Inches` and others |
| `Layers` | the layers; add to it with `Layers.Add(new Layer(name))` |
| `Parts` | the parts with their operations |
| `Machining` | the machining options, see [`MachiningOptions`](#machiningoptions) |
| `ActiveLayer`, `ActivePart` | the active layer and part |
| `AllEntities()` | all drawing objects of all layers |
| `FindEntity(id)` | object by its ID, or null |
| `AddEntity(entity, layer)` | adds an object and gives it an ID; without a layer to the active one |
| `RemoveEntity(entity)` | removes an object |
| `MoveEntity(entity, layer)` | moves an object to another layer |
| `Bounds(visibleOnly)` | extents of the drawing |
| `UniqueLayerName(prefix)`, `UniquePartName(prefix)` | a name not in use yet |

### `Layer`

| Member | Meaning |
|---|---|
| `Name`, `Color`, `Visible`, `Locked`, `PenWidth`, `Tag` | its properties; `Color` is an `RgbColor` |
| `Entities` | its objects, read only: use `Document.AddEntity` and `RemoveEntity` |
| `EntityCount` | number of objects |

### `Entity`

Base class of all drawing objects.

| Member | Meaning |
|---|---|
| `Id`, `Tag` | ID (shown in the tree) and free text |
| `Layer` | the layer it is on |
| `TypeName` | "Polyline", "Circle" and so on |
| `Transform` | transform on top of the stored geometry, as a `Matrix4` |
| `ToPaths()` | the geometry as `Path2`, with the transform applied |
| `Bounds()` | extents as a `BoundingBox` |
| `ApplyTransform(matrix)` | transforms the object |
| `CloneEntity()` | a copy, not in the drawing yet |

### `PolylineEntity`

| Member | Meaning |
|---|---|
| `Closed` | whether the last point connects to the first |
| `Points` | the points as `Vertex`, with the bulge of each segment |
| `LocalPath` | the same as a `Path2`, without the transform |

`new PolylineEntity(path)` makes one from a `Path2`.

### `CircleEntity`

| Member | Meaning |
|---|---|
| `Center` | `Point3` |
| `Diameter`, `Radius` | `Radius` is read only |

### `ArcEntity`

| Member | Meaning |
|---|---|
| `Center`, `Radius` | center point and radius |
| `StartAngle`, `Sweep` | degrees; positive sweep counter-clockwise |

### `RectEntity`

| Member | Meaning |
|---|---|
| `LowerLeft`, `Width`, `Height`, `CornerRadius` | its properties |

### `LineEntity`

| Member | Meaning |
|---|---|
| `Points` | the points as `Point3` |

### `PointListEntity`

| Member | Meaning |
|---|---|
| `Points` | the points as `Point3`, for example drill positions |
| `WorldPoints()` | the same with the transform applied |

### `TextEntity`

| Member | Meaning |
|---|---|
| `Text`, `Position`, `Height`, `Font`, `Bold`, `Italic` | its properties |
| `HorizontalAlignment`, `VerticalAlignment` | `TextAlignment`, `TextVerticalAlignment` |
| `InvalidateOutlines()` | call after a change of the text, so that the outlines follow |

### `SplineEntity`

| Member | Meaning |
|---|---|
| `Degree`, `Closed`, `ControlPoints`, `Weights`, `Knots` | the curve |
| `SetControlPoints(points)` | replaces the control points |

### `RegionEntity`

| Member | Meaning |
|---|---|
| `Outer`, `Holes` | outline and holes as `PolylineEntity` |

### `SurfaceEntity`

| Member | Meaning |
|---|---|
| `Vertices`, `Faces` | triangle mesh: points, and three point indices per triangle |

Undo steps share these lists. To change a mesh, add a new `SurfaceEntity` instead of editing the lists of an existing
one. Moving, rotating or scaling with `ApplyTransform` is fine, as it does not touch them.

### `ScriptEntity`

| Member | Meaning |
|---|---|
| `Script`, `Status` | the C# script of a script object and why it gives no outlines |
| `Invalidate()` | runs it again at the next use |

### `CadOps`

Operations on drawing objects. They return new objects, which are not in the drawing yet, except where noted.

| Member | Meaning |
|---|---|
| `ToPolylines(entity)` | as polylines |
| `Explode(entity)` | into its parts |
| `Join(entities, tolerance)` | connects open shapes |
| `Offset(entities, distance, openOutline)` | parallel outlines |
| `Union(entities)`, `Subtract(entities)`, `Intersect(entities)` | boolean operations on closed shapes |
| `BreakAtIntersections(entities)` | splits shapes where they cross |
| `ToRegions(entities)` | regions from closed shapes |
| `Divide(entities, count)` | a point list with evenly spaced points |
| `Reverse(polyline)`, `Clean(polyline, tolerance)` | change the polyline itself |
| `Transform(entities, matrix)` | changes the objects themselves |
| `Bounds(entities)` | extents of the objects |

## Geometry

### `Path2`

Open or closed outline of lines and arcs.

| Member | Meaning |
|---|---|
| `Vertices`, `Closed`, `Count` | its points and whether it is closed |
| `FromPoints(points, closed, z)` | makes one from `Point2` points |
| `Segments()`, `SegmentCount` | its segments, each a line or an arc |
| `StartPoint`, `EndPoint`, `Length` | start, end and length |
| `SignedArea()`, `IsClockwise` | area, positive when counter-clockwise |
| `Reversed()`, `Clone()`, `WithZ(z)`, `Transformed(matrix)` | a changed copy |
| `Flatten(tolerance)` | points with the arcs turned into short lines |
| `Bounds()`, `Contains(point)`, `DistanceTo(point)` | extents and position of a point |

### `Vertex`

| Member | Meaning |
|---|---|
| `Point`, `XY` | the point |
| `Bulge` | arc of the segment to the next point: 0 is a line, 1 a half circle, negative clockwise |

### `Segment`

| Member | Meaning |
|---|---|
| `Start`, `End`, `Bulge`, `Length` | its properties |
| `IsArc`, `ToArc()` | whether it is an arc, and the arc as an `Arc2` |

### `Point2`

| Member | Meaning |
|---|---|
| `X`, `Y`, `Length`, `Angle` | `Angle` in radians |
| `Normalized()`, `Perp` | length 1; rotated by +90 degrees |
| `Distance(a, b)`, `Lerp(a, b, t)`, `Dot(a, b)`, `Cross(a, b)` | calculations with two points |
| `FromAngle(angle, radius)`, `Rotate(angle, center)` | radians |
| `ToPoint3(z)` | with a height |

`+`, `-`, `*` and `/` work as usual.

### `Point3`

| Member | Meaning |
|---|---|
| `X`, `Y`, `Z`, `XY`, `Length` | `XY` is the `Point2` |
| `Distance(a, b)`, `Dot(a, b)`, `Cross(a, b)`, `Normalized()` | calculations |

### `BoundingBox`

| Member | Meaning |
|---|---|
| `Min`, `Max`, `Center` | corners and center as `Point3` |
| `Width`, `Height`, `Depth`, `IsEmpty` | size in X, Y and Z |
| `Union(box)`, `Inflate(distance)`, `Include(point)` | a larger box |
| `Contains(point)`, `IntersectsXY(box)` | tests in the XY plane |

### `Arc2`

| Member | Meaning |
|---|---|
| `Center`, `Radius`, `StartAngle`, `Sweep`, `EndAngle` | angles in radians |
| `Start`, `End`, `Length`, `IsClockwise` | its properties |
| `PointAt(t)` | point at `t` from 0 (start) to 1 (end) |
| `Flatten(tolerance)` | points along the arc |
| `FromThreePoints(a, b, c)` | arc through three points, or null |

### `Matrix4`

Combine with `*`: `a * b` applies `a` first.

| Member | Meaning |
|---|---|
| `Identity`, `IsIdentity` | no change |
| `Translation(dx, dy, dz)` | moves |
| `Scale(sx, sy, sz)` | scales around the origin; `Scale(s, center)` around a point |
| `RotationZ(angle)` | rotates, in radians; `RotationZ(angle, center)` around a point |
| `RotationX(angle)`, `RotationY(angle)` | rotates around the other axes |
| `Mirror(a, b)` | mirrors at the line through two points |
| `Transform(point)` | the transformed point |

### `Polygons`

Operations on `Path2` outlines, without drawing objects.

| Member | Meaning |
|---|---|
| `Offset(paths, delta)` | parallel outlines of closed shapes, outward when positive |
| `OffsetOpen(path, delta)` | outline around an open line |
| `Union(a, b)`, `Difference(a, b)`, `Intersection(a, b)`, `Xor(a, b)` | boolean operations |

## Machining

### `Part`

| Member | Meaning |
|---|---|
| `Name`, `Enabled`, `Tag`, `OutFile`, `Style`, `StyleLibrary` | its properties |
| `ToolLibrary`, `ToolNumber`, `ToolDiameter`, `ToolProfile` | tool for operations without their own |
| `Stock`, `MachiningOrigin`, `Nesting` | stock, origin and nesting |
| `Operations` | its operations; add a new one with `Operations.Add(op)` |

### `MachiningOptions`

| Member | Meaning |
|---|---|
| `ToolLibrary`, `ToolNumber`, `ToolDiameter`, `ToolProfile` | tool for parts without their own |
| `PostProcessor`, `PostProcessorMacros`, `OutFile` | output |
| `CustomFileHeader`, `CustomFileFooter` | text at the start and end of the file |
| `Stock`, `MachiningOrigin`, `FastPlungeHeight`, `Style`, `StyleLibrary` | defaults of the drawing |

### `Stock`

| Member | Meaning |
|---|---|
| `PMin`, `PMax`, `Size`, `IsEmpty` | corners and size as `Point3` |
| `SurfaceZ`, `Material` | top of the stock and material name |

### `CamParam`

Each parameter of an operation is a `CamParam<T>`.

| Member | Meaning |
|---|---|
| `Value` | the value; to read it |
| `Set(value)` | sets the value and the state `Value`, so that it stays |
| `State` | `ParamState`: `Default` (from the style or tool), `Value` (set by you) or `Auto` (computed) |
| `IsDefault` | whether it is in the state `Default` |

### `MachineOp`

Base class of all operations. `new ProfileOp { Name = "..." }` makes one; add it to `Part.Operations` inside `Change`.

| Member | Meaning |
|---|---|
| `Name`, `Enabled`, `Style`, `Tag` | its properties |
| `PrimitiveIds` | IDs of the drawing objects it machines |
| `DisplayType` | "Profile", "Pocket" and so on |
| `ToolNumber`, `ToolDiameter`, `ToolProfile` | its tool |
| `EffectiveToolNumber(document)`, `EffectiveToolDiameter(document)` | what is used, with part, machining options and library |
| `PlungeFeedrate`, `CutFeedrate`, `SpindleSpeed`, `SpindleDirection` | feed rates and spindle |
| `StockSurface`, `ClearancePlane`, `RoughingClearance` | heights and allowance |
| `RoughingFinishing`, `MaxCrossoverDistance`, `StartPoint` | further options |
| `CustomMOPHeader`, `CustomMOPFooter` | G-code before and after the operation |
| `FindParameter(name)`, `Parameters()` | parameters by name, as `ICamParam` |
| `ResetToDefaults()` | puts all parameters back to `Default` |
| `CloneOp()` | a copy |

### `DepthOp`

Base of the operations with depth passes.

| Member | Meaning |
|---|---|
| `TargetDepth`, `DepthIncrement`, `FinalDepthIncrement` | final depth and depth per pass |

### `AreaOp`

Base of `ProfileOp`, `PocketOp` and `Profile3DOp`.

| Member | Meaning |
|---|---|
| `StepOver` | stepover as a share of the tool diameter |
| `MillingDirection`, `CutOrdering`, `StepoverFeedrate` | `Conventional`, `Climb` or `Mixed`, and others |
| `LeadInMove`, `LeadOutMove`, `CollisionDetection` | lead moves and collisions |

### `ProfileOp`

| Member | Meaning |
|---|---|
| `InsideOutside` | `Inside` or `Outside` |
| `CutWidth`, `CornerOvercut` | width of the cut and overcut in corners |
| `HoldingTabs`, `Tabs`, `SideProfile` | holding tabs and side profile |

### `PocketOp`

| Member | Meaning |
|---|---|
| `RegionFillStyle` | `HorizontalHatch`, `VerticalHatch`, `InsideOutsideOffsets`, `OutsideOffsets` or `InsideOffsets` |
| `FinishStepover`, `FinishStepoverAtTargetDepth` | finishing stepover before the wall pass |

### `DrillOp`

| Member | Meaning |
|---|---|
| `DrillingMethod`, `HoleDiameter` | method and hole diameter |
| `PeckDistance`, `RetractHeight`, `Dwell` | pecking |
| `SpiralFlatBase`, `DrillLeadOut`, `LeadOutLength`, `CustomScript` | further options |

### `VEngraveOp`

| Member | Meaning |
|---|---|
| `VAngle`, `TipDiameter` | the tool |
| `ClearFlatAreas`, `FlatStepover`, `Resolution` | its options |

### `PlasmaOp`

| Member | Meaning |
|---|---|
| `CutData`, `KerfWidth`, `KerfSide`, `CutDirection` | cut data and kerf |
| `HeightControl`, `PierceHeight`, `PierceDelay`, `CutHeight`, `UseThc` | torch height |
| `ProbeStartHeight`, `ProbeDepth`, `ProbeFeedrate`, `SwitchOffset` | height probing |
| `LeadIn`, `LeadInLength`, `LeadOut`, `LeadOutLength`, `Overcut` | lead moves |
| `SmallHoleSize`, `SmallHoleFeedrate` | small holes |

### `DragKnifeOp`

| Member | Meaning |
|---|---|
| `KnifeOffset`, `SwivelAngle`, `Overcut` | the knife |

### Further operations

`EngraveOp` has only the parameters of `DepthOp`. `Profile3DOp`, `LatheOp`, `LatheFacingOp`, `LatheGrooveOp`,
`LatheThreadOp`, `LatheDrillOp` and `NcFileOp` have many special parameters. Their names are those of the Properties
window without spaces, and `FindParameter` and `Parameters()` list them.

### `ToolDefinition`

An entry of a tool library, from `CamLibraries.FindTool`.

| Member | Meaning |
|---|---|
| `Index`, `Name`, `Diameter`, `ToolProfile`, `Flutes`, `Length`, `VeeAngle`, `Comment` | its properties |

### `CamLibraries`

| Member | Meaning |
|---|---|
| `FindTool(document, part, number)` | the tool of a number, or null, searched as for operations |
| `ToolLibraries`, `StyleLibraries` | all libraries |
| `PostProcessors`, `DefaultPostProcessor` | names of the post processors |

### `ToolpathGenerator`

| Member | Meaning |
|---|---|
| `Generate(document, op)` | computes the toolpath of an operation and returns it as a `Toolpath`, without showing it |

Machining ▸ Generate Toolpaths remains the way to show toolpaths and to use them for G-code.

### `Toolpath`

| Member | Meaning |
|---|---|
| `Moves` | the moves |
| `Warnings` | warnings of the calculation |
