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
| **System** tab (left) | Configuration, post processors, CAM styles and tool libraries. Right-click *CAM Styles* or *Tools* for a new library; a library of a CamBam installation is read-only until you copy it to BamCam (right-click it). In own style libraries right-click for new, duplicate or delete style; double-click a style to edit its name, parent style and values (per kind of operation; D = inherited). |
| Properties (left, below) | Settings of whatever is selected: object, operation, part, layer, drawing. `?` shows the help text. |
| Drawing view | Left button selects (click, window from left to right, crossing window from right to left); middle button or wheel pans and zooms; Alt + left drag rotates (Tools ▸ Options ▸ Rotation Mode also allows holding the middle or right button and dragging with the left one, as in CamBam); right button opens the context menu. |
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
  near its end to extend it, Shift+click a piece to trim it.

**Text:** Draw ▸ Text uses the installed fonts. Draw ▸ Text on Polyline bends a selected text along a selected
polyline, line, arc or circle: click the start point; `U` reverses the direction, `S` puts the text on the other
side.

**Images** (File ▸ Import Image): *Trace Outlines* turns the dark (or light) areas into polylines or splines,
*As Height Map* turns brightness into a surface for the 3D profile (reliefs). The dialog previews the result while
you set threshold, smoothing and size.

**Script objects** (Draw ▸ Script Object): shapes generated by a small C# script, e.g. a flange with a hole circle;
see [scripts.md](scripts.md).

Everything can be undone with `Ctrl+Z`.

## 4. Machining

1. **Select** the objects to machine.
2. **Add an operation** (Machining menu or toolbar): profile, pocket, drill, engrave, 3D profile, drag knife, plasma, or a
   lathe operation (Machining ▸ Lathe).
3. **Set it up** in the properties: tool diameter (or a tool number from the library), depths, feeds, spindle
   speed. Tools ▸ Speeds and Feeds suggests spindle speed and feeds and applies them to the selected operation.
4. **Generate toolpaths** (`Ctrl+T`) and check them in the drawing; View ▸ Toolpath View Filter (`Ctrl+F`) steps
   through them.
5. **Choose the post processor** (Machining ▸ Post Processor, or the list *Post Processor* of the machining options
   in the drawing tree; it is saved in the drawing, so each file can have its own machine. Without a choice the default
   from the options applies) and **produce G-code** (`Ctrl+W`).

**3D simulation** (Machining ▸ 3D Simulation, `Ctrl+Shift+T`, toolbar button next to *Generate*): a separate
window shows the stock after the milling operations as a shaded 3D model, cut with the real tool shape (end mill,
ball nose, bull nose, V-bit) and diameter. Material left above what an operation is meant to reach is coloured:
yellow to red for rest material (corners the round tool cannot reach, holding tabs, roughing clearance), magenta
where the tool cut below the target. Targets are the area of pockets, the cut band of profiles, drill holes and the
surface of 3D profiles, each at its target depth. It shows what is selected in the drawing tree: one operation, all operations of a part, or (Machining, the drawing) all of them, and follows the tree while it is open; the check boxes on the left change the choice. The buttons below step through the moves one by one (◀ / ▶, or the
arrow keys), jump to the start or end (Home / End) or play them (Space) at the speed chosen next to the buttons (1 to 1000 moves per second or as fast as possible); the counter shows the move and the total,
with the operation and the G-code of the move, and the tool is drawn at its position. *Cell size* sets the accuracy (smaller = finer and slower), *Tolerance* how much material
counts as rest material. Left drag rotates, right drag moves, the wheel zooms. The simulation is 3-axis (no
undercuts); turning, plasma and drag knife are not shown.

Operations refer to their objects by ID (*Primitive IDs* in the properties; `[...]` picks them in the drawing).
Parts group operations; each part can have its own stock, origin and nesting. CAM styles provide default values;
values you type override them.

Special operations:

- **Lathe:** draw only the profile on one side of the spindle axis (through the machining origin). Turning
  (external or internal, nose radius compensation computed or by the machine), facing, grooving / parting,
  threading and drilling; the remaining stock is shown in the drawing. Use a lathe post processor such as
  *LinuxCNC Turn*.
- **Drag knife** for vinyl and foil: set the blade offset; corners get swivel arcs.
- **Trochoidal pocket** (Machining ▸ Trochoidal Pocket, toolbar button with the loops) for high speed clearing:
  the tool enters with a helix (*Ramp Angle*) at the widest place and clears the pocket in arcs along its
  centre lines. Each arc enters the material with no engagement and widens to at most *Step Over* (a fraction of the tool diameter, e.g. 0.15); a straight chord through the cleared area (at *Chord Feedrate*) leads to the next one,
  also in corners and slots. That allows deep cuts with a small step over and a high feedrate. *Mixed* is not supported here and is cut as Climb, with a warning. Islands
  are left standing; *Final Pass* cuts along all walls at the end of each depth level. Places narrower than *Min
  Circle Radius* are left to the final pass. Example: `examples_to_test/Trochoidal pocket.cb`.
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

**Operations only BamCam has:** Plasma, V-Engrave, Trochoidal Pocket, Drag Knife and the lathe operations Facing,
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
