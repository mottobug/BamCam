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
| Menu and toolbar | All commands; the toolbar has the common ones (drawing tools, snapping, align, operations, generate, G-code). |
| **Drawing** tab (left) | Layers with their objects, parts with their machining operations. Right-click for more. Drag objects onto another layer, and operations onto another operation (before or after it) or a part to reorder or move them. |
| **System** tab (left) | Configuration, post processors, CAM styles and tool libraries. Right-click *CAM Styles* or *Tools* for a new library; a library of a CamBam installation is read-only until you copy it to BamCam (right-click it). In own style libraries right-click for new, duplicate or delete style; double-click a style to edit its name, parent style and values (D = inherited). A style holds values for every kind of operation; *Show values for* only chooses which are shown, and the next style opens with the same kind. Each style opens in one window. |
| Properties (left, below) | Settings of whatever is selected: object, operation, part, layer, drawing. `?` shows the help text. |
| Drawing view | Left button selects (click, window from left to right, crossing window from right to left); middle button or wheel pans and zooms; Alt + left drag rotates (Tools ▸ Options ▸ Rotation Mode also allows holding the middle or right button and dragging with the left one, as in CamBam); right button opens the context menu. 3D meshes appear as shaded surfaces; View ▸ Shade Surfaces switches to wireframes. Lines and toolpaths always lie over a surface, even when they run behind it. |
| Messages (bottom) | What BamCam did, warnings and errors. |
| Prompt bar (above the drawing) | What the active command expects next. |

## 3. Drawing

**Tools** (Draw menu, toolbar, keys): line, polyline (`P`), arc (`A`), spline, circle (`C`), rectangle (`R`),
point list (`D`), text (`T`). Left click sets points, Enter or a middle click finishes, Esc cancels.
The active tool is highlighted in the toolbar.

**Exact sizes:** after the first point, simply type a value; the label at the cursor shows it:

- polyline / line: `25` = segment length towards the cursor, `25<30` = 25 at 30° (counter-clockwise from +X),
  `<90` = direction only; Enter places the point, so you can draw by keyboard;
- circle: the diameter; rectangle: `40x20`;
- hold **Shift** to snap the direction to 15° steps; Backspace corrects the value.

**Snapping:** the toolbar buttons with the red dot switch snap to grid (`Ctrl+G`) and snap to objects. View ▸ Object
Snap Modes chooses the points: end points (square marker), mid points (triangle), circle centres (circle),
quadrants (diamond) and intersections (cross).

**Changing objects** (Edit menu, right-click menu):

- double-click an object to drag its points; Edit ▸ Polyline ▸ Edit Points for a table;
- move (`Ctrl+M`), rotate (`Ctrl+R`), resize (`Ctrl+E`), mirror, array copies, align (toolbar button with the
  arrow: left / centre / right / top / middle / bottom, to the last or first selected object, the selection, the
  drawing, the origin or the stock);
- offset, join (`Ctrl+J`), union / subtract / intersection, explode, convert to polyline (`Ctrl+P`) or region;
- **fillet / chamfer** (Edit ▸ Polyline): select a polyline or rectangle, type the radius, click corners;
  Space switches fillet / chamfer / corner overcut, `-` gives an inverted fillet, Esc ends;
- **break at intersections** (Edit menu): select objects that cross; each line, arc, circle, rectangle, polyline or
  spline is split into separate polylines where it crosses another selected object (or itself);
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

**Scripts menu:** your own C# scripts as `.csx` files, run with a click. They can read and change the whole drawing.
New, edit, check and run them in the script editor, or copy files into the script folder. Before a file runs for the
first time BamCam shows its text; see [scripts.md](scripts.md#scripts-of-the-scripts-menu).

Everything can be undone with `Ctrl+Z`.

## 4. Machining

1. **Select** the objects to machine.
2. **Add an operation** (Machining menu or toolbar): profile, pocket, drill, engrave, 3D profile, drag knife, plasma, or a
   lathe operation (Machining ▸ Lathe).
3. **Set it up** in the properties: tool diameter (or a tool number from the library), depths, feeds, spindle
   speed. Tools ▸ Speeds and Feeds suggests spindle speed and feeds and applies them to the selected operation.
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
thinned to every second or third cell. Left drag rotates, right drag moves, the wheel zooms. The simulation is 3-axis
(no undercuts); plasma and drag knife are not shown.

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
  a tapping head or rigid tapping. The post processor writes its *Tap* cycle; posts without one (LinuxCNC, GRBL)
  leave these holes out with a warning. Thread milling and tapping are BamCam operations: CamBam opens a drawing
  without them.
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
  the table changes; a value typed into the operation wins. The tables start with a few rough values for 30 to 45
  A cutters: replace them with the cut chart of your plasma cutter (right-click: new, duplicate, delete entry).
  The toolbar button with the torch inserts a plasma operation.

  *THC* sends the THC on / off codes of the post processor around each cut (left out in holes up to *Small Hole
  Size*, which are also cut slower). Use the post processor *GRBL Plasma* (torch `M3 S1000` / `M5`, pierce delay
  `G4 P…`, probing `G38.2` and `G92`) or set the *Plasma* templates of your own post: Torch On, Torch Off, Pierce
  Delay, Torch Probe, Torch Probe Set Z, THC on / off. On GRBL switch laser mode off (`$32=0`), otherwise `M3`
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
`~/.config/BamCam/plugins`); BamCam asks once before loading one, Tools ▸ Plugins lists them. Tools ▸ Options holds
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
| `F1` | User guide | | |

## 8. Files

BamCam reads and writes CamBam drawings (`.cb`) and post processors (`.cbpp`), imports DXF, STL, G-code and images.
Values and objects BamCam does not know are kept when a file is saved again. Objects of plugins and script objects
are also saved as plain polylines, so CamBam (and BamCam without the plugin) can still open and machine them.

**Operations only BamCam has:** Plasma, V-Engrave, Drag Knife and the lathe operations Facing,
Grooving, Threading and Lathe Drilling are saved in the `.cb` file, but CamBam does not know them. It opens such a
drawing without them and without a message, and if you save it in CamBam they are gone. When you save such a drawing,
BamCam says so in the message window. Keep a copy, or generate the G-code in BamCam.

File ▸ Insert File adds a file to the open drawing; undo removes the whole insertion again.

- **`.cb` and `.dxf`:** the objects stay at their original position and are selected afterwards, so they can be moved
  at once. They go to the layer of the same name, other layers are added. If you also insert the machining operations
  of a `.cb` file, each part with operations becomes a new part; its output file is cleared, and the post processor of
  the open drawing applies. A `.cb` drawing in other units is scaled, without its operations. The numbers of a `.dxf`
  file are taken in the units of the open drawing.
- **`.stl`:** becomes a surface on the active layer.
- **G-code** (`.nc`, `.tap`, `.gcode`, `.ngc`, `.cnc` and any other file): becomes an NC File operation of the active
  part and is shown as a toolpath.
