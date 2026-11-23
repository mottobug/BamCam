# BamCam user guide

This guide walks through BamCam from the first start to G-code. BamCam works like CamBam: you draw (or import)
geometry, add machining operations to it, generate toolpaths and let a post processor write the G-code.

## 1. Install and first start

Download the package for your system from the [releases](https://github.com/mottobug/BamCam/releases):

- **Windows** (10 version 1607 or later, or 11; 64 bit; not Windows 7 or 8): `BamCam-x.y.z-win-x64-setup.exe`
  (installer) or the `.zip` (unpack and run `BamCam.exe`).
- **macOS:** the `.zip` for Apple Silicon (`osx-arm64`) or Intel (`osx-x64`). The app is not notarised: open it
  once with right click ▸ Open, or allow it in System Settings ▸ Privacy & Security.
- **Linux:** the `.tar.gz` for x64 or arm64; unpack and run `BamCam`.

At the first start BamCam asks whether it may check for new versions once a day (only the version list is
requested from GitHub). The interface follows the system language (English or German); Tools ▸ Options changes it.
To change single texts, choose Tools ▸ Edit Language File: search for the text, double-click its cell in the
column *Own text*, type your wording and save. Own texts apply at once and stay after updates; an empty cell
uses the built-in text again.

**Bringing your settings along:** Tools ▸ Import from CamBam copies the CAM styles, tool libraries and post processors
of a CamBam data folder (also from another computer or a copied folder) into BamCam, where they can be edited. Files
that exist already are kept, and so are post processors named like a built-in one. Tools ▸ Export Configuration saves settings, libraries, post processors, cut data, translations and
plugins in one `.zip` file, for a backup or a new computer; drawings are not part of it. Tools ▸ Import Configuration
restores such a file: files with the same name are overwritten, and the previous state is saved first in the folder
`backups` of the system folder. Permissions do not come along: BamCam asks again before it runs scripts, post-build
commands and plugins.

## 2. The window

| Area | What it is for |
|---|---|
| Menu and toolbar | All commands; the toolbar has the common ones (drawing tools, snapping, align, operations, generate, G-code, the sketch mode). |
| **Drawing** tab (left) | Layers with their objects, parts with their machining operations. Right-click for more. Several operations or parts selected with Ctrl or Shift are enabled or disabled together: Space, right-click ▸ Disable, or *Enabled* in the properties. Drag objects onto another layer, and operations onto another operation (before or after it) or a part to reorder or move them. Double-click an object to edit it as in the drawing: a sketch in the sketch mode, a text in its dialog, a script object in the script editor, other objects by their points. |
| **System** tab (left) | Configuration, post processors, CAM styles and tool libraries. Changes to a post processor are saved at once to its `.cbpp` file in the `post` folder of the system folder; a built-in post or one of a CamBam installation is saved there as a copy of the same name, which takes its place from then on (CamBam's file stays unchanged). Right-click *CAM Styles* or *Tools* for a new library; a library of a CamBam installation is read-only until you copy it to BamCam (right-click it). In own style libraries right-click for new, duplicate or delete style; double-click a style to edit its name, parent style and values (D = inherited). A style holds values for every kind of operation; *Show values for* only chooses which are shown, and the next style opens with the same kind. Each style opens in one window. |
| Properties (left, below) | Settings of whatever is selected: object, operation, part, layer, drawing. `?` shows the help text. |
| Drawing view | Left button selects (click, window from left to right, crossing window from right to left); Shift + left drag on a selected object moves the whole selection (Esc cancels), as in CamBam; middle button or wheel pans and zooms (without a middle button, as on a notebook: hold Space and drag with the left button, or use the arrow keys); Alt + left drag rotates (Tools ▸ Options ▸ Rotation Mode also allows holding the middle or right button and dragging with the left one, as in CamBam); right button opens the context menu. 3D meshes appear as shaded surfaces; View ▸ Shade Surfaces switches to wireframes. Lines and toolpaths always lie over a surface, even when they run behind it. |
| Messages (bottom) | What BamCam did, warnings and errors. |
| Prompt bar (above the drawing) | What the active command expects next. |

## 3. Drawing

**Tools** (Draw menu, toolbar, keys): line, polyline (`P`), arc (`A`), spline, circle (`C`), rectangle (`R`),
point list (`D`), text (`T`). Left click sets points, Enter or a middle click finishes, Esc cancels.
The active tool is highlighted in the toolbar. While a tool takes points (drawing, move, rotate, picking a point) the cursor
is a cross with a small white square on the snapped point the next click gives: with snapping it moves in grid steps or
jumps to the object point. Selecting has the normal arrow.

**Exact sizes:** after the first point, simply type a value; the label at the cursor shows it:

- polyline / line: `25` = segment length towards the cursor, `25<30` = 25 at 30° (counter-clockwise from +X),
  `<90` = direction only; Enter places the point, so you can draw by keyboard;
- circle and flat spiral: the diameter; rectangle and ellipse: `40x20`;
- hold **Shift** to snap the direction to 15° steps; Backspace corrects the value.

**More shapes** (Draw ▸ More):

- **Ellipse:** two corners of the rectangle round it, like the rectangle. It becomes a closed polyline of arcs
  (within 0.005 mm of the true ellipse), with points at the ends of both axes.
- **Tangent:** select two circles, arcs or single points (a point list with one point), then choose Tangent. The
  lines touching both are shown; the one nearest to the cursor is highlighted, a click draws it. On an arc only the
  lines touching the arc itself are offered.
- **Flat Spiral:** asks for the stepover and the direction, then the centre and the radius are clicked like a
  circle (or the diameter is typed). The spiral is made of half circles from the centre out to the radius, so its
  turns are at most a stepover apart (slightly less so that it ends exactly on the radius), and ends with one full
  circle there, for example to engrave or to clear a round pocket with an engrave operation.
- **Spiral Path / Trochoidal Path:** select the lines, arcs or polylines to follow first. Both ask for the stepover,
  the radius and the direction. The spiral path is a circle going round while it moves along the line by a stepover
  per turn (a looped line). The trochoidal path puts a full circle every stepover along the line (and one at its
  end), joined by short straight lines, a chain of arcs to cut a slot wider than the tool with an engrave operation.

**Point patterns** (Draw ▸ Point List), for drilling or as markers; the point list goes to the active layer:

- *Divide Geometry*: a number of points evenly along the selected shapes, e.g. a bolt circle;
- *Step Around Geometry*: a point every step along each shape from its start (an open shape also gets its end);
- *Fill Geometry*: a grid of points a step apart inside the selected closed shapes, centred on them; holes (shapes
  inside shapes) stay empty. *Offset Fill Geometry* moves every other row by half a step;
- *Centers*: one point in the centre of each object; *Extents*: the four corners and the centre of each object's
  bounding box.

Edit ▸ Transform ▸ **Center (Of Points)** moves the selection so that the average of its points lies on the origin;
*Center (Extents)* uses the middle of its bounding box.

**Snapping:** the toolbar buttons with the red dot switch snap to grid (`Ctrl+G`) and snap to objects. View ▸ Object
Snap Modes chooses the points: end points (square marker), mid points (triangle), circle centres (circle),
quadrants (diamond) and intersections (cross).

**Changing objects** (Edit menu, right-click menu):

- double-click an object (in the drawing or in the Drawing tab) to drag its points; Edit ▸ Polyline ▸ Edit Points for a table;
- move (`Ctrl+M`), rotate (`Ctrl+R`), resize (`Ctrl+E`), mirror, array copies, align (toolbar button with the
  arrow: left / centre / right / top / middle / bottom, to the last or first selected object, the selection, the
  drawing, the origin or the stock);
- **resize** (`Ctrl+E`): per axis X, Y, Z the current size, the new size and the percentage; with *Preserve Aspect
  Ratio* the switched-on axes scale alike. *mm → inch* and *inch → mm* scale by 1 / 25.4 or 25.4 in one click, for
  drawings in the other units. It scales about the centre of the selection or the drawing origin;
- offset, join (`Ctrl+J`), union / subtract / intersection, explode, convert to polyline (`Ctrl+P`) or region;
- **fillet / chamfer** (Edit ▸ Polyline): select a polyline or rectangle, type the radius, click corners;
  Space switches fillet / chamfer / corner overcut, `-` gives an inverted fillet, Esc ends;
- **break at intersections** (Edit menu): select objects that cross; each line, arc, circle, rectangle, polyline or
  spline is split into separate polylines where it crosses another selected object (or itself);
- **intersection points** (Edit menu): a point list with the places where the selected objects cross or touch each
  other (or themselves), to snap to or to drill;
- **polyline tools** (Edit ▸ Polyline), for imported drawings in particular:
  - *Set Start Point*: select one closed polyline, then click where it should start. A point near a corner takes the
    corner, anywhere else on the outline a new point is added.
  - *Break at Points*: select shapes and one or more point lists (for example from Draw ▸ Point List ▸ Divide
    Geometry or Intersection Points); the shapes are split at the points that lie on them. A closed shape with one
    point on it is opened there.
  - *Arc Fit*: asks for a tolerance (default 0.01 mm) and turns runs of short straight segments that lie on a circle
    within it into arcs, as from DXF, SVG or STL slices. Arcs already in the polyline stay. Fewer segments give
    smoother toolpaths with G2 / G3 instead of many short G1 moves.
  - *Remove Overlaps*: removes the places where a polyline runs back over itself (a spike or a "Z" step along a
    line), which some exports produce. The cleaned polylines are copies on the active layer; the originals stay
    unchanged for comparison;
- **trim / extend** (`Ctrl` + `+`): click the boundaries (or just press Enter for all objects), then click a line
  near its end to extend it, Shift+click a piece to trim it;
- **hide** single objects (right-click > Hide): they are no longer drawn, picked, snapped to or selected, but
  operations still cut them. In the drawing tree they are grey with *(hidden)*; select one there and right-click >
  Show, or use View > Show All Objects. Hidden is saved in the drawing (CamBam shows such objects). Whole layers are
  hidden with right-click on the layer in the tree.

**Meshes and fills from outlines** (Draw menu). Select the shapes first; the results go to the active layer and are
selected.

- **Surface ▸ Extrude:** the walls the outlines sweep when moved up (+) or down (−) by a height, as an open mesh
  without top and bottom, in a number of rows of triangles. Open outlines give a single wall.
- **Surface ▸ Extrude Solid:** closed outlines (with their holes, such as letters) as a closed body with walls, top
  and bottom, facing outwards, ready for STL export or as a model for the 3D profile.
- **Surface ▸ Fill:** closed outlines as a flat mesh without thickness.
- **Fill Region:** closed outlines filled with polylines, a margin inside the outlines and a step over apart: Inside
  Offsets (growing from the holes), Outside Offsets (from the outer wall inwards), Inside + Outside Offsets, or
  horizontal or vertical hatch lines, with the outline pass. The same patterns as the pocket's *Region Fill Style*,
  as drawing objects, for example to engrave, plot or laser an area.

**Outlines of 3D models** (Edit ▸ Surface), for example the outline of a mould cavity as the boundary of a pocket
or a profile. Select one or more surfaces (meshes) first; the polylines go to the active layer and are selected.

- **Plane Slice X / Y / Z:** the model cut by planes across the axis, a distance apart from its highest point down,
  as 3D polylines: closed where the cut goes all round, open at the open borders of a mesh. Useful for 2D pockets
  and profiles and for the boundary of a 3D profile, without drawing the model again. A face that lies exactly in a
  plane gives no line there.
- **Silhouette:** the outline of the model seen from above. With a height interval of 0 one outline of the whole
  model, at its top. With an interval, one outline for each level from the top down to the bottom, each at its
  height: what lies above the level is projected down onto it, so overhangs are included (the area a tool from above
  can reach around the model at that height). A level at exactly the height of a flat face already includes it.
- **Edge Detect:** the edges of the mesh as 3D polylines: open borders (such as the rim of an open cavity) and edges
  where two faces meet at more than the angle you give (30° by default; 0 also gives the edges between the
  triangles of curved faces). Corners that lie closer together than a millionth count as one, as in STL files.
- **Project Lines to Surface:** select lines or texts and the surfaces: the lines are laid onto the surfaces from
  above, with a point every *resolution* along them at the height of the highest face below. Where no face is, the
  points get the lowest height of the surfaces. An Engrave operation follows the heights of these 3D lines (its
  depth is added to them), for example to engrave text into a relief.
- **Invert Faces:** turns the normals of the selected meshes round (the order of the corners of each triangle), for
  meshes whose faces point inwards; saved in STL files.

**Text:** Draw ▸ Text uses the installed fonts. Draw ▸ Text on Polyline bends a selected text along a selected
polyline, line, arc or circle: click the start point; `U` reverses the direction, `S` puts the text on the other
side.

**Images** (File ▸ Import Image): *Trace Outlines* turns the dark (or light) areas into polylines or splines,
*As Height Map* turns brightness into a surface for the 3D profile (reliefs). The dialog previews the result while
you set threshold, smoothing and size.

**Script objects** (Draw ▸ Script Object): shapes generated by a small C# script, e.g. a flange with a hole circle;
see [scripts.md](scripts.md).

**Numeric transform** (Edit ▸ Transform ▸ Numeric): scale, rotate about X, Y and Z, and move the selection by numbers, for
example a large 3D model that has nothing to pick with the mouse. The order is scale, rotate about X, then Y, then Z, then
move, around the origin, the centre of the selection or a point you give. A box in the drawing previews the result, the
window stays open for the next step, and it can transform a copy. Each Apply is one undo step. Objects with circles or arcs
are left out with a warning when a transform would take them out of the XY plane or scale them unevenly.

**Centre lines** (Draw ▸ Centre Lines): a horizontal and a vertical centre line for the selected circle or rectangle.
This is a plugin that comes with BamCam; Tools ▸ Plugins switches it off.

**Squircle** (Draw ▸ Squircle): a shape whose values you change in the properties. *Simple* goes from circle over rounded
rectangle to diamond with the exponent n1: 2 gives a circle or ellipse, more a rounded rectangle, 1 to 2 a rounded
diamond, and below 1 a concave diamond. *Complex* gives a wide variety of shapes from m, n1, n2, n3, a and b. The
accuracy is set by the angle or the length of the segments. Also a plugin that comes with BamCam. The idea, formulas and
parameters come from GeoffreyGRoy, author of the
[Squircle Pro plugin](http://www.atelier-des-fougeres.fr/Cambam/Aide/Plugins/SquirclePlugin.html) for CamBam. The
example script **Scripts ▸ Examples ▸ Squircle** draws the same shapes, as a plain polyline; see
[Script or plugin?](scripts.md#script-or-plugin).

**Involute Gear** (Draw ▸ Involute Gear): a spur gear with involute flanks whose values you change in the properties: teeth,
module, pressure angle, profile shift, backlash and bore, and under *Tooth Form* the height of the tooth and the rounding at
its root. The outline is what a rack cutter leaves, including the undercut of gears with few teeth, which a note mentions
with the profile shift that avoids it. Two gears run together at the sum of their pitch radii when they have the same module
and pressure angle and their profile shifts add up to 0. *Dimensions* shows the diameters and the *Largest Tool Diameter*: a
larger end mill leaves material in the gaps or at the root, and profiles and pockets do not warn about that by themselves.
Keep in mind that a milled gear is only as accurate as the machine, and check it before relying on it. Also a plugin that
comes with BamCam.

**Sketches** (Draw ▸ Sketch, double-click a sketch to edit it): lines, rectangles, circles, arcs and points held in place
by constraints and dimensions, so that changing a dimension or a variable reshapes the part. A sketch is one drawing
object; its outlines (lines and arcs that meet are joined, circles closed) are what profiles, pockets and the other
operations machine, and drawings also store them as plain polylines, so CamBam shows them.

- **The sketch mode** shows a bar above the drawing. Draw with *Line* (`L`, a chain: Enter or Esc ends it, clicking an
  existing point closes it), *Rectangle* (`R`), *Circle* (`C`: centre, then a point on it), *Arc* (`A`: start, end,
  then a point on the arc, as Draw ▸ Arc), *Arc (Centre)* (`Shift+A`: centre, start, end, counter-clockwise) and *Point*
  (`P`). A click on an existing point of the sketch joins the new point to it, a click
  on a line or circle puts it on that; lines drawn within 2° of horizontal or vertical get that constraint.
  *Construction* (`X`) makes the selected or the next geometry construction geometry (dashed): it helps with constraints
  but is not part of the outlines. The cross is the sketch origin, fixed at 0, 0.
- **Select** (`S`): click points, lines, circles, constraint symbols or dimensions (Ctrl or Shift adds), or drag a box.
  Dragging a point or a line moves it and everything that depends on it (with grid and object snap: a dragged point goes
  to the snapped position, a line moves by snapped steps); dragging a circle changes its radius; dragging
  a dimension moves its label. `Del` deletes the selection, `Ctrl+Z` / `Ctrl+Y` undo and redo the steps of the sketch.
- **Constraints** for the selection: *Coincident* (`K`; two points on one place, or points on a line, circle or arc),
  *Horizontal* (`H`), *Vertical* (`V`), *Parallel*, *Perpendicular*, *Tangent* (`T`), *Equal* (`E`, lengths or radii),
  *Midpoint* (`M`), *Symmetric* (two points and the line of symmetry) and *Fix* (`F`). A constraint that contradicts
  others or that they already give is not added; the message window says why. A midpoint, or a coincidence with an end of a
  line or arc, replaces a point-on-object it includes.
- **Fillet** (`G`) and **Chamfer** (`N`): select corners (points where two lines meet, or two lines that meet) and give
  the radius or the length cut off each line, a number or a formula. The lines are shortened and joined by a tangent
  arc or a straight chamfer; the radius or chamfer length becomes a dimension you can change later. The old corner
  stays as a construction point on both lines, and what held it (dimensions, a corner on the origin, the length of
  a side) holds it still, so the size of the part does not change.
- **Dimensions** (`D`): one line → its length, two points → their distance, a point and a line → the distance between
  them, a circle → its diameter (*Radius* for the radius), an arc → its radius, two lines → the angle between them (or
  their distance when they are parallel). *Horizontal Dim.* and *Vertical Dim.* measure along X or Y only; their labels
start with `X` or `Y` (e.g. `X 60`), while a direct distance has no mark. To dimension from the origin, select the
cross together with a point or line. The value may be
  a formula of the sketch's **variables** (*Variables...*, one per line: `Width = 60`, `Height = Width / 3 + 5`;
  `+ - * / ^`, parentheses, `sqrt`, `abs`, `min`, `max`, `round`, `sin`, `cos`, `tan` and their inverses in degrees,
  `pi`). Double-click a dimension to change it; an empty value makes it a reference dimension, shown in parentheses,
  which only measures. A dimension that would over-constrain the sketch becomes a reference dimension by itself.
- **Colours:** green = fully determined, blue = can still move, red = constraints that contradict or repeat each other;
  the bar and the property *Constraint Status* say how many degrees of freedom are left.
- **Finish** (Enter, or Esc with nothing selected) keeps the sketch as one undo step; **Cancel** drops the changes of this
  session. Move, rotate and copy a sketch like any other object.

**Scripts menu:** your own C# scripts as `.csx` files, run with a click. They can read and change the whole drawing.
New, edit, check and run them in the script editor, or copy files into the script folder. Before a file runs for the
first time BamCam shows its text; see [scripts.md](scripts.md#scripts-of-the-scripts-menu).

Everything can be undone with `Ctrl+Z`.

## 4. Machining

1. **Select** the objects to machine.
2. **Add an operation** (Machining menu or toolbar): profile, pocket, drill, engrave, 3D profile, drag knife, plasma, or a
   lathe operation (Machining ▸ Lathe).
3. **Set it up** in the properties: tool diameter (or a tool number from the library), depths, feeds, spindle
   speed. Tools ▸ Speeds and Feeds suggests spindle speed and feeds and applies them to the selected operation. The cutting
   speed is in m/min, in inch drawings in ft/min (SFM).
   The ▾ button beside *Tool Number* lists the tools of the library that applies (typing a number works too), and
   *Style*, *Style Library* and *Tool Library* are drop-down lists. A tool number brings the tool's diameter and
   profile; a style brings its values. Where a choice disagrees with values set elsewhere, BamCam asks instead of
   deciding, and stores the answer in the operation: a tool chosen while a diameter or profile is typed in the
   operation (take the tool's or keep the typed ones), a style chosen while the operation has typed values it sets
   otherwise, and a style whose tool number differs from the number of the operation, part or machining options
   (which tool to use). Drawings opened as they are use the operation's own number, else its style's, else the
   part's, else the machining options'. Without any tool library (Default-mm or Default-in, as on a computer without
   CamBam) BamCam offers to create one. The small button beside a parameter (D = Default, V = typed value,
   A = calculated) sets it back to Default.
   **Same settings for another operation:** right-click the operation ▸ *Copy*, then right-click the other one ▸
   *Paste Format* (or select it and press `Ctrl+Shift+V`). It takes all values with their state (default or typed),
   but keeps its name, its objects and, for plasma, its start points of the shapes. Both must be of the same type;
   otherwise the message window says so.
   **Start Point** (Options) of a pocket with several shapes: the pocket nearest to it is cut first, then always the
   nearest next one; its first ring starts at the point nearest to the start point. Without a start point the
   pockets are cut from left to right.
   **Mirrored and moved copies of an operation:** copy and paste it, then set its *Transform* (Options, button [...]):
   scale X/Y/Z and rotate about Z around the drawing origin, then move. *Mirror Y* (scale Y -1) gives the other half
   of a mold. BamCam transforms the drawing objects before it computes the toolpath, so the milling direction stays
   as set. Boundary shapes and the clip area of a 3D profile and the start point move with them; the 3D simulation
   compares the cut with the transformed surface. The properties show the transform in words, e.g. "Mirror Y".
4. **Generate toolpaths** (`Ctrl+T`) and check them in the drawing; View ▸ Toolpath View Filter (`Ctrl+F`) steps
   through them.
5. **Choose the post processor** (Machining ▸ Post Processor, or the list *Post Processor* of the machining options
   in the drawing tree; it is saved in the drawing, so each file can have its own machine. Without a choice the default
   from the options applies) and **produce G-code** (`Ctrl+W`). This posts the whole drawing, as in CamBam; the
   right-click menu of a part or operation posts just that one. With *G-code Follows Selection* (Tools ▸ Options,
   CAM) `Ctrl+W` and the toolbar button post the part or operation selected in the drawing tree instead. The title
   of the save dialog always says what is being posted.

**3D simulation** (Machining ▸ 3D Simulation, `Ctrl+Shift+T`, toolbar button next to *Generate*): a separate
window shows the stock after the milling operations as a shaded 3D model, cut with the real tool shape (end mill,
ball nose, bull nose, V-bit) and diameter. Material left above what an operation is meant to reach is coloured:
yellow to red for rest material (corners the round tool cannot reach, holding tabs, roughing clearance), magenta
where the tool cut below the target. Targets are the area of pockets, the cut band of profiles, drill holes and the
surface of 3D profiles, each at its target depth. It shows what is selected in the drawing tree: one operation, all operations of a part, or (Machining, the drawing) all of them, and follows the tree while it is open; the check boxes on the left change the choice. The buttons below step through the moves one by one (◀ / ▶, or the
arrow keys), jump to the start or end (Home / End) or play them (Space) at the speed chosen next to the buttons (1 to 1000 moves per second or as fast as possible); the counter shows the move and the total,
with the operation and the G-code of the move, and the tool is drawn at its position with the shape it cuts with.
*Cell size* is the edge length of the grid cells of the simulated material, which has one height per cell: smaller is
finer and slower. At most 4000 cells fit along a side; a smaller size is enlarged to fit, and the window says so.
*Tolerance* only decides what is shown as rest material (yellow to red) or as cut too deep (magenta), not the
simulation itself; by default it is half a cell. **Add to Drawing** puts the material as it is at the move shown on a
new layer as a surface (a closed mesh through the centres of the cells, with sides and bottom), for example for a 3D
profile; **Save STL...** writes the same mesh as a binary STL file. Fields with more than a million triangles are
thinned to every second or third cell. Left drag rotates, right drag moves, the wheel zooms; dragged down past the
side view, it looks at the stock from below. The simulation is 3-axis (no undercuts). Plasma cuts through the stock
as wide as its kerf (without stock the sheet is as thick as in the cut data, else 1); the drag knife is not shown.
**Both sides:** 3D profiles with *Back Face* are cut with the part turned over. The simulation turns their moves back
and cuts the stock from below, so front and back operations simulated together show the part as it comes off the
machine, cut free where both sides meet; the tool of a back face move is drawn pointing up. Rest material on the
underside is coloured like on top. The stock has one height for the top and one for the underside per cell.
Example: `examples/3D profile (pebble).cb`, a pebble roughed trochoidally and finished on both sides, with a
waterline rough to compare (off) and the mould of it as a part of its own (off: select the part to simulate it alone).
The pebble is computed by BamCam's tests, so the file has no third-party content.

**Other simulators** (Machining ▸ Open in CutViewer / NC Viewer / CAMotics, or the eye in the toolbar, from the G-Code
Viewer plugin that comes with BamCam): they simulate the G-code the post processor writes, an independent check. CutViewer and NC Viewer run in
the browser; drag the file shown in the file manager into the browser window. CAMotics ([camotics.org](https://camotics.org),
install it first) opens directly with the tools of the operations and the stock; if it is not in its usual place,
BamCam asks once for the program.

Turning operations are simulated in the same window: the stock section is cut move by move by the tool (the nose
radius of turning and facing tools, the grooving blade, the drill, 60° thread grooves) and shown turned about the
spindle axis, with the tool at its position and the same buttons to step and play. It needs a stock in the machining
options. Milling and turning are simulated apart: checking a turning operation unchecks the milling ones and the other
way round; with both selected in the tree, the milling ones are shown. Rest material colours, cell size and the export
buttons belong to milling only.

Operations refer to their objects by ID (*Primitive IDs* in the properties; `[...]` picks them in the drawing).
Parts group operations; each part can have its own stock, origin and nesting. CAM styles provide default values;
values you type override them.

Special operations:

- **Lathe:** draw only the profile on one side of the spindle axis (through the machining origin). Turning
  (external or internal, nose radius compensation computed or by the machine), facing, grooving / parting,
  threading and drilling; the remaining stock is shown in the drawing and, move by move, in the 3D simulation.
  Turning tools in the tool library can have an *Insert Shape* (C80, D55, V35, T60, W80, S90, K55, Round) and the
  *Approach Angle* κr of their holder: the toolpath then keeps the whole insert out of the part, not only its nose. A
  contour falling steeper than the insert allows (about 180° − κr − corner angle, e.g. 52° for a VBMT in a 93° holder)
  is followed at the steepest angle the insert can take; the material left there is reported with its Z range and
  shown in the simulation. Grooving blades can have a *Corner Radius* (0 = square corners); BamCam warns when the
  plunges are further apart than the flat of the blade and would leave ridges. Without an insert shape the tool is
  its nose radius, as before. Use a lathe post processor such as
  *LinuxCNC Turn*.
- **Drag knife** for vinyl and foil: set the blade offset; corners get swivel arcs.
- **Thread milling** (Machining ▸ Thread Milling): select circles (or points with a *Thread Diameter*). Internal or
  external, pitch, right- or left-hand, bottom up or top down, radial passes; the thread depth is the ISO depth unless
  you set one. The helix turns so that it cuts the chosen hand (right-hand with a clockwise spindle: bottom up is climb
  milling in a hole, top down conventional), from the target depth to the stock surface in whole turns. The tool comes
  in on a half circle from the centre of the hole (from outside a stud) and leaves the same way. With *Cutter Length*
  set, a cutter with several teeth turns once per cutter length instead of running one helix over the whole thread.
  The tool diameter is the cutting diameter of the thread mill.
- **Tapping (G84)** (Machining ▸ Tapping): select the holes and set the pitch; the feed is pitch × spindle speed, for
  a tapping head or rigid tapping. *Feed Factor* (percent, default 100) lowers it for a tension-compression tapping
  head, e.g. to 95 %, so the head takes up the difference when the spindle does not follow exactly; the right value
  depends on the machine. The post processor writes its *Tap* cycle; posts without one (LinuxCNC, GRBL)
  leave these holes out with a warning. Thread milling and tapping are BamCam operations: CamBam opens a drawing
  without them. Save an operation per thread as a style to reuse it.
- **Drilling with a custom script**: with *Drilling Method* Custom Script the drill operation writes the text of
  *Custom Script* at every hole instead of a canned cycle, as in CamBam, e.g. `G84 X$x Y$y Z$z R$r F$f P$p` for
  a tapping cycle with your own numbers. `|` starts a new line. Macros: `$x` `$y` position of the hole, `$z` target
  depth, `$h` Z of the drill point, `$r` retract height, `$c` clearance plane, `$s` stock surface, `$f` plunge
  feedrate, `$p` dwell, `$q` peck distance, `$d` hole diameter, `$t` tool diameter, `$n` tool number. After the
  script BamCam moves the tool up to the clearance plane before it goes to the next hole.
- **Trochoidal pocket and profile (matmill)** (Machining ▸ Trochoidal Pocket (matmill) / Trochoidal Profile (matmill);
  the toolbar button with the loops inserts the pocket) for high speed clearing: a port of the CamBam plugin
  [matmill](https://github.com/jkmnt/matmill) by jkmnt (MIT License), which comes with BamCam.
  The pocket opens with a spiral at the widest place and clears the shape with slices along its centre lines; each slice
  takes at most *Step Over* of the tool diameter and at least *Minimum Stepover* of that. With *Milling Direction*
  Mixed the direction changes from slice to slice, so that most chords between them fall away (in a test pocket the
  way was 30 % shorter). *Smooth Chords* joins slices with arcs instead of straight chords (not with Mixed), *Chord
  Feedrate* and *Spiral Feedrate* set their own feed rates, and a *Lead In Move* of type Spiral goes down on a helix.
  Islands stay, there is no final pass along the walls. The profile cuts a slot of *Cut Width* (default twice the tool
  diameter) beside the shapes, on the side of *Inside Outside*; open shapes are cut to the right of their point order
  for Outside. *Clear Inside Corners* cuts it like a pocket between the shape and an offset of it, which reaches into
  inside corners (closed shapes only); *Corner Overcut* is not supported yet. The operations and their parameters have
  matmill's names, so CamBam drawings with matmill operations open in BamCam, and drawings from BamCam open in CamBam
  with matmill installed. Example: `examples_to_test/Trochoidal pocket.cb`. BamCam's own earlier trochoidal pocket
  is gone; drawings with it keep it unchanged in the file, but it is no longer cut.
  **Trochoidal 3D Rough (matmill)** (Machining menu; only in BamCam, neither matmill nor CamBam have it) roughs a 3D
  model like the 3D profile *Waterline Rough*, but clears each level with matmill's slices instead of offset rings:
  a large *Depth Increment* with a small *Step Over* (default 0.2), e.g. for the cavity of a mould. Select the surfaces
  as for a 3D profile; it is one, so boundary method, margin and taper, clip area, back face, *Mold*, *Roughing
  Clearance* and the 3D simulation work as there (the scanline settings are hidden). Level by level from the top, each
  free area is opened with a spiral and cleared to the model; between areas and levels the tool goes up to the
  clearance plane, since a straight link could run into the model. An area too narrow for the tool plus a little (a
  strip along the model) is left out, and the message window counts these: a finishing pass or *Waterline Rough* with
  a smaller tool clears them. It takes longer to calculate than *Waterline Rough* (a few seconds for a large model).
- **V-engrave** (Machining ▸ V-Engrave, toolbar button with the V) for signs and lettering: select text, regions or
  closed polylines, set the *V Angle* of the bit (and its *Tip Diameter* if it has a flat tip) and the *Target
  Depth*. The bit follows the centre lines of each shape and goes exactly as deep as the shape is wide, so the cone
  touches both sides and corners end sharp. Where a shape is wider than the cone at the target depth, the bottom
  is flat: its outline is cut at target depth and, with *Clear Flat Areas*, cleared in passes of *Flat Stepover*
  (switch it off to clear these areas with a pocket and a flat end mill). *Depth Increment* splits deep cuts into
  passes. Example: `examples_to_test/V-Engrave (sign).cb`.
- **Plasma** (Machining ▸ Plasma) for plasma, laser and oxy-fuel cutters: select all outlines and holes of the parts
  in one operation and set the *Kerf Width*. BamCam cuts holes before the outline around them, outlines outside
  and holes inside the line, and enters through the scrap with a lead-in arc. Per cut the torch moves to *Pierce
  Height*, is switched on, waits *Pierce Delay*, moves down to *Cut Height* and cuts. *Height Control*:
  - *Fixed Heights*: the heights are above the stock surface (Z 0 on the material).
  - *Probe*: before every pierce the torch moves down with G38.2 until its switch triggers, Z is set to the
    surface (minus the *Switch Offset* of a floating head) and the heights count from there.

  *Cut Data* picks a row of the plasma cut data tables (System tab ▸ Plasma Cut Data, files in the `plasma`
  folder of the system folder): material, thickness, current, nozzle, kerf width, feedrate, pierce height, pierce
  delay and cut height. The operation takes these values wherever it keeps the default (italic) value, also after
  the table changes; a value typed into the operation wins. A table in other units than the drawing is converted
  (kerf width, feedrate and heights; the pierce delay is in seconds). Without Cut Data (or with Cut Data no table has),
  no toolpath is made until kerf width, feedrate, pierce height, pierce delay and cut height are typed in; the
  message window names the values still missing. The tables start with a few rough values for 30 to 45
  A cutters: replace them with the cut chart of your plasma cutter (right-click an entry: duplicate, delete; a table: new
  entry, duplicate, rename, delete table; Plasma Cut Data: new table). Each table is one file; the two starting tables
  are written again when missing, so they cannot be renamed or deleted (duplicate them instead). Operations remember
  the entry by its name only: give entries names that are unique across tables, otherwise the first one found is used
  (tables in the drawing's units first).
  The toolbar button with the torch inserts a plasma operation.

  **Where each cut starts:** by default in the middle of the longest edge of the shape (or, with a *Start Point* of the
  operation, at the corner nearest to it). To choose it per shape, right-click the operation ▸ *Set Start Points of the
  Shapes* and click each outline where its cut (with the lead-in) should start, anywhere on the outline; the toolpath is
  generated again after each click. Del over an outline gives it the automatic start again, Esc ends. The points are
  saved with the drawing (CamBam ignores them); a point no longer on its outline, after the object was changed, is
  ignored. *Clear Start Points of the Shapes* removes them all.

  In a hole the lead-in and lead-out must stay inside it, or they would cut into the part: where one does not fit, it
  is shortened (halved until it fits) and the message window names the object; if none fits, the hole is cut without
  one. A start point at a straight, roomy place of the hole avoids that.

  *THC* sends the THC on / off codes of the post processor around each cut (left out in holes up to *Small Hole
  Size*, which are also cut slower). Use the post processor *GRBL Plasma* (torch `M3 S1000` / `M5`, pierce delay
  `G4 P…`, probing `G38.2` and `G92`) or set the *Plasma* templates of your own post: Torch On, Torch Off, Pierce
  Delay, Torch Probe, Torch Probe Set Z, THC on / off. Pierce height and cut height are Z moves of the toolpath; a
  post that needs the values elsewhere can use `{$mop.pierceheight}`, `{$mop.piercedelay}` (seconds) and
  `{$mop.cutheight}` (all templates and the order: [spec-formats.md](spec-formats.md#bamcam-plasma-templates)). On GRBL switch laser mode off (`$32=0`), otherwise `M3`
  only switches the torch while the machine moves. Try a new setup in the air first (torch off or without
  material). Example: `examples_to_test/Plasma (bracket).cb`.

  The post processor *RedGear Plasma* is for the RedGear hand controller Plasma_8060_MPG with the Z-Probe extension
  (firmware 1.05 or later, `$6=1`): it probes with `G38.3` and `G92` as in the instructions of the extension, and
  after writing the G-code it uploads the file to the SD card of the controller with `curl`. Connect the computer to
  the WLAN `RG-CNC` first; if the controller cannot be reached, the message window says so and the file stays on the
  computer. Load the file at the controller under *Load File*; it takes up to 32,000 lines, BamCam warns about longer
  programs (*Maximum Lines* of the post processor). `G38.3` raises no alarm
  without contact, so set *Probe Depth* large enough to reach the material (the instructions use 100 mm at
  *Probe Feedrate* 150).

## 5. Printing and export

File ▸ Print writes a true-to-scale PDF and opens it in your PDF viewer (choose "actual size" there for 1:1);
large drawings at 1:1 are split over several pages. File ▸ Export writes DXF (R2000 or R12) or PDF.

## 6. Plugins and settings

Plugins from other vendors go into the `plugins` folder of the system folder (`%APPDATA%\BamCam\plugins`,
`~/.config/BamCam/plugins`); BamCam asks once before loading one, Tools ▸ Plugins lists them, and its
*Install from ZIP...* puts a plugin ZIP (e.g. a community plugin from a release) into the plugin folder. Tools ▸ Options holds
display, drawing, files, post processor and update settings.

## 7. Keyboard shortcuts

| Key | Command | Key | Command |
|---|---|---|---|
| `Ctrl+N` / `O` / `S` | New / open / save | `Ctrl+Shift+S` | Save as |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo | `Ctrl+X` / `C` / `V` | Cut / copy / paste |
| `Delete` | Delete | `Ctrl+A` | Select all |
| `P` `A` `C` `R` `D` `T` | Polyline, arc, circle, rectangle, points, text | `M` | Measure |
| `Ctrl+M` / `Ctrl+R` / `Ctrl+E` | Move / rotate / resize | `Ctrl+J` | Join |
| `Ctrl+P` / `Ctrl+I` | Convert to polyline / region | `Ctrl` + `+` | Trim / extend |
| `Ctrl+G` | Snap to grid | `Ctrl+F` | Toolpath view filter |
| `Ctrl+T` | Generate toolpaths | `Ctrl+W` | Produce G-code |
| `Ctrl+Shift+P` | Print | `F2` | Rename |
| `Ctrl+B` | Break at intersections | `Ctrl+L` | Align |
| `Ctrl+1` / `2` / `3` / `4` | View XY / XZ / YZ / isometric | `Home` | Reset view (XY, zoom to fit) |
| `Ctrl+Shift+T` | 3D simulation | `F5` | Run script objects again |
| `F1` | User guide | `Space` + left drag | Pan the view |

## 8. Files

BamCam reads and writes CamBam drawings (`.cb`) and post processors (`.cbpp`), imports DXF, SVG, STL, G-code and images.
Values and objects BamCam does not know are kept when a file is saved again. Objects of plugins and script objects
are also saved as plain polylines, so CamBam (and BamCam without the plugin) can still open and machine them.

**Templates:** any drawing can serve as a template, for example one with your post processor, stock, machining
origin, layers, a frame or standard operations. *File ▸ New from Template* chooses one (it starts in the `templates`
folder of the system folder) and opens an untitled copy; the template itself is never changed, and the G-code file
name of its Machining stays. Tools ▸ Options ▸ Files ▸ *Drawing Template* makes File ▸ New always start from one.

**Opening `.cb` files by double-click:** the Windows installer offers it as an option (off by default, since CamBam may
already open them). The Linux package registers the file type when installed (`install.sh`), and on macOS BamCam is
offered under *Open With* for `.cb` files.

**More in the menus:** View ▸ *Zoom Actual Size* shows the drawing at about its real size on the screen (1 mm as
1 mm, taking the screen at the 96 units per inch the system scales to). View ▸ *Window Opacity* makes the main window
see-through (10 to 100 %), for example to trace a picture shown by another program behind it; it is not stored.
Machining ▸ *Browse Gcode Folder* (also in the right-click menu of Machining) opens the folder of the G-code file in
the file manager.

**SVG** (from Inkscape, Illustrator, Affinity and others; File ▸ Open, File ▸ Insert File or drag and drop): the drawing
keeps its real size (plain numbers are pixels at 96 per inch) and its place on the page, whose lower left corner
becomes the origin. Inkscape layers become layers (in files of other programs the named top-level groups); objects
outside a layer go to a layer named after the file. Circles stay circles and rectangles stay rectangles where the
transform allows; every other path becomes a polyline per subpath, with Bézier curves and elliptical arcs fitted with
arcs to 0.005 mm. Hidden objects and layers are left out. Text and embedded images are skipped with a message:
convert text to paths first (Inkscape: Path ▸ Object to Path), import images with File ▸ Import Image. Strokes and
fills do not matter: the outline of each path is imported, not the width of its line.

**Operations only BamCam has:** Plasma, V-Engrave, Drag Knife and the lathe operations Facing,
Grooving, Threading and Lathe Drilling are saved in the `.cb` file, but CamBam does not know them. It opens such a
drawing without them and without a message, and if you save it in CamBam they are gone. When you save such a drawing,
BamCam says so in the message window. Keep a copy, or generate the G-code in BamCam.

**Values BamCam does not know:** a drawing or style from CamBam can hold an option BamCam does not have (for example
an option of a newer CamBam). BamCam keeps it in the file, shows it in red in the properties with
*(unknown to BamCam)* and reports it when the drawing is opened. The operation gets no toolpath until you choose a
value BamCam knows, so that it never cuts with a stand-in value.

**Start Corner of the 3D profile:** BamCam has CamBam's ten values and starts like CamBam: *Horizontal* starts at the top
for every value with *Top* (its first row from right to left), else at the bottom (left to right); *Vertical* starts at
the right for every value with *Right* (its first column upwards), else at the left (downwards). The other half of the
value does not count, as in CamBam. Compared with CamBam in `examples_to_test/Vergleich Startecke.cb`.

**Milling Direction of the scanline 3D profile:** as in CamBam, *Conventional* (the default) cuts every row the same
way and goes up to the clearance plane between rows; *Climb* cuts every row the other way. *Mixed* goes back and forth
(Horizontal starting left to right, Vertical downwards); it goes over to the next row at the plunge feed when that
starts within *Max Crossover Distance* × tool diameter, otherwise it goes up. Compared with CamBam in
`examples_to_test/Vergleich Zickzack.cb`.

**Scan Angle** (only in BamCam, issue 15) turns the rows of *Horizontal* and *Vertical* by an angle (degrees,
counter-clockwise about the origin), for example to cut along a slanted part without turning the drawing. Start Corner
and Milling Direction apply to the turned rows: *Top* is then the side the turned grid calls top. CamBam does not know
the parameter and cuts such a drawing with unturned rows.

**Rest From Operation** (only in BamCam, Waterline Rough and Trochoidal 3D Rough): rest machining. Choose the roughing
operation done before with a larger tool (the list offers the other Waterline Roughs); this operation then cuts only
the material it left at each level: corners, gaps and narrow places the larger tool could not reach, also the strips a
trochoidal roughing left out. BamCam takes this from the toolpath of the earlier operation, so it works for any tool
shape (a ball nose reaches less wide just above its tip). Its *Roughing Clearance* counts as cut: that skin is for the
finishing. Both operations must cut the same side (Back Face, Mold and Transform alike). The steps the larger tool
leaves on slopes between its levels are not rest machined; the finishing takes them. When nothing is left, the message
window says so; with a name no operation has, everything is cut, with a warning. CamBam does not know the parameter and
cuts everything. Example: *Front rest rough, 3 mm* in `examples/3D profile (pebble).cb`.

File ▸ Insert File adds a file to the open drawing; undo removes the whole insertion again.

- **`.cb`, `.dxf` and `.svg`:** the objects stay at their original position and are selected afterwards, so they can be moved
  at once. They go to the layer of the same name, other layers are added. If you also insert the machining operations
  of a `.cb` file, each part with operations becomes a new part; its output file is cleared, and the post processor of
  the open drawing applies. A `.cb` drawing in other units is scaled, without its operations. The numbers of a `.dxf`
  file are taken in the units of the open drawing; an `.svg` file is converted to them.
- **`.stl`:** becomes a surface on the active layer.
- **G-code** (`.nc`, `.tap`, `.gcode`, `.ngc`, `.cnc` and any other file): becomes an NC File operation of the active
  part and is shown as a toolpath.

File ▸ Export ▸ To STL writes the selected surfaces (meshes), or all visible ones when none is selected, as one binary
STL file in drawing units, moved, rotated or scaled as they are in the drawing. Outlines are not exported: make a
surface of them first (Draw ▸ Surface).

## 9. Not implemented yet

These CamBam functions are not in BamCam yet. If one of them matters to you, say so in
[issue 12](https://github.com/mottobug/BamCam/issues/12) or open a new issue; that decides what comes next.

- **Import of STEP, 3DS and Gerber files.** STL, DXF, G-code and images can be imported.
- **Rotary axis** (wrapping toolpaths around a cylinder for a 4th axis).
- **Corner Overcut of the trochoidal profile (matmill).** The profile is cut without it, with a warning.
- **Drawing on other planes than XY.** Shapes are drawn at Z = 0; they can be moved and rotated in 3D afterwards.
