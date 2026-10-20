# BamCam – UI & Feature Implementation Spec

Version 0.1 – derived (clean-room) from reading the CamBam plus 1.0 user help only (help rev. 1.23).
No binaries were inspected. All descriptions below are paraphrased; only UI labels, menu captions,
property names and shortcuts are reproduced literally so that BamCam can present a familiar UI.

Target: Avalonia UI on .NET 10, Windows / Linux / macOS.

### Legend

| Tag | Meaning |
|-----|---------|
| **[doc]** | Behaviour is explicitly described in the help. |
| **[inf]** | Inferred from indirect references in the help (e.g. a tutorial says "use Edit - Offset from the context menu"). Placement or detail may differ from the original. |
| **[prop]** | BamCam proposal. The help doesn't say, so we pick a sensible value. |
| *New 1.0* | The help marks it as new in v1.0 (lower priority for a 0.9.8-level MVP). |

Where the help is inconsistent, the conflict is noted and a resolution is proposed.

---

## 0. Core concepts and terminology

- **Drawing (document)**: one file. It holds the CAD data (layers → entities) and the CAM data (Machining → Parts → MOPs). The help calls the native file `.cb`. BamCam uses its own format (e.g. `.bcam`, XML or JSON), because we can't legally reverse-engineer `.cb`. Importing `.cb` is out of scope unless a public format description turns up.
- **Entity / drawing object / primitive**: a CAD object. Each one has a **unique integer ID** within the drawing. The tree shows the ID in brackets, e.g. `Circle (3)`. MOPs refer to their source geometry by ID through the **Primitive IDs** property.
- **MOP**: a machining operation (Profile, Pocket, Drill, Engrave, 3D Profile, Lathe, NC File).
- **Part**: a group of MOPs with its own optional stock, tool library, style, origin, nesting and output file.
- **CAM Style**: a reusable, named set of MOP parameter values. A MOP property can be in one of three states: *Default* (inherited), *Value* (explicit) or *Auto* (computed).
- **System libraries**: configuration, CAM style libraries, tool libraries, post processors, materials and machine definitions. They're shared by all drawings, live in the **System Folder** and are shown in the **System** tab.
- **Coordinate system**: right-handed. +Z points out of the screen towards the viewer. Positive Z is "up, away from the stock" and negative Z goes into the stock. A typical setup has Stock Surface = 0 and a negative Target Depth. **[doc]**
- **Units**: each drawing has a Units property (Millimeters or Inches). Changing it re-labels the numbers but doesn't scale the geometry. Use Transform – Resize to scale. **[doc]**

---

## 1. Main window layout

### 1.1 Regions (numbered as in the help's UI overview) [doc]

| # | Region | Content |
|---|--------|---------|
| 1 | **Main drawing window** | A 3D OpenGL view of the drawing and its toolpaths. Takes most of the window. |
| 2 | **Drawing tree view** | Left side. Shows layers, entities, the Machining folder, Parts and MOPs. |
| 3 | **Object property window** (property grid) | Left side, below the tree. Edits whatever is selected in the tree or the drawing view. |
| 4 | **Main menu bar** | Top of the window. |
| 5 | **Tool bar** | Below the menu. Shortcuts to frequently used commands and settings. |
| 6 | **Message window** | Below the drawing view. Shows errors, warnings, info and timing messages. Tools – Clear messages empties it. |
| 7 | **Drawing context menu** | Right-click in the drawing view. Offers commands for the current selection. |
| 8 | **System tab** | A tab next to the **Drawing** tab above the tree. Shows global libraries and settings. |

Additional elements:

- **Status bar** (bottom). The current cursor coordinates appear at the bottom right. **[doc]** BamCam proposal: also show the current units, snap state (grid/points) and active layer. **[prop]**
- **Command prompt line** at the top of the drawing window. While a drawing or transform command is running, it shows guidance such as "Select center point". **[doc]**
- **Busy indicator**: during long calculations the app shows a configurable "Thinking Message". **[doc]**

### 1.2 Docking arrangement

```
+---------------------------------------------------------------------+
| Menu bar: File Edit View Draw Machining Tools Plugins Help  [inf]   |
+---------------------------------------------------------------------+
| Toolbar: New Open Save | Units▼ | Axis Grid | draw tools | CAM tools |
+-------------------+-------------------------------------------------+
| [Drawing][System] | (prompt line)                                   |
|  tree view        |                                                 |
|                   |              Drawing view (3D)                  |
|-------- splitter -|                                                 |
| property toolbar  |                                                 |
|  property grid    +-------------------------------------------------+
|                   | Message window                                  |
|  (help pane)      |                                                 |
+-------------------+-------------------------------------------------+
| Status bar                                     X: ...  Y: ...  Z:.. |
+---------------------------------------------------------------------+
```

- The left column has a tab strip (**Drawing** | **System**), with the tree on top and the property grid below. A horizontal splitter between them can be dragged with the left mouse button (the cursor changes to a vertical-resize cursor). **[doc]**
- The property grid's name/value column divider can also be dragged. **[doc]**
- The message window sits under the drawing view, with a splitter between them. **[doc]** (position) / **[prop]** (splitter)
- Default sizes aren't documented. **[prop]** Left column 300 px (about 22–25% of 1366 px), tree and property grid split 45/55, message pane 110 px high, minimum window 1024×700. Save the splitter positions in the user configuration.

### 1.3 Property grid toolbar [doc]

- **Sort toggle**: alphabetical vs. categorized (grouped) display.
- **Advanced / Basic**: Basic view shows only the most-used properties plus any property whose value differs from its default. Advanced shows everything. Several properties appear only in Advanced view: Primitive IDs, Clearance Plane in some cases, the Nesting collection editor, and the post processor sections.
- **Help (?) toggle**: shows a short description of the selected property in a panel at the bottom of the grid.
- **Value-state glyph**: for MOPs and styles, a small icon to the right of each property name shows its state: *Default* (grey italic value, inherited), *Value* (explicit) or *Auto* (computed). Clicking the glyph, or right-clicking the property, opens a menu with **Default / Value / Auto** and **Inherited style** (a message box that says where the effective value comes from). See §6.3.
- **[...] buttons** on collection or complex properties open editors: Points, Transform matrix, Text (multi-line), Script, Primitive IDs (interactive selection), Start Point / Machining Origin (pick in view), Out File (file browser), Nesting items.
- **Composite properties** expand with **+** (Holding Tabs, Lead In Move, Lead Out Move, Side Profile, Nesting, Stock, Grid Info).
- Multi-selection: when several objects are selected, show the properties they have in common. **[inf]**

### 1.4 Toolbar contents [doc/inf]

Documented buttons and icons:

- File: **New**, **Open**, **Save**.
- **Drawing units** drop-down (Millimeters / Inches, possibly others). After a change it asks: "Would you also like to change the default units for new drawings?" Yes also updates the config's Drawing Units. No changes only this drawing. **[doc]**
- **Show Axis**, **Show Grid** toggles. **[doc]**
- Draw tools: Line, Polyline, Arc, Circle, Rectangle, Point list, Text, Spline (New 1.0), Bitmap (New 1.0), Script object (New 1.0), plus Region and Surface icons. **[doc icons]**
- CAM tools: Profile, Pocket, Drill, Engrave, 3D Profile. Lathe is not on the toolbar because it's a plugin. **[doc]** NC File has an icon. **[inf]**

### 1.5 Message window behaviour [doc]

- Shows errors, warnings and info. How much appears is controlled by the **Diagnostic Level** config (0 = almost nothing, 4 = verbose).
- If Diagnostic Level ≥ 2, then after toolpath or G-code generation it prints, for each MOP, the toolpath length, cutting time and rapid time. These are rough estimates: rapids assume about 600 mm/min, Z rapids are ignored, drilling and plugin MOPs are skipped, and nested parts count one instance only.
- Warnings include: Target Depth above Stock Surface (the depth is clamped to the stock surface), no source shapes for a MOP, and post processor parser errors (these can be suppressed per post).

---

## 2. Menu structure

The help has no single menu reference. The structure below is reconstructed from every reference in the help. Items are tagged where their placement is inferred.

### 2.1 File

| Item | Shortcut | Behaviour |
|------|----------|-----------|
| New | – | Creates an empty drawing using configured defaults. If the **Drawing Template** config is set, that file is the starting point (posts, stock, objects and MOPs included). |
| New from template | – | Asks for a drawing (usually in the System Folder `templates` subfolder) and creates an untitled copy. The template file itself is never changed. Machining Out File is kept from the template. |
| Open… | Ctrl+O | Opens a native drawing, or imports DXF (up to AutoCAD 2000), 3DS, STL, RAW, STEP (.stp/.step, New 1.0), Gerber (.gbr) or G-code. Unknown extensions are treated as G-code, which creates an NC File MOP. Bitmaps (jpg/png/bmp/…) are inserted as Bitmap entities. Files can also be dragged and dropped onto the window or passed on the command line. Can run on a worker thread (config). |
| Save | Ctrl+S | Saves the drawing. Before overwriting, rotates backups `name.b1` (newest) … `name.bN`, where N = **File Backups** config. |
| Save As… | – | Saves under a new name or location. |
| Print… | – | *New 1.0.* Opens a print-preview dialog (see §2.1.1). |
| Export ▸ To DXF | – | Writes DXF R12 (AC1009). Objects that can't be expressed correctly (rectangles, rotated text, some transformed arcs and circles) are converted to polylines. The config option *Export 3D Polylines* forces Z on every vertex. |
| Export ▸ To STL | – | *New 1.0.* Merges all surface objects on visible layers into one STL. |
| Recent files | – | **[inf/prop]** MRU list. |
| Exit | – | **[prop]** Saves settings if **Save settings on exit** is checked. |

#### 2.1.1 Print dialog (*New 1.0*) [doc]
- Print colours and line widths come from each layer's **Color** and **Pen Width**. The background isn't printed. A pen width of 0 may not print. White prints as black in monochrome and B/W modes. In colour mode, a colour that matches the background is inverted.
- The preview pane supports the same pan, zoom and rotate gestures as the main view. Alt+double-click centres the drawing on the page. The zoom factor is shown in yellow at the top left. The mouse wheel or a **Scale** popup (with a **Custom** value) changes the zoom.
- Buttons: **Printer setup**, **Print**, **Preview**, **Options**. A right-click menu offers the same options.
- Options: **Print Color** (Color / Monochrome / Black & White), **Border Size** (0 = no border), **Margins**, **Page Orientation** (Portrait/Landscape), **Line Width Scale** (default 1). The same options also appear under Tools ▸ Options.

### 2.2 Edit

| Item | Shortcut | Behaviour |
|------|----------|-----------|
| Undo | Ctrl+Z | Undoes the last operation. Covers entity, layer and MOP edits, including layer delete. |
| Redo | Ctrl+Y | Redoes the last undone operation. |
| Cut | Ctrl+X | Puts the selected entities, layers, MOPs or Parts on the clipboard and removes them. |
| Copy | Ctrl+C | Puts the selection on the clipboard. Works across running instances. |
| Paste | Ctrl+V | Pastes into the layer selected in the tree. If no layer is selected, pastes into the active layer. From a layer's context menu, pastes into that layer. MOPs paste into the current Part. |
| Paste Format | Shift+Ctrl+V | Copies every parameter from the MOP on the clipboard into the selected MOP or CAM style. The target keeps its Name and Primitive IDs. |
| Delete | Del | Deletes the selected entities, layers, MOPs or Parts. |
| Select All | Ctrl+A | Selects all visible entities. Hidden and locked layers are excluded. **[inf placement]** |
| Select All on Active Layer | Shift+Ctrl+A | **[inf placement]** |
| Explode | – | Splits an entity into its parts. Polyline → lines and arcs. Point list → single points. Text → one region per glyph. Region → polylines. Script object → static entities. |
| Join | Ctrl+J | Asks for a **join tolerance** (drawing units) and joins selected open shapes whose end points are within that tolerance. |
| Offset | – | Asks for a distance. A positive value gives an outside offset polyline, a negative value an inside one. On an open polyline the result is another open polyline. |
| Open Offset | – | Asks for a distance. Builds a closed outline that encloses an open shape, like a slot or PCB track. The result may self-overlap. |
| Union | Ctrl+U | Replaces the selected closed shapes with their combined outer boundary. |
| Subtract | – | Subtracts closed shapes from other closed shapes. Order semantics: **[prop]** first selected minus the rest. |
| Intersection | – | Intersection of the **first two** selected shapes only. |
| Trim | – | Deletes the parts of objects that lie inside (or outside) the selected trimming objects. |
| Fillet | – | Asks for a radius and inserts arcs between adjacent straight segments of a polyline. Separate lines have to be joined first. |
| Intersection Points | – | Adds point entities where the selected shapes intersect, so later operations can snap to them. |
| Break At Intersections | Ctrl+B | Splits the selected shapes where they cross other selected shapes. |
| Convert To ▸ Polyline | Ctrl+P | Converts the selection to polylines. Also breaks the link to a bitmap's file by keeping its vectors. The ID changes, so MOP sources must be re-selected. |
| Convert To ▸ Region | Ctrl+I | Builds a region from a selected outer shape and inner holes. (Conflict: the entity page says Ctrl+R, the shortcut table says Ctrl+I, and Ctrl+R is Rotate. **Use Ctrl+I.**) |
| Polyline ▸ Edit | (double-click) | Enters interactive control-point editing. |
| Polyline ▸ Reverse | – | Reverses the point order. This flips which side an offset or profile falls on for open shapes. |
| Polyline ▸ Clean | – | Removes duplicate points. |
| Polyline ▸ Break at points | – | Splits the selected polylines at the points of the selected point list(s). |
| Polyline ▸ Set start point | – | Changes the first vertex of a closed polyline (interactive pick). |
| Polyline ▸ Arc Fit | – | Asks for an **Arc Fit Tolerance** and replaces runs of short segments with fitted arcs. |
| Polyline ▸ Remove overlaps | – | Builds a copy with back-tracking "Z" glitches removed, placed on the active layer. |
| Surface ▸ Plane Slice X / Y / Z | – | Slices the selected meshes with evenly spaced planes normal to the axis. Asks for the interval. Output is polylines. Arc fitting is optional (config). |
| Surface ▸ Silhouette | – | Like Plane Slice Z, but overhangs from higher levels are projected down, giving the areas a tool can reach. Z only. |
| Surface ▸ Invert Faces | – | Flips the face normals (winding) of the selected meshes. |
| Surface ▸ Edge Detect | – | Outputs mesh edges that are boundary edges or sharp (angled) edges. |
| Surface ▸ Project Lines to Surface | – | Projects the selected 2D shapes onto the selected surfaces. Asks for a **Projection Resolution**. Points that miss every surface get the minimum depth of the surfaces. |
| Transform ▸ … | – | See §2.3. The help calls it both "Edit - Transform - X" and "Transform - X". |

### 2.3 Edit ▸ Transform

| Item | Shortcut | Behaviour |
|------|----------|-----------|
| Move (Translate) | Ctrl+M | Pick a source point, then a destination point (both snap). *New 1.0:* Ctrl+click picks up to 3 source points (shown red, green, blue), followed by the same number of destination points. 2 points = move + rotate in XY. 3 points = full 3D alignment. |
| Rotate | Ctrl+R | Pick the centre, then a reference angle point (a middle click skips it and uses 0° = +X), then move the mouse to set the angle and click. With snap to grid on, the angle snaps to multiples of 30° and 45°. Press **X / Y / Z** during the command to change the rotation axis. The angle is always set by mouse movement in the view plane. Rotating 180° about Y mirrors the object. |
| Resize | Ctrl+E | Dialog with a row per axis X/Y/Z. Each row has an enable checkbox, **Original Size**, **New Size** and **Percent**, plus **Preserve aspect ratio** and quick buttons **100%**, **mm to inches**, **inches to mm**. **Apply** commits the change. |
| Align | Ctrl+L | Dialog with three columns (X, Y, Z). For each axis choose the reference (none / Left·Center·Right, Bottom·Center·Top, Lower·Center·Upper) and a target coordinate. **Apply** commits. Only one instance can be open at a time. |
| Mirror | – | Makes a mirrored **copy** about a line through two picked points. Shift constrains the line to 0/30/45/60/90° in each quadrant. |
| Array Copy | – | Asks for the number of copies (not counting the original), then an offset `X,Y[,Z[,scale]]`. Copy *n* is scaled by `1+(scale-1)*n`. |
| Polar Array Copy | – | Pick the centre, enter the number of copies, then the rotation per copy `X,Y[,Z]` in degrees (right-hand rule). Example: 11 copies at `0,0,30` gives 12 objects evenly spaced around a circle. |
| Center (Extents) | – | Moves the selection so the centre of its bounding box is at the origin. |
| Center (Of Points) | – | Moves the selection so the average of all control points is at the origin. |
| Apply Transformations | – | Bakes each selected entity's Transform matrix into its geometry and resets the matrix to identity. (The help lists this under the **View** menu. **[prop]** Show it in both places.) |

- Freehand move: hold **Shift** and drag the selection with the left button. Shift+arrow keys move by one minor grid unit (1 mm or 1/16"). Ctrl+Shift+arrow keys move by one major grid unit (10 mm or 1"). **[doc]**
- Freehand rotate: hold **Shift** and use the view-rotate gesture (e.g. Alt+Shift+drag). Rotates about the origin with no snapping. **[doc]**
- **Auto-Apply Transformations** config: when true, move, rotate, resize and array automatically bake the transform, unless that's impossible (e.g. a circle rotated about X), in which case the matrix is kept. **[doc]**

### 2.4 View

| Item | Shortcut | Behaviour |
|------|----------|-----------|
| Reset | Alt+double-click, Home | Returns to the top (XY) view and runs Zoom To Fit. With the Left+Middle rotation mode, hold middle and double-click left. With Left+Right, hold right and double-click left. |
| Zoom To Fit | – | Fits every object on visible layers into the view without changing the orientation. |
| Zoom Actual Size | – | Zooms so objects appear at roughly physical size on the monitor. |
| Show Toolpaths | – | Toggle (stored on the drawing). |
| Show Cut Widths | – | Toggle. Shades the swept tool area. Only works in an unrotated view. |
| Show Stock | – | Toggle the stock block display. |
| Show Nests | – | Toggle display of nesting copies. |
| Show Rapids | – | Toggle the dashed rapid-move lines. |
| Show Direction Arrows | – | Toggle the direction arrows on toolpaths. |
| Show Grid | – | Toggle (also on the toolbar). |
| Show Axis | – | Toggle the XYZ axis lines (also on the toolbar). |
| Anti-alias | – | Toggle line anti-aliasing. |
| Wireframe | – | Toggle shaded vs. wireframe meshes. |
| Snap to Grid | Ctrl+G | Toggle. |
| Snap to Objects | – | Toggle snapping to control points, centres, intersections and mesh vertices. |
| Window Opacity | – | Sets the main window opacity from 0 to 100%, so you can trace over a window behind it. (**[prop]** Optional on non-Windows platforms.) |
| XY Plane / XZ Plane / YZ Plane | – | Standard views: top (XY, default), front (XZ) and side (YZ). Mouse drawing works **only in XY**. |
| Toolpath View Filter… | Ctrl+F | A modeless window for stepping through toolpaths (§6.12). |
| Apply Transformations | – | See §2.3. |

The right-click drawing context menu has a **View** submenu that mirrors this menu. **[doc]**

### 2.5 Draw

| Item | Key | Behaviour |
|------|-----|-----------|
| Line | – | Multi-segment line with straight segments only (§4.6). |
| Polyline | P | Lines (and arcs, when edited later) (§4.6). |
| Arc | A | Three clicks (§4.6). |
| Circle | C | Click the centre, then click to set the radius. |
| Rectangle | R | Click one corner, then the opposite corner. |
| Point List | D | Click points. Enter or a middle click finishes. |
| Point List ▸ Divide Geometry | – | Asks for a count and puts that many evenly spaced points along the selected shape (e.g. a bolt circle). |
| Point List ▸ Step Around Geometry | – | Asks for a step distance and puts a point every *step* along the shape. |
| Point List ▸ Fill Geometry | – | Asks for a step and fills a closed shape with a grid of points. Holes are excluded. |
| Point List ▸ Offset Fill Geometry | – | Same as Fill, but every other row is shifted by half a step. |
| Point List ▸ Centers | – | One point at the centre of each selected object. |
| Point List ▸ Extents | – | Points at the bounding-box extremes and centre of each object. |
| Text | T | Opens a multi-line text dialog. After OK, click to place the text. Repeats until Cancel when Repeat Commands is on. |
| Spline | – | *New 1.0.* Click the control (inflection) points. Enter, a middle click or Esc ends it. |
| Bitmap… | – | *New 1.0.* Choose an image file. It's linked, not embedded, and vectorised (§5.1). |
| Script Object | – | *New 1.0.* Inserts a script entity containing the **Default Script Entity** text. |
| Surface ▸ From Mesh File | – | Inserts an STL mesh. |
| Surface ▸ From Bitmap | – | Dialog: image file button, **Heightmap Size** X/Y (0 = keep the aspect ratio), **Grid Stepover** (0 = one pixel), **Z Height range** min/max, **Invert**, then **Create surface**. Builds a triangle mesh with Z taken from brightness (dark = low unless inverted). |
| Surface ▸ From text file | – | Reads a text file where each line holds nine numbers (three XYZ vertices) and makes one triangle per line. |
| Surface ▸ Extrude | – | Asks for an **Extrusion Height** (+ goes towards +Z) and **Extrusion Steps** / **Arc Expand Tolerance**. Makes an open side-wall mesh. |
| Surface ▸ Extrude Solid | – | *New 1.0.* Asks for a thickness and an Arc Expand Tolerance. Makes a closed (capped) mesh suitable for STL export. |
| Surface ▸ Fill | – | *New 1.0.* Triangulates the selected closed shapes or regions into a flat mesh with no thickness. Asks for an Arc Expand Tolerance. |
| Region Fill (Fill Region) | – | Asks for **Margin** and **Step Over**, then a pattern: Inside Offsets, Outside Offsets, Inside + Outside Offsets, Horizontal Hatch, Vertical Hatch. Produces polylines (§5.4). |
| More ▸ Tangent | – | Draws a line tangent to two circles or arcs (§4.6). |
| More ▸ Flat Spiral | – | Asks for **Stepover** and **Direction** (CW/CCW), then you draw it like a circle (centre, then radius). |
| More ▸ Spiral Path | – | Select a path shape first. Asks for Stepover, Direction and Radius, then makes a spiral that runs along the path. |
| More ▸ Trochoidal Path | – | Like Spiral Path, but builds a chain of arcs. Meant for engraving-based slotting. |
| More ▸ Ellipse | – | Drag a bounding rectangle. The result is a polyline made of arcs and lines. |

Note: the help also refers to "Draw - Points List - …" in the drawing context menu, so the context menu contains a Draw submenu. **[doc]**

### 2.6 Machining (the help also says "CAM" and "Machine")

| Item | Shortcut | Behaviour |
|------|----------|-----------|
| Profile | – | Creates a Profile MOP from the selected shapes in the active Part. |
| Pocket | – | Creates a Pocket MOP. |
| Drill | – | Creates a Drill MOP from point lists or circles. |
| Engrave | – | Creates an Engrave MOP. |
| 3D Profile | – | Creates a 3D Profile MOP from meshes. |
| Lathe | – | Creates a Lathe MOP (a plugin in the original). |
| NC File | – | **[inf]** Creates an NC File MOP (normally created by opening a G-code file). |
| New Part | – | **[inf/prop]** Adds a Part under Machining and makes it the active Part. |
| Generate Toolpaths | Ctrl+T | Regenerates the toolpaths of every enabled MOP. |
| Produce Gcode | Ctrl+W | Posts every enabled Part and MOP to Machining.Out File. Asks for a path when none is set. Follows **Rebuild Toolpath Before Post**. |
| Edit Gcode | – | Opens Machining.Out File in the internal editor, or in the external **GCode Editor** command from config. |
| Browse Gcode Folder | – | **[doc, Machining folder context menu]** Opens the OS file manager at the output folder. |

### 2.7 Tools

| Item | Behaviour |
|------|-----------|
| Save settings | Writes the configuration plus any changed style libraries, tool libraries and post processors. |
| Save settings on exit | A checkable item. When checked, settings are saved automatically when the app closes. |
| Browse system folder | Opens the System Folder (libraries, posts, templates, samples, scripts). |
| Options… | Opens the configuration grid (§8). Equivalent to System tab ▸ Configuration. |
| Check for new version | Fetches a version number and reports whether an update exists. **[prop]** Point it at BamCam's own endpoint, or leave it out. |
| Clear messages | Empties the message window. |
| Get object extremas | Shows Min (X,Y,Z), Max (X,Y,Z), Width, Height and Depth of the selection. |
| Measure | Key **M**. Pick two points, and a window shows the distance (and **[prop]** dX, dY, dZ and angle). |
| Reload post processors | Re-reads the post processor files from disk. |
| Simulate with CutViewer | Starts the external CutViewer Mill on the produced G-code. Needs a CutViewer-aware post and a defined Stock. **[prop]** Swap this for BamCam's own simulator later, or a configurable external simulator command. |
| Download latest translations | Fetches UI language files. **[prop]** Use .resx/.json localisation instead. |
| Set file associations | Associates the native file type with this install. **[prop]** Platform-specific. |

### 2.8 Plugins / Scripts / Toolkit (original extensibility menus)

- **Plugins ▸ HeightMap Generator**: a separate window (File ▸ Open image, Tools ▸ Options, Tools ▸ Generate Heightmap). Options: ClearPrevious, Invert, XSize/YSize (0 = keep aspect), XStep/YStep (0 = every pixel), ZMax, ZMin. Output is a 3D line plus an Engrave MOP that follows it. **[doc]**
- **Toolkit ▸ Timing Pulley** and similar commands come from a bundled plugin. Out of scope. **[doc]**
- **Scripts ▸ New ▸ VBscript / Python**: opens an internal text editor. **[inf]**
- BamCam: provide a plugin API (C#), with scripting optional. **[prop]**

### 2.9 Help

| Item | Shortcut | Behaviour |
|------|----------|-----------|
| Help / Contents | F1 | Opens the documentation. |
| About | – | **[prop]** |

### 2.10 Drawing view context menu (right-click in the view) [doc/inf]

Contents depend on the selection. Documented submenus:
- **Edit** (Offset, Join, Explode, Transform ▸ …, Convert To ▸ …, Polyline ▸ …), plus Cut / Copy / Paste directly in the menu.
- **Draw** (including Point List ▸ …).
- **View** (Snap to Grid, Snap to Objects, Show Cut Widths, …).
- **Machining** ("Machine") ▸ Generate Toolpaths, Produce GCode.
- **Holding Tabs** submenu. Appears at the bottom only when a Profile MOP is selected in the tree: **Autocalc** (sets Tab Method = Automatic and rebuilds), **Add Tab** (at the right-clicked point on the source shape), **Remove Tab** (right-click inside a tab rectangle).

---

## 3. Project tree

### 3.1 Drawing tab structure [doc]

```
MyDrawing                      <- root node, named after the file; drawing-level settings
├── Layers
│   ├── Default (green "active" arrow)   <- layer: colour swatch, name; greyed when hidden; padlock when locked
│   │   ├── Circle (1)
│   │   ├── Polyline (2)
│   │   └── …                  <- icon by type, "TypeName (ID)"
│   └── Layer1
└── Machining                  <- machining options (global CAM settings)
    ├── Part1                  <- Part (greyed when disabled)
    │   ├── Profile1           <- MOP (greyed when disabled)
    │   ├── Pocket1
    │   └── Drill1
    └── Part2
```

Notes:
- Whether the "Layers" folder node exists is ambiguous. The help says the drawing "is divided into Layers and Machining". **[prop]** Use a "Layers" container node so the two sections are symmetric.
- A new drawing starts with the layer **Default**, plus **Part1** when the first MOP is added. MOPs get auto-names: Profile1, Pocket1, Drill1, … **[inf]**
- New entities go into the **active layer** (marked by a small green arrow). New MOPs go into the **active Part**. **[doc]**
- Drag and drop: move entities between layers. Reorder MOPs or move them between Parts (a horizontal insertion bar shows the drop position). Moving a MOP changes its G-code order. **[doc]**
- Rename: F2, or a slow second click on the name. Avoid parentheses in MOP names because they nest badly in G-code comments. **[doc]**
- **Space** toggles visible/hidden for layers and enabled/disabled for Parts and MOPs. **Ctrl+Space** toggles a layer's lock. **Del** deletes. **[doc]**
- **F5** on a script entity runs its script. **[doc]**
- Selecting a MOP in the tree highlights all of its visible source entities in the view. **[doc]**
- **Selected Entity Focus** (config, default True): when you select an entity in the view, its layer node expands and scrolls into view. Ctrl+Tab (also written Alt+Tab in the help) toggles this focus. **[doc]**
- Double-clicking a text entity in the tree opens its text editor. Double-clicking an NC File MOP opens its source in the G-code editor. **[doc]**

### 3.2 Root (Drawing) node properties [doc unless tagged]

| Property | Meaning |
|----------|---------|
| Name | **[inf]** Drawing name, used by the `{$cbfile.name}` macro. |
| Units | **[inf]** Drawing units. |
| Show Cut Widths | Shade the swept cut area. |
| Show Direction Vector | Arrow at each toolpath start showing the cut direction. |
| Show Rapids | Dashed rapid moves. Rapids are drawn within a MOP only, not between MOPs. **[prop]** BamCam also draws the rapids between MOPs. |
| Show Toolpaths | Same as View ▸ Show Toolpaths. |
| Toolpath Visibility | `All` or `Selected Only` (only toolpaths of the selected MOPs or Parts). |
| Script | *New 1.0.* Script prepended to every script entity in the drawing. |
| Tag | **[inf]** Notes. |

### 3.3 Layer node

Context menu [doc]:

| Item | Behaviour |
|------|-----------|
| New layer | Creates a layer and makes it active. Its colour comes from **Default Layer Color**. |
| Set as active layer | New entities go here. A hidden layer can be active. |
| Hide | Hides the layer. Its objects are excluded from Select All. The tree shows it greyed. |
| Hide all but this | Hides every other layer. |
| Show | Makes the layer visible. |
| Show all | Makes every layer visible. |
| Clear | Deletes all entities on the layer. |
| Select all on layer | Selects the layer's entities. |
| Cut / Copy / Paste | Applies to the layer and its contents. Paste puts clipboard entities into this layer. |
| Delete | Removes the layer and its entities. |
| Rename | Rename (also F2). |
| Lock / Unlock | *New 1.0.* (Ctrl+Space, or the Locked property.) **[inf as menu item]** |

Properties: **Alpha** (0 transparent … 1 opaque), **Color**, **Locked** (*New 1.0*; locked objects can't be picked in the view but can be selected in the tree and still act as snap targets), **Name**, **Number of Entities** (*New 1.0*, read-only), **Pen Width**, **Script** (*New 1.0*), **Tag**, **Visible**.

### 3.4 Entity node

Context menu **[inf]**: Cut, Copy, Paste, Delete, Rename(?), plus the same Edit and Convert commands as the drawing context menu. The tree must support multi-select (Ctrl/Shift click). **[inf]**

### 3.5 Machining node

Context menu [doc/inf]: **Generate Toolpaths**, **Create Gcode File** (Produce Gcode), **Edit Gcode**, **Browse Gcode Folder**, **New Part** **[inf]**, **Paste** (MOP/Part) **[inf]**. Properties: §6.9.

### 3.6 Part node

Context menu [doc]: **Generate Toolpaths** (this Part only), **Produce Gcode** (this Part only; default filename uses the Part name), **Nest to Point List**, **Export ▸ Toolpaths to DXF** (*New 1.0*; writes the Part's toolpaths as polylines and honours the post's arc settings), Enable/Disable (Space), Cut / Copy / Paste **[doc: copy-paste Part used in tutorial]**, Delete, Rename **[inf]**. Properties: §6.10.

### 3.7 MOP node

Context menu [doc]:

| Item | Behaviour |
|------|-----------|
| Enable / Disable MOP | Disabled MOPs appear greyed. Their toolpaths are hidden and they're skipped when posting. |
| Set Start Point | Click in the view. The toolpath begins at the nearest possible point. A red circle marks it and can be dragged. Writes the **Start Point** property. |
| Select Drawing Objects | Enters re-selection mode. Current sources show in red. Click, Ctrl+click and window selection edit the set, and clicking empty space clears it. Enter or a middle click applies. Esc reverts. Also available from Primitive IDs [...]. |
| Cut / Copy / Paste | Clipboard, including between app instances. |
| Paste Format | Copies the clipboard MOP's parameters into this MOP. Name and sources are kept. |
| Delete | Removes the MOP. |
| Rename | Rename (also F2). |
| Reset to defaults | Sets every property to *Default* state so values inherit from the style chain. |
| Generate Toolpaths | This MOP only. |
| Produce Gcode / Create Gcode File | This MOP only. Default filename is `Drawing.Part.MOP.nc`. |
| Toolpaths to Geometry | Converts the toolpaths to polylines on a layer (**[prop]** the active layer). For NC File MOPs this is "Toolpath To Geometry". |
| Speeds and Feeds… | Opens the calculator (§6.14). |

### 3.8 System tab tree [doc]

```
System
├── Configuration            <- same as Tools ▸ Options
├── CAM Styles
│   ├── Standard-mm          <- style library
│   │   ├── (default, empty name)   <- must not be renamed or deleted
│   │   ├── MyStyle
│   │   └── …
│   └── Standard-in
├── Tools
│   ├── Default-mm           <- tool library
│   │   └── 1: 3mm End Mill …
│   ├── Default-in
│   └── Lathe-mm
├── Post Processors
│   ├── Default (green arrow = system default)
│   ├── Mach3, Mach3-CV, Mach3-Turn, Mach3-Turn-CV, EMC2-Turn, RotaryX, RotaryY, …
├── Materials                <- placeholder (future)
└── Machine Definitions      <- placeholder (future)
```

Context menus:
- **CAM Styles folder/library**: New library, New style, Cut / Copy / Paste / Delete / Rename, **Reload** (re-read from disk). Styles can be dragged between libraries.
- **Style**: **New CAM style variant** (creates a child whose Parent Style is this style), **Paste Format** (from a clipboard MOP), Cut / Copy / Paste / Delete / Rename.
- **Tools folder**: **New Library**, **Refresh** (reload from disk, dropping unsaved changes), **Paste**.
- **Tool library**: New tool **[inf]**, Cut / Copy / Paste, Delete (also Del), **Reload**, **Save to XML**, **Rename all tools** (re-applies Tool Name Format).
- **Tool**: Cut / Copy / Paste / Delete. Renumber by editing **Index**. If the index is already used, the following tools shift up.
- **Post Processors folder**: New post processor, Paste. **Post**: **Set As Default** (green arrow), Copy, Paste, Rename, Delete.

Storage **[doc]**: tool libraries are one XML file per library in `<System>/tools`. Posts are XML `.cbpp` files in `<System>/post`. Templates are in `<System>/templates`. Scripts are in `<System>/scripts`. BamCam should use its own extensions (e.g. `.bcpp`) and may add an importer for the documented post macro language. **[prop]**

---

## 4. Drawing view behaviour

### 4.1 Rendering
- A true 3D view (OpenGL in the original, with a GDI fallback). **[doc]** BamCam: Avalonia + OpenGL/Skia, with orthographic or perspective projection (config **View Projection Mode**, **View Focal Length Scale**). **[doc]**
- Arcs are drawn as polylines using **Arc Display Degrees** per segment. Splines use **Spline Curve Steps** segments. **[doc]**
- Meshes are drawn shaded or wireframe. Back-face culling is optional. **[doc]**
- Unselected objects fade when something is selected (**Select Fade** %). Selected objects are drawn in **Select Color**, which the help describes as "bold red". Selected meshes use the selection colour if **Use Surface Selection Color** is on. **[doc]**
- Background, grid, text, toolpath arc, line and rapid, and cut-width colours all come from config. **[doc]**

### 4.2 Units and grid [doc]
- The grid has two definitions, **Grid Info (Metric)** and **Grid Info (Inches)**, each with Drawing Units, Minimum (XY lower-left of the drawn grid), Maximum (XY upper-right), Major Scale and Minor Scale. Defaults implied by the help: metric minor 1 mm / major 10 mm, inch minor 1/16" / major 1". **[doc]** Grid extent defaults **[prop]**: metric −100..100 mm, inch −4..4 in.
- Grid and axis are toggled from the toolbar or View menu. The X axis line is red, and in XZ view it marks the stock-surface level (Z=0). **[doc]** **[prop]** X red, Y green, Z blue.
- Stock is drawn as a translucent box in the stock colour. **[doc]**
- Markers: machining origin = a draggable "X". MOP start point = a draggable red circle. Holding tabs = rectangles, with a red cross through tabs that can't be applied. Nest copies = dashed outlines in the cut-width colour with a draggable red triangle at each centre. **[doc]**

### 4.3 Navigation [doc]

| Action | Default input (Rotation Mode = ALT+Left) |
|--------|------------------------------------------|
| Rotate view | Alt + left drag. Alternatives (config **Rotation Mode**): *Left+Middle* (hold middle, drag with left; middle can be released) or *Left+Right* (press right then left and drag; right can be released). |
| Pan | Middle-button drag, or cursor keys. |
| Zoom | Mouse wheel, centred on the cursor position. Num+ / Num−, PgDn (in) / PgUp (out). |
| Reset view | Alt + double-click (or the per-mode equivalent), Home, View ▸ Reset. |
| Fit | View ▸ Zoom To Fit. |
| Standard planes | View ▸ XY / XZ / YZ Plane. |

### 4.4 Selection [doc]
- Left click selects the object under the cursor and deselects everything else. Clicking empty space clears the selection.
- Ctrl+click adds or removes an object.
- Left drag on empty space draws a selection rectangle. Only objects **fully inside** it are selected. **[prop]** Ctrl+drag adds to the selection.
- Ctrl+A selects all visible objects. Shift+Ctrl+A selects all objects on the active layer.
- Hidden layers can't be selected in the view, and neither can locked layers (those can still be selected in the tree).
- Selecting in the tree selects in the view, and the reverse.
- Del deletes the selection.
- Pick tolerance: **[prop]** 5 px screen distance to the nearest curve. Inside a filled region or mesh triangle counts as a hit.

### 4.5 Snapping [doc]
- **Snap to Grid** (Ctrl+G): points snap to minor grid intersections. It also turns on angle snapping (30°/45° multiples) in Rotate.
- **Snap to Objects/Points**: points snap to control points, end points, circle and arc centres, "other significant points", intersection point entities and mesh vertices. Locked layers still provide snap targets. Bitmaps don't provide snaps when their vectors are hidden.
- **[prop]** Snap priority: object point (within 8 px) beats grid. Show a snap marker glyph. Show the snapped coordinate in the status bar.

### 4.6 Drawing tools interaction [doc unless tagged]

General rules:
- The prompt line shows the current step.
- **Left click** places a point.
- **Enter** or **middle click** finishes a multi-point entity (polyline, line, point list, spline) or confirms an edit.
- **Esc** cancels the current entity or edit, and ends the command.
- With **Repeat Commands** = True (config), a draw command restarts after finishing. Esc or a middle click (with no entity in progress) ends the command mode.
- Right-click opens the context menu. It isn't used to finish entities. **[inf]**
- Drawing happens only in the XY plane (Z=0, **[prop]** or the active work plane in a later version).
- **[prop]** Keyboard coordinate entry (typing `x,y` into the prompt line) isn't documented, but it's strongly recommended. Exact values can always be set afterwards in the property grid, which is the documented way.

| Tool | Interaction |
|------|-------------|
| Polyline (P) | Click each vertex. Press **C** to close the shape, or click the first point (it snaps), then Enter or middle click to finish. |
| Line | Same as Polyline, straight segments only. |
| Circle (C) | Click the centre, release, move to set the radius (rubber band), then click again. |
| Rectangle (R) | Click the first corner, release, move, then click the opposite corner. |
| Arc (A) | Click the start point, then the end point. The third click positions the centre (bulge). **Shift** while moving flips the arc direction. |
| Point list (D) | Click points. Enter or middle click finishes. |
| Text (T) | A dialog asks for (multi-line) text. OK, then click the location. The command repeats with a fresh dialog. Cancel ends it. |
| Spline | Click the control points. Enter or middle click finishes. |
| Ellipse | Drag or click a bounding rectangle. **[inf: two clicks like Rectangle]** |
| Flat spiral | Dialog (Stepover, Direction), then centre click and radius click. |
| Tangent | Hover over the first circle or arc, and the line end slides along it. Click to fix it. Hover over the second, and a triangle marks the nearest tangency point. Click on the triangle to finish. Turning grid snapping off makes this easier. |
| Measure (M) | Click two points. The result appears in a window. |

Edit modes (double-click an entity) [doc]:

| Entity | Edit behaviour |
|--------|----------------|
| Polyline / Line | Drag the square vertex handles. |
| Circle | Drag the centre handle to move it. Drag the rim to change the diameter. |
| Rectangle | Drag the corner handles. It stays rectangular. |
| Arc | Drag the centre, start and end handles. Shift flips the direction. |
| Spline | Drag the control points (red circles) and tangent handles (yellow squares). **Ctrl** while dragging a control point unlinks its tangent handles temporarily. |
| Point list | Drag points. Click empty space to add a point. |
| Text | Opens the text editor. |
| Script entity | Opens the script editor. |

In every edit mode, Enter or a middle click accepts and Esc cancels.

Prompted numeric input (Join tolerance, Offset distance, Arc Fit tolerance, Fillet radius, Array counts, Extrude height, …) uses a small modal input dialog with OK/Cancel. **[doc]** The last value used is remembered. **[prop]**

---

## 5. CAD objects and operations

### 5.1 Entity types and properties

Every entity has **Tag** (multi-line note text) and **Transform** (a 4×4 matrix with an editor: a Transformation drop-down (Rotate/Scale/Translate), an Axis, an amount, **Apply**, and **Identity** to reset; transforms stack). The Transform isn't baked into the geometry until *Apply Transformations* runs or Auto-Apply is on. Each entity also has an **ID** (read-only) and belongs to a layer. **[doc]**

| Entity | Specific properties | Notes |
|--------|--------------------|-------|
| **Polyline** | Closed (True/False). Points (collection of X, Y, Z, **Bulge**, where bulge = tan(sweep/4) and 0 = straight). | A closed flag is required even when the first and last points coincide. The [...] opens a point table. |
| **Line** | Points (straight only). | *New 1.0:* editable. |
| **Circle** | Center (X,Y,Z), Diameter. | |
| **Arc** | Center, Radius, Start (deg, 0 = +X), Sweep (deg, + = CCW). | |
| **Rectangle** | Lower Left, Width, Height, Corner Radius. | |
| **PointList** | Points (collection). | Drill source. Explode gives single points. |
| **Text** | Text (multi-line, [...] editor), Font, Height (em-square height in drawing units), Bold, Italic, Regular, Char Space (default 1), Line Space (default 1), Location (alignment point), Text Alignment Horizontal (Left/Center/Right), Text Alignment Vertical (Top/Center/Bottom, where Bottom = baseline). | TrueType outlines. **Text Curve Tolerance** (font units 0–2048) controls the flattening. Use Resize for an exact height. |
| **Region** | Outer shape plus holes. | Made with Convert To ▸ Region. |
| **Spline (NURBS)** | Control points, weights. **[inf]** | *New 1.0* drawing. **Spline to Polyline Tolerance** applies for CAM. 3D splines are supported. |
| **Surface (mesh)** | Triangles (from STL, 3DS, RAW, STEP, Extrude, Fill, From bitmap/text). | STEP is tessellated at **STEP Resolution** (default 0.05 mm). |
| **Bitmap** (*New 1.0*) | Source File (relative path when possible; BMP, GIF, EXIF, JPG, PNG, TIFF), Position (lower-left XYZ), Width, Height (default from DPI), Display Bitmap, Display Vectors, Invert (which edges are traced), Threshold (0 black … 1 white; transparent counts as white), Smoothing (spline-fit edges), Smooth Pixels (allowed error in pixels; 2 recommended, below 1 is very slow). | The file is linked, not embedded. Edge vectors are usable as MOP sources. Convert To ▸ Polyline breaks the link. |
| **Script object** (*New 1.0*) | Script, Execute On Update (auto-run when needed; False = run only with F5), Entities (generated list). | The original uses IronPython. Effective script = header + Drawing.Script + Layer.Script + Entity.Script. **[prop]** BamCam: C# scripting (Roslyn) or a Python host. Treat as post-MVP. |
| **Point** | Position. **[inf]** | Produced by exploding a point list. |

Point-list and polyline [...] editor [doc]: a table of X, Y, Z (plus Bulge for polylines). Del removes rows. Typing in the last `*` row appends a point. Tab-delimited copy and paste is supported (spreadsheet friendly).

### 5.2 CAD operations
All of Edit ▸ … and Edit ▸ Transform ▸ … in §2.2 and §2.3. Implementation notes **[prop]**:
- Use a robust 2D polygon-with-arcs library for offset, union, subtract, intersection and trim (e.g. Clipper2 on arc-flattened data, followed by arc fitting).
- Join must handle mixed lines, arcs and polylines and reverse segments as needed.
- Ops that replace entities give the results new IDs. Warn about any MOPs that referenced the old IDs.
- **Offset Backtrack Check** (config) cleans back-track glitches automatically before offsetting.

### 5.3 Layers
See §3.3.

### 5.4 Region fill patterns [doc]

| Pattern | Description |
|---------|-------------|
| Inside Offsets | Repeated offsets growing outwards from the holes (islands). |
| Outside Offsets | Repeated offsets moving inwards from the outer boundary (the classic pocket). |
| Inside + Outside Offsets | Union of the two above. |
| Horizontal Hatch | Parallel lines along X. |
| Vertical Hatch | Parallel lines along Y. |

Parameters: **Margin** (distance kept from the boundaries; the tool radius when pocketing) and **Step Over** (distance between lines). Used by Pocket and by 3D Waterline Rough.

---

## 6. CAM

### 6.1 Workflow [doc]
1. Draw or import geometry.
2. Select it and insert a MOP (toolbar, Machining menu, or by copy-pasting an existing MOP).
3. Generate toolpaths (Ctrl+T, or Part/MOP context menu) and inspect them (rotate the view, use cut widths, use the toolpath filter).
4. Choose a post processor (Machining ▸ Post Processor, otherwise the system default).
5. Produce G-code (Ctrl+W). The first time, it asks for a path (default: the drawing folder, drawing name plus **Default GCode Extension**, e.g. `.nc`). It asks before overwriting (config **Out File Prompt**: If Exists / Always / Never). It offers to create a missing output folder.

Default output folder order (*1.0rc*): folder of Part.OutFile, then Machining.OutFile, then the drawing file's folder, then the last browsed folder.

### 6.2 MOP property reference

#### 6.2.1 Properties common to all (or most) MOPs

| Property | Meaning | Default / Auto |
|----------|---------|----------------|
| Name | Descriptive name, written to G-code as a comment. | Auto-name (Profile1 …). |
| Enabled | False = no toolpaths and not posted. | True |
| Style | CAM style the Default values come from. | blank → Part → Machining → default style |
| Primitive IDs | IDs of the source entities (comma list; [...] = interactive select). Advanced view only. | from selection |
| Tool Number | Looks up the tool library. A change between successive MOPs causes a tool change. 0 = no tool change. | — |
| Tool Diameter | Diameter in drawing units. 0 = use the library value for Tool Number. | 0 → library |
| Tool Profile | EndMill / BullNose / BallNose / Vcutter / Drill / Lathe. Unspecified = from library. | Unspecified |
| Stock Surface | Z where machining starts (lathe: X radius). Auto = from the Stock object. | Auto (default style) |
| Target Depth | Z of the final depth. Auto = cut through the stock (stock surface − stock Z size). V-cutter Engrave Auto = one depth increment below the surface. | Auto (default style) |
| Depth Increment | Depth per pass, which sets the number of passes. | — |
| Final Depth Increment | Depth of the last pass (a light finishing cut). | — (tutorial: 0.1 mm) |
| Clearance Plane | Safe Z (offset from the work plane), clear of stock and clamps. | — |
| Cut Feedrate | Feed while cutting. | — |
| Plunge Feedrate | Feed while plunging. | — |
| Stepover Feedrate | Feed for crossover (stepover) moves. | — |
| Max Crossover Distance | Fraction (0–1) of the tool diameter. If the next path is further than this, retract, rapid and plunge instead of feeding across. | — |
| Spindle Speed | RPM. | — |
| Spindle Direction | CW / CCW / Off. | — |
| Spindle Range | Pulley or dial setting. | — |
| Velocity Mode | Default (use the Machining value) / Constant Velocity (G64) / Exact Stop (G61). | Default |
| Work Plane | XY / XZ / YZ. Arcs are output in this plane. | XY |
| Optimisation Mode | Toolpath ordering: New / Legacy / None. | New **[prop]** |
| Start Point | Point near which the first path begins. Shows a draggable marker. [...] = pick. | none |
| Roughing Clearance | Stock left for a later pass. Negative = oversize (inlays). | 0 **[prop]** |
| Roughing / Finishing | Used only by 3D Profile and Lathe. | Roughing **[prop]** |
| Custom MOP Header | G-code inserted before the MOP (`{$mop.header}`). | empty |
| Custom MOP Footer | G-code inserted after the MOP (`{$mop.footer}`). | empty |
| Transform | Experimental toolpath transform. Not on Lathe or NC File. | Identity |
| Tag | Free text. | empty |

#### 6.2.2 Applicability matrix

| Property | Profile | Pocket | Drill | Engrave | 3D Profile | Lathe | NC File |
|----------|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| Clearance Plane | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ (X radius) | – |
| Collision Detection | ✓ | ✓ | – | – | – | – | – |
| Corner Overcut | ✓ | – | – | – | – | – | – |
| Cut Ordering | ✓ | ✓ | – | – | ✓ | – | – |
| Cut Width | ✓ | – | – | – | – | – | – |
| Depth Increment | ✓ | ✓ | ✓ (spiral pitch) | ✓ | ✓ | ✓ (radial) | – |
| Final Depth Increment | ✓ | ✓ | – | ✓ | – | – | – |
| Finish Stepover (+ At Target Depth) | – | ✓ | – | – | – | – | – |
| Holding Tabs | ✓ | – | – | – | – | – | – |
| Inside / Outside | ✓ | – | – | – | – | – | – |
| Lead In / Lead Out Move | ✓ | ✓ | – | – | ✓ | – | – |
| Milling Direction | ✓ | ✓ | – | – | ✓ | – | – |
| Region Fill Style | – | ✓ | – | – | ✓ | – | – |
| Side Profile | ✓ | – | – | – | – | – | – |
| StepOver | ✓ | ✓ | – | – | ✓ | – | – |
| Stepover Feedrate | ✓ | ✓ | – | – | ✓ | – | – |
| Stock Surface / Target Depth | ✓ | ✓ | ✓ | ✓ | ✓ | Stock Surface only | – |
| Spindle* / Velocity Mode | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | – |
| Roughing Clearance | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | – |
| Transform | ✓ | ✓ | ✓ | ✓ | ✓ | – | – |
| Primitive IDs | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | – |

### 6.3 Value states and CAM styles [doc]

- Every MOP and style property is in one of three states:
  - **Default**: the value comes from the style chain and is shown in grey italics.
  - **Value**: an explicit override.
  - **Auto**: computed by the program from other data. Examples: Stock Surface and Target Depth from Stock; Hole Diameter from circle sizes; Target Depth for V-cutter engraving.
- Style resolution order for a MOP's Default value:
  1. The MOP's **Style**.
  2. The containing Part's **Style**.
  3. The Machining folder's **Style**.
  4. The **default style**, the one with the empty name.

  Within a style, unset values come from its **Parent Style**. If no parent is set, they come from the default (blank-name) style.
- **Style libraries** group styles (e.g. by material or units). Machining and Part have a **Style Library** property that can contain the macros `{$Material}` (stock material) and `{$Units}` (`mm`/`in`). If it's blank, libraries are searched in this order:
  1. `{$Material}-{$Units}` (when a material is set).
  2. `Standard-{$Units}`.
- **Property Cache Conflict**: the drawing caches the resolved value of every Default property. If, when the file is loaded or used, the resolved value differs from the cache (because a style or an Auto input changed), a dialog offers three choices, plus a **Use same action for all conflicts** checkbox that lasts until the file is closed:
  - **Use new value**.
  - **Use existing value**: turns the property into an explicit Value.
  - **Cancel current action**: keeps the Default but aborts the action.

  This makes drawings portable without their styles.
- A style has every MOP parameter (the union of all MOP types) plus **Parent Style**. Styles are edited in the System tab.
- **[prop]** Data model: `ParamValue<T> { State: Default|Value|Auto; T? Value; T? CachedResolved }`. The grid draws the glyph and the inherited value.

### 6.4 Profile [doc]

2.5D contour inside, outside, or alongside (open shapes) the selected shapes. Supports tabs, leads, side profiles and wide cuts.

| Property | Meaning |
|----------|---------|
| Inside / Outside | Which side to cut. For open shapes, the point order picks the side (use Polyline ▸ Reverse to flip it). |
| Cut Width | Total width of the cut. If wider than the tool, several parallel passes are made, spaced by StepOver. |
| StepOver | Fraction of the tool diameter between parallel passes. |
| Collision Detection | Prevents overlapping neighbouring toolpaths by unioning them. |
| Corner Overcut | Adds a move into inside corners (a dog-bone-style overcut) so mating parts fit. |
| Cut Ordering | Depth first, or level first (all shapes at one depth before going deeper). Captions **[prop]**: "Depth First" / "Level First". |
| Milling Direction | Conventional / Climb / Mixed. |
| Holding Tabs | Composite (§6.5). |
| Lead In Move / Lead Out Move | Composite (§6.6). |
| Side Profile | Composite (§6.7). |
| (all common properties) | §6.2.1 |

### 6.5 Holding Tabs (composite on Profile) [doc]

| Sub-property | Meaning |
|--------------|---------|
| Tab Method | None / Automatic / Manual (set automatically once a tab is dragged) / Automatic (Outer) / Automatic (Inner) (outer or inner region shapes only). |
| Tab Distance | Approximate spacing. Count = round(perimeter / distance), clamped to Min..Max. 0 = always Minimum Tabs. |
| Minimum Tabs / Maximum Tabs | Limits per shape. |
| Size Threshold | Shapes with a smaller perimeter get no automatic tabs. |
| Height | Tab height measured up from Target Depth. |
| Width | Width at the narrowest part of the tab. The displayed rectangle is wider by the tool size. |
| Tab Style | Square / Triangle / Skip (a rapid over the tab top, for plasma). |
| Use Lead Ins | With square tabs, adds the Profile's lead-in at the trailing edge instead of a vertical plunge. |

Interaction: tabs show as rectangles. Drag one to move it (Method → Manual). Right-click ▸ Holding Tabs ▸ Add Tab / Remove Tab / Autocalc. A red X marks a tab that can't be applied (e.g. on a corner) until toolpaths are regenerated. **Holding Tab Drag Toolpath Refresh** (config) re-applies tabs live while dragging. Machining-level **Inner Tab Scale** / **Outer Tab Scale** stretch the tab test rectangle towards or away from the toolpath, to fix narrow shapes.

### 6.6 Lead moves (composite: Lead In Move, Lead Out Move) [doc]

| Sub-property | Meaning |
|--------------|---------|
| Lead Move Type | None (plunge at Plunge Feedrate) / Spiral / Tangent. |
| Spiral Angle | Ramp angle. For Spiral, 0 = automatic: ramp down one depth increment over one lap, then do one flat lap at final depth (for a circle this gives a continuous helix). With an explicit angle, ramp until the depth is reached, then do a full flat lap. Shallow angles can take several laps. |
| Tangent Radius | Radius of the tangential arc entry or exit. Required for Tangent, which also honours Spiral Angle (helical arc). |
| Lead Move Feedrate | 0 = Cut Feedrate. |

Tangent leads on inside profiles with internal corners need a sensible Start Point. Machining **Fast Plunge Height** speeds up the drop to each level.

### 6.7 Side Profile (composite on Profile) [doc]

| Sub-property | Meaning |
|--------------|---------|
| Method | None / Slope (Value = angle from vertical) / Convex Radius / Concave Radius (Value = radius). |
| Value | Parameter for the method. Its sign flips the effect, together with the Inside/Outside setting. |
| Adjust Cut Width | False = follow the profile only (finishing). True = also clear all stock above the profile (roughing). |

### 6.8 Pocket [doc]

Clears the area inside the selected closed shapes. Nested shapes are detected automatically as islands, alternating pocket / island / pocket by nesting depth.

| Property | Meaning |
|----------|---------|
| StepOver | Fraction of the tool diameter between fill passes. |
| Region Fill Style | Horizontal Hatch / Vertical Hatch / Inside+Outside Offsets / Outside Offsets / Inside Offsets (§5.4). |
| Finish Stepover | Stepover used for the last (wall) pass. |
| Finish Stepover At Target Depth | True = finish stepover only at final depth. False = at every level. |
| Collision Detection, Cut Ordering, Milling Direction, Lead In/Out Move, Final Depth Increment | As for Profile. |
| (all common properties) | §6.2.1 |

### 6.9 Drill [doc]

Holes at point-list points or circle centres.

| Property | Meaning |
|----------|---------|
| Drilling Method | Canned Cycle (G81 plain / G82 with dwell / G83 peck) / SpiralMill_CW / SpiralMill_CCW / CustomScript. |
| Hole Diameter | Spiral-mill hole size. Auto = from the selected circles. |
| Depth Increment | Spiral pitch (depth per lap). |
| Spiral Flat Base | Adds a full flat circle at the bottom (False suits thread milling). |
| Drill Lead Out | Spiral only: move towards or away from the centre before retracting. |
| Lead Out Length | Distance for that move. + = towards the centre, − = away from it. |
| Peck Distance | Depth per peck. 0 = no pecking. |
| Retract Height | Retract level between pecks (the R word). |
| Dwell | Pause at the bottom. The time unit depends on the controller. |
| Custom Script | Per-hole G-code template, used when Method = CustomScript. Macros: `|` new line, `$c` clearance, `$d` hole diameter, `$f` plunge feed, `$h` hole Z, `$n` tool number, `$p` dwell, `$q` peck, `$r` retract, `$s` stock surface, `$t` tool diameter, `$x` `$y` hole XY, `$z` target depth. |
| (common) | §6.2.1. Roughing/Finishing isn't used. |

### 6.10 Engrave [doc]

Follows the selected curves exactly, adding each point's own Z to the depth, so 3D lines (heightmaps) are followed. Properties: the common set plus Depth Increment and Final Depth Increment. No offset or leads.

### 6.11 3D Profile [doc]

Machines triangle meshes (STL, 3DS, …). Supports waterline and scanline methods, roughing and finishing, front and back face, molds, boundary restriction, and an experimental additive mode.

| Property | Meaning |
|----------|---------|
| Profile 3D Method | Horizontal (scanlines along X) / Vertical (scanlines along Y) / Waterline Rough (pocket each Z slice to the boundary) / Waterline Finish (tool-offset contour per Z slice). |
| Roughing / Finishing | For scanline methods, Roughing = a pass at every depth increment and Finishing = the final depth only. |
| StepOver | Scanline spacing, or offset spacing for waterline rough (fraction of the tool diameter). Not used for waterline finish. |
| Resolution | Scanline sample spacing along each line (fraction of the tool diameter). |
| Depth Increment | Z step between levels. |
| Roughing Clearance | Stock left. Negative = oversize. |
| Region Fill Style | Fill pattern for Waterline Rough. |
| Boundary Method | Shape Outline / Bounding Box / Selected Shapes. |
| Boundary Shape IDs | IDs used with Selected Shapes. |
| Boundary Margin | Grows the boundary. Use > 0 with waterline + outline. Use −tool radius to confine scanlines to flat areas. |
| Boundary Taper | Angle from vertical for the boundary wall. |
| Clip Area Min / Clip Area Max | XY clipping rectangle. Both 0 = no clipping. |
| Start Corner | Scanline start corner. |
| Scanline Gradient Threshold | Suppresses scanline segments steeper than this angle (deg, 90 = vertical). Combine with a waterline finish. |
| Plane Slice Only | Simpler slicing for prismatic shapes without overhangs. |
| Back Face | Generates the back-face toolpath. |
| Back Face Zero Z | Model Z that becomes Z=0 after flipping. |
| Flip Axis | X or Y (the stock flip axis). |
| Back Face Culling | Ignores faces that point away (faster). Turn it off for meshes with inconsistent winding. |
| Mold | Generates a negative (mold) toolpath from a positive model. |
| Additive | Experimental: bottom-up extrusion toolpath starting at Stock Surface. |
| Auto Arc Fitting / Arc Fit Tolerance | Arc-fit the output (0 = auto tolerance). |
| Lead In/Out Move, Cut Ordering, Milling Direction, Stepover Feedrate | As for Profile. |
| (common) | §6.2.1 |

The tool profile affects scanline and waterline-finish results (ball and bull nose compensation). Waterline rough ignores it. The config option **Waterline Safety Check** blocks posting if a waterline path looks wrong. 3D holding tabs are made manually: extruded cylinders added as sources, with the boundary restricted to the main model.

### 6.12 Lathe (experimental, plugin in the original) [doc]

Drawing convention: draw only the profile line (open, one side of the axis). Lathe +X is drawn along drawing −Y, and lathe +Z along drawing +X. Machining Origin goes on the spin axis at lathe Z=0. Stock is a rectangular box: X size = length, Y and Z size = diameter, Stock Surface = radius, Stock Offset Y = −radius.

Checklist from the help:
- Work Plane = XZ.
- Stock Surface = stock radius.
- Clearance Plane > stock radius.
- Origin on the axis.
- Tool Diameter = 2 × nose radius.
- Tool Profile = Lathe.
- Roughing/Finishing chosen, with a small Roughing Clearance when roughing.
- A turning post (Mach3-Turn, EMC2-Turn).

| Property | Meaning |
|----------|---------|
| Lathe Cut Direction | Right Hand (+Z → −Z) / Left Hand (−Z → +Z). |
| Lathe Lead In Length / Lathe Lead Out Length | Length of the 45° approach and retreat moves (0 = off). |
| Depth Increment | Radial X step between roughing passes. |
| Roughing / Finishing | Roughing = straight passes per increment down to shape + clearance, then one contour pass. Finishing = one contour pass at the clearance distance. |
| Clearance Plane | Safe X, as a radius. |
| Stock Surface | Starting X, as a radius. Auto from Stock. |
| Tool Profile | Must be Lathe. |
| Work Plane | Always XZ. |
| (common) | Except Target Depth and Transform. |

Lathe-related post options are described in §6.16.

### 6.13 NC File (back-plotting) [doc]

Created by opening a G-code file. Displays its toolpaths and includes its content verbatim in the drawing's output. Double-click opens the source in the G-code editor. The context menu has **Toolpath To Geometry**. Only basic G-code is parsed (no subroutines).

| Property | Meaning |
|----------|---------|
| Source File | G-code file path. |
| Distance Mode | Absolute / Relative, for X/Y/Z. |
| Arc Center Mode | Absolute / Relative, for I/J. |
| Name, Enabled, Style, Tag, Start Point, Cut/Plunge Feedrate, Max Crossover Distance, Optimisation Mode, Tool Diameter/Number/Profile, Work Plane, Custom MOP Header/Footer | As common. |

### 6.14 Machining (folder) properties [doc]

| Property | Meaning |
|----------|---------|
| Arc Center Mode | I/J convention: Default (use the post's setting; a post default means Incremental C-P1) / Absolute / Incremental (C-P1) / Incremental (P1-C) / Incremental (C-P2) / Incremental (P2-C). |
| Custom File Header / Custom File Footer | Text at the start and end of the file. Multi-line or `|` separated. Macros allowed. |
| Fast Plunge Height | Rapid down to this height above the stock or the last cut level, then plunge at feed. 0 = always plunge at feed. **Default −1 = one minor grid unit.** |
| Holding Tabs: Inner Tab Scale / Outer Tab Scale | Tab test-rectangle scaling (§6.5). |
| Machining Origin | Drawing point that becomes G-code X0 Y0. [...] picks it in the view. A draggable X marker shows it. |
| Number Format | Numeric output format (overridden by the post's Number Format). |
| Out File | Destination G-code path ([...] opens a browser). |
| Post Processor | Drop-down of posts. Blank = system default. |
| Post Processor Macros | Lines of the form `$name=value`, passed to the post. |
| Rebuild Toolpath Before Post | Always / Prompt / If Needed. |
| Stock | Composite: **Material** (text), **Stock Offset** (XY of the lower-left corner), **Stock Surface** (Z of the top), **Stock Size** (X,Y,Z; all 0 = undefined), **Color**. |
| Style | Default CAM style for the drawing. |
| Style Library | Library lookup (macros `{$Material}`, `{$Units}`). |
| Tool Library | Blank = `Default-{$Units}`. |
| Tool Number / Tool Diameter / Tool Profile | Drawing-wide default tool. |
| Velocity Mode | Constant Velocity / Default / Exact Stop. Global value. Default = write nothing. |

Note: display properties (Show Cut Widths, Show Rapids, …) moved to the root Drawing node in 0.9.8.

### 6.15 Part properties [doc]

| Property | Meaning |
|----------|---------|
| Enabled | Include this Part's enabled MOPs in display and output. |
| Name | Also used in default output file names. |
| Machining Origin | Part-level override of the origin. |
| Out File | Part-level output path. |
| Stock | Part-level stock. It overrides the Machining stock for this Part's MOPs. |
| Style / Style Library | Part default style and lookup. |
| Tool Library | Overrides the Machining value. |
| Tool Number / Tool Diameter / Tool Profile | Part default tool. Overrides Machining. A MOP's own non-zero value wins. |
| Tag | Notes. |
| Nesting | Composite: **Nest Method** (None / Grid / ISO Grid / Manual / Point List), **Rows**, **Columns**, **Spacing** (gap between the outermost toolpaths of neighbouring copies), **Grid Order** (e.g. Right Up, …: the first direction, then the next row), **Grid Alternate** (zig-zag rows or columns vs. rapid back to the start), **Point List ID** (point list entity, for Point List), **GCode Order** (Auto / Nest Each MOP / All MOPs Per Copy), and the **nesting item collection** (Advanced view, Manual method: a list editor with Add, Delete, Up/Down reorder, and per-copy X/Y and rotation **[prop]**). |

Nesting behaviour:
- ISO Grid shifts every other row or column by half a pitch.
- Copies are visible only while the Part is selected (and Show Nests is on).
- Dragging a copy's red triangle switches the method to Manual.
- Only the copies are output. A moved "original" isn't machined.
- Disabled MOPs don't count towards the spacing.
- **Nest to Point List** (Part context menu) turns the current copies into a point-list entity, so several Parts can share one layout.
- GCode Order:
  - Auto: consecutive MOPs with the same tool are posted for every copy, then the next tool.
  - Nest Each MOP: each MOP at every copy.
  - All MOPs Per Copy: the whole Part at copy 1, then copy 2, and so on.
- Copies are written out in full, not as subroutines.
- 4th-axis trick: a point list of N points all at 0,0, plus a rotation command in the last MOP's footer.

### 6.16 Tool libraries [doc]

- Several libraries can exist, e.g. `Default-mm`, `Default-in`, `Lathe-mm`. Lookup order for a MOP's tool number:
  1. The Part's Tool Library.
  2. The Machining Tool Library.
  3. `Default-{units}`.
- Explicit values on a MOP (diameter, profile) win over the library values. A tool number with no library entry is allowed, as long as the MOP sets the diameter and profile itself.
- Default tool cascade: Machining tool → Part tool → the MOP's own non-zero Tool Number.
- Library property: **Tool Name Format**, with macros `{$diameter}`, `{$flutes}`, `{$index}`, `{$length}`, `{$partcode}`, `{$profile}`, `{$veeangle}`. Example: `{$diameter} mm - {$profile} - z={$flutes}`. Names update automatically when a tool changes. **Rename all tools** re-applies the format.

Tool properties:

| Property | Meaning |
|----------|---------|
| Index | Tool number (unique in the library). Used in T words. |
| Name | Shown in tool drop-downs. Can be generated from the format. |
| Diameter | Cutting diameter used for offsets. For V-cutters, the diameter at the typical depth. |
| Tool Profile | End Mill / Bull Nose / Ball Nose / V-Cutter / Drill / Lathe. |
| Tool Change | Custom tool-change G-code for this tool. Overrides the post's Tool Change section. |
| Comment | Text for `{$tool.comment}`. |
| Axial Depth Of Cut, Coating, Flute Length, Flutes, Helix Angle, Length, Material, Max Ramp Angle, Notes, Part Code, Radial Depth Of Cut, Shank Diameter, Tooth Load, Vee Angle | Informational. Flutes is used by the speeds and feeds calculator. Length goes to `{$tool.length}`. Vee Angle goes to `{$tool.veeangle}`. |

Libraries save to disk with **Save to XML**, Tools ▸ Save settings, or on exit.

### 6.17 Post processor system [doc]

A post is a set of named text templates mixing literal text and `{$macro}` expressions. It's edited in the property grid of the System tab (Advanced view). Sections, grouped as the help groups them:

- **(Main)**
  - **Post File**: overall layout, normally `{$header}`, `{$mops}`, `{$footer}` plus literals such as `%` and `O{$o}`.
  - **Header**: what `{$header}` expands to.
  - **Footer**: what `{$footer}` expands to.
  - **MOP**: per-MOP block, e.g. comment, toolchange, velocity mode, workplane, `{$mop.header}`, spindle, `{$blocks}`, `{$mop.footer}`.
  - **Start Cut** / **End Cut**: emitted when a feed move goes below, or comes back above, the stock surface (laser/plasma on/off).
  - **Post Processor Macros**: default values for user macros. The drawing's Post Processor Macros override them.
- **Tools**
  - **Tool Table Item**: format of each line in `{$tooltable}`.
  - **Tool Change**: what `{$toolchange}` expands to.
- **G Codes**
  - G0, G1, G2, G3, G81, G82, G83.
  - Arc Center Absolute (e.g. G90.1), Arc Center Incremental (G91.1).
  - Canned Cycle Start (G98), Canned Cycle End (G80).
  - Cutter Comp Off/Left/Right (G40/G41/G42).
  - Distance Absolute/Incremental (G90/G91; incremental output is unsupported).
  - Units (Inches)/(Metric) (G20/G21).
  - Velocity Mode – ExactStop/Constant Velocity (G61/G64).
  - Workplane XY/XZ/YZ (G17/G18/G19).
  - X Mode Diameter (G7) / X Mode Radius (G8).
- **M Codes**: End Rewind (M30), Repeat (M47), Spindle CW/CCW/Off (M3/M4/M5), Stop (M0).
- **Moves**: Rapid, Feed Move, Arc CW, Arc CCW (templates such as `{$g0} {$_f} {$_x} {$_y} {$_z} {$_a} {$_b} {$_c}`).
- **Canned Cycles**: Drill, Drill Dwell, Drill Peck (templates using `{$g81..83} {$_x} {$_y} {$_z} {$p} {$_q} {$_r} {$_f}`).
- **Lathe**: Lathe Tool Radius Offset (output the virtual tool point instead of the nose centre), Lathe X Mode (Radius/Diameter).
- **Line Numbering**: Add Line Numbers, Line Number Format (`0` = digit or zero, `#` = digit or space), Line Number Increment, Line Number Prefix (e.g. N), Line Number Skip (leading characters that suppress numbering), Line Number Space After, Line Number Start.
- **Options**:
  - Arc Output (Normal / Convert To Lines / Helix Convert To Lines).
  - Arc Center Mode (as Machining).
  - Arc To Lines Tolerance.
  - Clearance Plane Axis (Z, or X for lathe).
  - Comment / End Comment (e.g. `(` `)` or `;`).
  - End Of Line (`\r\n` escapes).
  - Invert Arcs (XZ arcs only; True for lathe).
  - Minimum Arc Length (shorter arcs become G1, e.g. 1e-4).
  - Maximum Arc Radius (larger arcs become G1, e.g. 1e6).
  - Number Format (e.g. `#.#0`; moves that don't change after formatting are dropped).
  - Rapid Down To Clearance.
  - Suppress Parser Errors.
  - Upper Case.
- **Post Build**: Post-Build Command and Post-Build Command Args (macros `{$outfile}`, `{$cbfile.name}`). Runs an external program on the output.
- **Rotary** (*New 1.0*): Axis Of Spin, Rotary Axis (letter), Rotary Wrap. Wrap converts the paths to lines and wraps them around the axis, using each MOP's Stock Surface as the radius.

Macro catalogue (all documented):
- Arcs and moves:
  - `$arc.i/j/k`, `$arc.radius` (negative if the sweep is over 180°), `$arc.start/sweep/end`, `$arccentermode`.
  - `$move.x/y/z`.
  - Register macros `$x $y $z $a $b $c $i $j $f $r $p $q`. They include the letter. With an underscore prefix (`$_x`) they're modal (written only when the value changes).
  - Variants `$xneg …` (sign flipped) and `$xabs …` (absolute value).
  - G-code word macros `$g0 $g1 $g2 $g3 $g81 $g82 $g83` (and the modal `$_g…` forms).
- Structure and file:
  - `$blocks`, `$header`, `$footer`, `$mops`.
  - `$comment`, `$endcomment`.
  - `$cbfile.header/footer/name`, `$date`.
- Motion and state:
  - `$check(x,y,z)`: inserts the moves needed to reach a position safely.
  - `$clearance`.
  - `$cuttercomp(off|L|R)`, `$distancemode`.
  - `$endrewind`, `$repeat`, `$stop`.
  - `$set(reg,value)`: updates an internal register without writing output. NaN = unknown.
  - `$spindle`, `$spindle(off|cw|ccw)`, `$s`.
  - `$units`, `$velocitymode`, `$workplane`, `$lathexmode`.
- Current MOP and Part:
  - `$mop.clearanceplane`, `$mop.cutfeedrate`, `$mop.depthincrement`, `$mop.dwell`.
  - `$mop.first.x/y/z`.
  - `$mop.header/footer`, `$mop.holediameter`, `$mop.name`, `$mop.peckdistance`, `$mop.plungefeedrate`, `$mop.retractheight`, `$mop.stocksurface`, `$mop.tag`, `$mop.targetdepth`.
  - `$part.name`.
- Stock: `$stock.xsize|width|_width`, `$stock.ysize|length|_length`, `$stock.zsize|height|_height`, `$stock.xoffset`, `$-stock.xoffset|stock_x`, `$stock.yoffset`, `$-stock.yoffset|stock_y`, `$stock_z`.
- Tools:
  - `$post.toolchange`, `$toolchange`, `$toolchange(first)`, `$tooltable`.
  - `$tool.comment`, `$tool.diameter` (defined only after a tool change), `$tool.index`, `$tool.length`, `$tool.name`, `$tool.profile`, `$tool.radius`, `$tool.veeangle`.
- User macros come from the drawing's Post Processor Macros (`$o=1234`).

Post management: posts live as XML files in `<System>/post`. You can create, copy, paste, rename or delete them, choose one with **Set As Default**, and refresh with Tools ▸ Reload Post Processors. Bundled posts named in the help: Default, Mach3, Mach3-CV (CutViewer), Mach3-Turn, Mach3-Turn-CV, EMC2-Turn, RotaryX, RotaryY. **[prop]** BamCam MVP ships Generic (RS274/LinuxCNC), Mach3, GRBL, and a turning variant.

### 6.18 G-code generation pipeline [inf/prop]

1. Resolve every MOP's parameters (style chain, Auto values, cache conflicts).
2. Generate toolpaths per MOP. These are polylines with Z, plus rapids, leads, tabs and nest copies.
3. Order MOPs by tree order, Part by Part (enabled only), applying GCode Order for nests.
4. Use the Machining Origin (Part overrides Machining) to translate coordinates.
5. Expand the post templates. Tool changes happen when the tool number changes (0 = no tool change). The tool table lists every referenced tool.
6. Write the file. Number format, line numbers, case, EOL and arc conversion apply. Then run the Post-Build command.
7. Output scope: whole drawing (Machining), single Part, or single MOP (file `Drawing.Part.MOP.nc`).

### 6.19 Toolpath display [doc]
- The toolpath is the path of the tool's tip centre. Line moves are blue and arc moves green (configurable). Rapids are dotted red. Small arrows show the direction, and an optional start arrow appears (Show Direction Vector).
- Show Cut Widths shades the swept area in the Cut Width Color (top view only).
- Toolpath Visibility: All, or Selected Only.
- Disabled MOPs and Parts hide their toolpaths.
- **Toolpath View Filter** (Ctrl+F, modeless):
  - **Toolpath index** checkbox with a number: shows paths up to N in execution order. Path N is highlighted, earlier paths use the "cut" colour, later paths are hidden.
  - **Z depth index** checkbox with a number: shows all paths at Z level N. Both checkboxes together filter by index down to a maximum Z.
  - **Cut toolpath color** (show or hide earlier paths) and **Toolpath color** (highlight colour on or off), each with a colour swatch.
  - Line width and alpha.
  - Respects Toolpath Visibility.

### 6.20 Simulation
The original has no built-in material-removal simulation. It relies on the Toolpath View Filter and the external CutViewer (Tools ▸ Simulate with CutViewer, using CutViewer-aware posts and stock macros). **[prop]** BamCam phase 2: a built-in heightfield (Z-buffer) stock simulator, with an optional external simulator command in config.

### 6.21 Speeds and Feeds calculator [doc]
Opened from the MOP context menu and pre-filled from the MOP (diameter, feed, RPM) and the tool library (flutes). Fields: **Num flutes**, **Diameter**, **Tooth Loading**, **Surface Speed**, **Feedrate**, **RPM**. Five "calculate" buttons, each showing its formula:
1. Tooth load from feed, RPM and flutes.
2. Surface speed from RPM and diameter.
3. Feed from RPM, tooth load and flutes.
4. RPM from feed, tooth load and flutes.
5. RPM from surface speed and diameter.

Standard relations: RPM = Vc·1000/(π·D) in metric. Feed = RPM·z·fz. Results aren't written back to the MOP (the user copies them over).

### 6.22 G-code editor [doc]
- Machining ▸ Edit Gcode opens the top-level output file only.
- External editor via the **GCode Editor** config (e.g. a path to a text editor). Otherwise an internal editor is used.
- **[prop]** Internal editor features: syntax highlighting, line numbers, find. Saving can optionally back-plot the result.

---

## 7. Keyboard shortcuts

| Keys | Action |
|------|--------|
| Ctrl+A | Select all (visible) objects |
| Shift+Ctrl+A | Select all objects on the active layer |
| Ctrl+B | Break at intersections |
| Ctrl+C | Copy |
| Ctrl+E | Resize selected objects |
| Ctrl+F | Toolpath view filter window |
| Ctrl+G | Toggle snap to grid |
| Ctrl+I | Convert to region |
| Ctrl+J | Join |
| Ctrl+L | Align window (*1.0rc3*) |
| Ctrl+M | Move selected objects |
| Ctrl+O | Open file |
| Ctrl+P | Convert to polylines |
| Ctrl+R | Rotate selected objects |
| Ctrl+S | Save |
| Ctrl+T | Generate (regenerate) all toolpaths |
| Ctrl+U | Union |
| Ctrl+V | Paste |
| Shift+Ctrl+V | Paste format |
| Ctrl+W | Produce G-code file |
| Ctrl+X | Cut |
| Ctrl+Y | Redo |
| Ctrl+Z | Undo |
| Ctrl+Tab (help also says Alt+Tab) | Toggle tree focus to the selected drawing object. **[prop]** Use Ctrl+Tab only, since Alt+Tab belongs to the OS. |
| Space | Hide/unhide selected layers. Enable/disable selected Parts and MOPs. |
| Ctrl+Space | Lock/unlock selected layers (*New 1.0*) |
| A | Draw arc |
| C | Draw circle. While drawing a polyline: close it. |
| D | Draw point list |
| M | Measure |
| P | Draw polyline |
| R | Draw rectangle |
| T | Insert text |
| X / Y / Z (during Rotate) | Choose the rotation axis |
| Cursor keys | Pan the view |
| Shift+Cursor | Move selection by one minor grid unit |
| Shift+Ctrl+Cursor | Move selection by one major grid unit |
| Page Up / Num − | Zoom out |
| Page Down / Num + | Zoom in |
| Home | Reset view |
| Alt+double-click | Reset view (Zoom to fit, XY) |
| Alt+left drag | Rotate view (default Rotation Mode) |
| Middle drag | Pan |
| Wheel | Zoom at the cursor |
| Shift+left drag (on selection) | Move selection freehand |
| Shift + rotate gesture | Freehand-rotate selection about the origin |
| Shift (while drawing/editing an arc) | Flip arc direction |
| Shift (during Mirror) | Constrain the mirror line angle |
| Ctrl+click (during Move) | Add a source point (up to 3) |
| Ctrl (dragging a spline control point) | Break the tangent link |
| Enter / middle click | Finish entity, accept edit, or apply MOP object selection |
| Esc | Cancel the current command or edit |
| Del | Delete selected objects, layers or MOPs |
| F1 | Help |
| F2 | Rename the selected tree item |
| F5 | Run the selected script object |

Conflict notes: Ctrl+R means Rotate (the entity page's "Ctrl+R = region" is wrong, so region is Ctrl+I). The shortcut table pairs PgDn with zoom in, which is unusual. **[prop]** Keep it for fidelity, but make every key rebindable.

---

## 8. Configuration (Tools ▸ Options / System ▸ Configuration)

The help lists these as a flat property grid. **[prop]** BamCam groups them into categories as below.

**Display**
- Arc Display Degrees: angle per display segment for arcs.
- Backface Culling: hide back-facing mesh triangles.
- Display Mode: OpenGL / OpenGL_Legacy / GDI. Needs a restart. Holding Shift at start-up forces the safe mode. **[prop]** BamCam: GPU / Software.
- GLSL Shader Version: 110 / 330.
- Spline Curve Steps: display segments per spline.
- View 3D Wireframe: show meshes as wireframe.
- View Projection Mode: Orthographic / Perspective.
- View Focal Length Scale: perspective depth factor.
- Select Fade: % fade of unselected objects.
- Use Surface Selection Color: tint selected meshes.
- Show Grid: grid on or off.
- Selected Entity Focus (*1.0rc3*, default True): auto-expand the tree to the selected entity.
- Menu Font Family / Menu Font Size (Mono only).
- Windows opacity (View menu, not stored in config). **[inf]**

**Colors**
- Cut Width Color, Grid Color, Select Color, Toolpath Arc Color, Toolpath Line Color, Toolpath Rapid Color, View Background Color, View Text Color.
- Default Layer Color, Default Stock Color.

**Grid**
- Grid Info (Metric) and Grid Info (Inches): Drawing Units, Minimum, Maximum, Major Scale, Minor Scale.

**Drawing / CAD**
- Drawing Units: units for new drawings.
- Drawing Template: `.cb` used as the template for new and imported drawings.
- Default Font Family: font for text without one.
- Default Layer Pen Width (*1.0rc1*).
- Default Script Entity: script inserted into new script objects.
- Auto-Apply Transformations: bake transforms automatically.
- Arc Fit Tolerance (0 = auto) and Auto Arc Fitting: for operations such as plane slice.
- Offset Backtrack Check: remove polyline back-tracks before offsetting.
- Spline to Polyline Tolerance: CAM conversion accuracy for splines.
- Text Curve Tolerance: glyph flattening (font units).
- Snap to Grid / Snap to Points.
- Repeat Commands: draw commands repeat until Esc or middle click.
- Rotation Mode: ALT+Left / Left+Middle / Left+Right.

**CAM**
- Default GCode Extension (e.g. `.nc`).
- GCode Editor: external editor command. Blank = internal.
- Out File Prompt (*1.0rc2*, "Gcode Output"): If Exists / Always / Never.
- Holding Tab Drag Toolpath Refresh: re-apply tabs while dragging.
- Waterline Safety Check: block posting of suspicious waterline paths.
- Worker Threads: number of parallel calculation threads.

**Import / Export**
- DXF Options – Export 3D Polylines (*New 1.0*).
- Gerber – Flatten: union all layers into one.
- Gerber – Subtract Layers: "clear" layers subtract from earlier layers.
- Gerber – Union Layers: union the shapes on each layer.
- Gerber – Union Traces: union the traces (False = keep them as lines and arcs).
- STEP Resolution (default 0.05 mm): tessellation tolerance.
- File Open Worker Thread (default True on Windows): open files in the background.

**Files / System**
- File Backups: number of `.b#` backups kept.
- System Path: root of the libraries. Macros `{$common}` (all-users app data) and `{$user}` (user app data). **[prop]** Default `{$user}/BamCam`.
- Language: UI language (needs a restart).
- Check Version At Start.
- Diagnostic Level: message verbosity, 0–4.
- Thinking Message: busy text.

**Printing** (*New 1.0*)
- Print Color, Border Size, Margins, Page Orientation, Line Width Scale.

Tools-menu toggles: Save settings on exit.

---

## 9. MVP priority

**P0: core (first usable release)**
1. Main window shell: menu, toolbar, Drawing/System tabs, tree, property grid with categories and Basic/Advanced views, message pane, status bar with coordinates, prompt line. Undo/redo framework.
2. Drawing view: OpenGL 3D (ortho), grid and axes, pan/zoom/rotate per §4.3, XY/XZ/YZ/Reset/Fit, click/Ctrl/window selection, grid and point snapping.
3. Entities: Line, Polyline (with bulge), Arc, Circle, Rectangle, PointList, Text (TTF outlines), Region. Their properties and Transform matrix. Double-click edit for polylines and points.
4. Layers: active/visible/locked/colour, plus the context menu. Drag and drop in the tree.
5. CAD ops: Join, Offset, Explode, Convert to Polyline/Region, Union/Subtract/Intersect, Break at intersections, Polyline Reverse/Clean/Set start point. Transforms: Move, Rotate, Resize, Align, Mirror, Array, Center, Apply.
6. I/O: native format, DXF import/export, G-code export. STL import for display.
7. CAM: Machining/Part/MOP tree. **Profile** (inside/outside, depth passes, cut width, tabs automatic and manual, lead in/out), **Pocket** (offset fills and islands), **Drill** (canned cycles and spiral), **Engrave**. Stock, Machining Origin, Start Point, toolpath display (lines/arcs/rapids/arrows/cut widths), Generate Toolpaths, Produce G-code for drawing/Part/MOP.
8. Styles: Default/Value/Auto states, inheritance chain, default style, style library in the System tab. Tool libraries.
9. Post processor engine with the documented template sections and macros. Ship Generic RS274, Mach3 and GRBL posts.
10. Options dialog with the Display, Colors, Grid, Drawing and CAM groups.

**P1: important**
- Toolpath View Filter, Speeds and Feeds calculator, NC File back-plot, Toolpaths to Geometry.
- Side Profile, Corner Overcut, Collision Detection, Finish Stepover, hatch fill styles, Paste Format, Reset to defaults, Property Cache Conflict dialog.
- Nesting (Grid, ISO Grid, Point List, Manual, GCode Order), Nest to Point List.
- Remaining CAD: Trim, Fillet, Open Offset, Arc Fit, Remove overlaps, Break at points, Point List generators, Region Fill, Polar Array, 3-point Move, Measure, Extremas.
- Edit modes for circle, arc and rectangle. Spline drawing.
- Print.

**P2: advanced / later**
- 3D Profile (waterline and scanline, back face, mold), mesh ops (plane slice, silhouette, project, extrude, fill), STEP and 3DS import, STL export.
- Lathe MOP and turning posts, rotary wrap.
- Bitmap entity (vectorise and smooth), heightmap surface.
- Script objects, plugin API.
- Gerber import, line numbering, post-build command, built-in material-removal simulator, localisation, auto-update check.
