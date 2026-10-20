# BamCam – File Format Specification (CamBam interoperability)

Status: derived **only** from data files shipped with / produced by CamBam plus 1.0
(`C:\ProgramData\CamBam plus 1.0\...`) and from the semantic descriptions in the shipped HTML
help. No binaries were inspected. All prose is our own wording.

Evidence corpus:

| Kind | Files |
|---|---|
| Drawings (`.cb`) | `samples\`: C-BEAM-Belt-Drive, CamBam, continuous-spiral-feed, gear-script, heart-shaped-box, klannimate, lathe-test, opto_input.pcb.output_back, side profiles, skull-big-foam, snap-together-box, solids-example; `templates\`: laser, nameplate, plasma (15 files) |
| G-code | `samples\snap-together-box.nc` (generated from `snap-together-box.cb`) |
| Post processors | `post\*.cbpp` (21 files) |
| Libraries | `styles\Standard-mm.xml`, `styles\Standard-in.xml`, `tools\Default-mm.xml`, `tools\Default-in.xml`, `tools\Lathe-mm.xml` |
| Config | `CamBam1.0.config`, `ExcellonIO.config` |

Legend used below:

* **[obs]** – observed in the corpus (exact name/value).
* **[help]** – the concept is described in the help, but the exact XML spelling was **not** observed.
  Any XML name given for a [help] item is a *guess* and is marked **UNVERIFIED**. A parser must not
  depend on it; a writer must not emit it unless a real CamBam file confirms it.
* `n=` occurrence count across the corpus, `f=` number of files containing it.

General rule for BamCam: **round-trip unknown content.** Every element/attribute/text node not
modelled must be preserved (kept as raw XML in the owning object and re-emitted in place).

---

## 0. Common lexical conventions (all CamBam XML files)

### 0.1 Encoding / layout

| File type | Encoding | BOM | Newlines | Indentation | Trailing newline |
|---|---|---|---|---|---|
| `.cb` | UTF-8 (declared `<?xml version="1.0" encoding="utf-8"?>`) | **no** | CRLF | 2 spaces per level | **no** (file ends with `</CADFile>`) |
| `.cbpp` | UTF-8 | no | CRLF | 2 spaces | no |
| `styles\*.xml`, `tools\*.xml` | UTF-8 | **yes** (EF BB BF) | CRLF | **none** (every element starts at column 0) | no |
| `.nc` output | ASCII | n/a | CRLF | n/a | **yes** (last line `M30\r\n`) |

Self-closing empty elements are written as `<Name />` (space before slash), e.g. `<mat m="Identity" />`,
`<OutFile />`, `<objects />`, `<parts />`.

The `.cb` and `.cbpp` roots carry `xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"` and
`xmlns:xsd="http://www.w3.org/2001/XMLSchema"`. Attribute order varies (`laser.cb` has xsd before xsi),
so a parser must be order-insensitive; a writer should emit `xmlns:xsi`, `xmlns:xsd`, then `Version`, `Name`, `units`.

### 0.2 Numbers

* Always invariant culture: `.` decimal separator, no thousands separator, `-` sign.
* Exponent form occurs: `-2.8908581599591E-07`, `-4.37113882867379E-08`, `1.22460635382238E-16`,
  `-1.05836760413361E-14` (mantissa `E`, sign, at least 2 exponent digits). Parsers must accept it.
* Two precisions are visible:
  * **Composite strings** (point lists `x,y,z`, `mat m="..."`, stock points, `c=`, `p1=`, `p=`): up to
    **15 significant digits** (e.g. `110.344500682117`, `0.707106781186548`, `141.949996948242`).
  * **Scalar double values** serialized individually (circle `d`, rect `w`/`h`, `ParametricPoint`,
    `Normal/X`, `Normal/Y`): **round-trip precision, up to 17 significant digits**
    (e.g. `d="12.891874375741431"`, `w="34.766340912226937"`, `h="12.699999999999989"`,
    `0.01483519531268529`).
  * Writer recommendation: composite strings → `ToString("G15", Invariant)`; scalars →
    `ToString("R", Invariant)` (or .NET Core shortest round-trip). Integers are written without decimals (`3`, `800`, `-13`).
* Negative zero appears literally: `<X>-0</X>` (HoldingTab normal). Accept it.

### 0.3 Booleans

Two spellings coexist and must be reproduced per element:

| Spelling | Where |
|---|---|
| `true` / `false` (lower case) | attributes (`Closed="true"`, `visible="false"`), plain elements `ShowCutWidths`, `ShowStock`, `ShowRapids`, `ShowDirectionVector`, `Enabled`, `ExecuteOnUpdate`, `NormalInverted`; `.cbpp` `UpperCase`, `InvertArcs`, `RotaryWrap`, `AddLineNumbers`, `SuppressParserErrors`; config file |
| `True` / `False` (capitalised) | values of state-wrapped MOP/style properties (`<CollisionDetection state="Default">True</CollisionDetection>`, `CornerOvercut`, `DrillLeadOut`, `SpiralFlatBase`, `Mold`, `Additive`, `BackFace`, `BackFaceCulling`, `PlaneSliceOnly`, `FinishStepoverAtTargetDepth`) and sub-elements of composite properties (`HoldingTabs/UseLeadIns`, `SideProfile/AdjustCutWidth`) |

Parsers: case-insensitive. Writers: follow the table.

### 0.4 Colors

* Format `R,G,B` decimal bytes, no spaces: `color="127,255,0"`, `<Color>255,165,0</Color>`.
* A 4-component form also occurs: `<Color>0,0,0,0</Color>` (part-level Stock, meaning "unset / transparent"),
  and in config `<CutToolpathColor>255,0,0,120</CutToolpathColor>`. The component order of the 4-value
  form cannot be proven from the data: read as `A,R,G,B` the config value would be opaque dark blue, read as
  `R,G,B,A` it would be translucent red (more plausible for a "cut toolpath" overlay). **Treat 4-value colors
  as opaque strings and round-trip them unchanged**; `0,0,0,0` means "unset". For display, prefer `R,G,B,A`
  (UNVERIFIED).

### 0.5 Points

| Shape | Format | Seen in |
|---|---|---|
| 3D, always 3 values | `x,y,z` | `pline/pts/p`, `rect/pts/p`, `circle@c`, `text@p1`, `text@p2`, `rect@p`, `StartPoint`, `surface/verts/v` |
| 3D with Z omitted when Z==0 | `x,y` or `x,y,z` | `points/pts/p` (81× 2-comp, 14× 3-comp with tiny Z), `line/pts/p` (2-comp), `Stock/PMin`, `Stock/PMax` |
| 2D | `x,y` | `MachiningOrigin`, `Nesting/BasePoint`, `NestingItem/Location`, `ClipAreaMin`, `ClipAreaMax` |
| Empty = "not set" | `<StartPoint state="Default" />`, `<BasePoint />` | |

Parsers: split on `,`, accept 2 or 3 components (missing Z = 0). Writers: reproduce the per-element rule above.

### 0.6 Transform matrix (`m` attribute)

* `m="Identity"` for the identity matrix (by far the most common).
* Otherwise 16 space-separated numbers, **row-major, row-vector convention (translation in the 4th row)**:
  `M11 M12 M13 M14 M21 M22 M23 M24 M31 M32 M33 M34 M41 M42 M43 M44`.
  * Pure translation: `1 0 0 0 0 1 0 0 0 0 1 0 2.5 0 0 1` → translate (2.5, 0, 0).
  * Rotation +45° about Z plus translation: `0.707106781186548 0.707106781186547 0 0 -0.707106781186547 0.707106781186548 0 0 0 0 1 0 160.5 40 0 1`
    → `x' = x·M11 + y·M21 + M41`, `y' = x·M12 + y·M22 + M42` (counter-clockwise rotation).
  * Rotation about Y by 180°: `-1 0 -1.22460635382238E-16 0 0 1 0 0 1.22460635382238E-16 0 -1 0 -24 40 0 1`.
  * Non-uniform scale: `1.29999995231628 0 0 0 0 1 0 0 0 0 1 0 0 0 0 1` (scale X by 1.3).
  * Rotation -90° about Z: `-4.37113882867379E-08 -1 0 0 1 -4.37113882867379E-08 0 0 0 0 1 0 0 0 0 1`.
* The stored entity geometry is **untransformed**; the matrix is applied on top (the help states transformations may
  be kept un-applied, e.g. when rotating a circle out of plane).
* Same encoding is used by `NestingItem/Transform`.

---

## 1. `.cb` drawing file schema

### 1.1 Overall skeleton

```xml
<?xml version="1.0" encoding="utf-8"?>
<CADFile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" Version="0.9.8.0" Name="snap-together-box">
  <layers> ... </layers>
  <MachiningOptions> ... </MachiningOptions>
  <parts> ... </parts>
  [view flags: ShowCutWidths | ShowDirectionVector | ShowRapids | ShowStock | ToolpathVisibility | ToolpathVisiblility]
  [drawing-level script: mixed TEXT node]
  <ActiveLayer>Default</ActiveLayer>
  [<ActivePart>Part1</ActivePart>]
</CADFile>
```

Element order observed (always this relative order): `layers`, `MachiningOptions`, `parts`,
`ShowCutWidths`, `ShowDirectionVector`, `ShowRapids`, `ShowStock`, `ToolpathVisibility`/`ToolpathVisiblility`,
(script text), `ActiveLayer`, `ActivePart`.

### 1.2 Root `<CADFile>`

| Attribute | Values seen | Meaning |
|---|---|---|
| `Version` | `0.9.8.0` (all 15 files, also in CamBam 1.0 templates) | file-format version; write `0.9.8.0` |
| `Name` | file base name (`snap-together-box`, `side profiles`, `opto_input.pcb.output_back`) | drawing name; used by `{$cbfile.name}` |
| `units` | `Inches` (only `opto_input.pcb.output_back.cb`) | drawing units; **absent ⇒ Millimeters** (default omitted) |
| `xmlns:xsi`, `xmlns:xsd` | fixed URIs | needed for `xsi:type` on plugin MOPs |

Child elements of `CADFile`:

| Element | n / f | Content | Notes |
|---|---|---|---|
| `layers` | 15/15 | `layer*` | |
| `MachiningOptions` | 15/15 | see 1.6 | |
| `parts` | 15/15 | `part*` (may be `<parts />`) | |
| `ShowCutWidths` | 2/2 | `true` | view flag |
| `ShowDirectionVector` | 1/1 | `false` | view flag |
| `ShowRapids` | 1/1 | `false` | view flag |
| `ShowStock` | 3/3 | `false` | view flag |
| `ToolpathVisibility` | 2/2 (CamBam.cb, skull-big-foam.cb) | `SelectedOnly` | help values: All, Selected Only |
| `ToolpathVisiblility` (sic, misspelt) | 3/3 (continuous-spiral-feed, heart-shaped-box, side profiles) | `SelectedOnly` | **older spelling**; read both, write `ToolpathVisibility` |
| `ActiveLayer` | 15/15 | layer name | |
| `ActivePart` | 13/13 | part name | absent when there are no parts |
| *(text node)* | gear-script, klannimate | drawing-level script source (IronPython) | **mixed content**: a raw text node directly inside `CADFile`, located after `parts` / view flags and before `ActiveLayer`. Help: drawing script is prepended to every script entity. |

Default-valued properties are **omitted** (XmlSerializer behaviour): e.g. `units`, all view flags, many
MachiningOptions children. A reader must supply defaults; a writer may omit default values.

Mixed content example (gear-script.cb, trimmed):

```xml
  <parts />import Gears
import Solids

DP = 0.5
...
<ActiveLayer>sun-gear-solids</ActiveLayer></CADFile>
```

### 1.3 `<layer>`

```xml
<layer name="sun-gear" color="255,255,0" pen="2" visible="false">
  <objects> ... </objects>
  <ModificationCount>2</ModificationCount>
  [layer script text node]
</layer>
```

| Attribute | Values | Default when absent |
|---|---|---|
| `name` | any string, unique per drawing | – (always present) |
| `color` | `R,G,B` | (always present; new layers use config `DefaultLayerColor` 127,255,0) |
| `pen` | `2` | pen width; absent ⇒ default (presumably 1) |
| `visible` | `false` | absent ⇒ `true` |

Help also lists layer properties Alpha, Locked, Tag, Script – XML for Alpha/Locked/Tag **not observed**
(UNVERIFIED names `alpha`, `locked`, `Tag`).

Children:

* `ModificationCount` – integer edit counter (n=47). Position varies: before `objects` (most files) or after it
  (gear-script, solids-example, klannimate). Value is informational; write `0` or preserve.
* `objects` – entity list, may be `<objects />`.
* **Layer script** – mixed text node after `ModificationCount` (klannimate.cb layer
  `Animate - select script, press F5`: `...</objects><ModificationCount>5</ModificationCount>from CamBam.Geom import *...`).
  Help: layer script is included into all script entities of that layer.

### 1.4 Entities (children of `<objects>`)

Common to every entity:

* attribute `id` – positive integer, **unique across the entire drawing** (not per layer); not contiguous
  (e.g. snap-together-box has a single entity id 244; klannimate max 1139). MOPs reference entities by this id.
* child `ModificationCount` (first child), then `mat m="..."` (second child).
* Help-listed `Tag` (free text) – not observed (UNVERIFIED element name `Tag`).

Entity element names observed (with counts):

| Element | n | files | Meaning |
|---|---|---|---|
| `pline` | 420 | 10 | polyline (lines + arcs via bulge) |
| `circle` | 191 | 7 | circle |
| `points` | 67 | 2 (klannimate, C-BEAM) | point list (drill positions etc.) |
| `script` | 45 | 4 | script entity (generates geometry at runtime) |
| `rect` | 27 | 6 | rectangle (optionally rounded) |
| `surface` | 6 | 1 (skull-big-foam) | triangle mesh |
| `text` | 4 | 3 | text |
| `line` | 2 | 1 (lathe-test) | multi-segment straight line |

**Not observed anywhere** (exist per help, XML names unknown): arc, region, spline, bitmap, NC-file MOP,
engrave MOP. See §2.

#### 1.4.1 `pline`

```xml
<pline id="244" Closed="true">
  <ModificationCount>7</ModificationCount>
  <mat m="Identity" />
  <pts>
    <p>2,60,0</p>
    <p b="-0.41421356237309503">38.5,80,0</p>
    <p>40,78.5,0</p>
  </pts>
</pline>
```

* `Closed="true"` only when closed; **absent ⇒ open** (`false` never written).
* `pts/p` text: `x,y,z` (always 3 components).
* `p@b` – **bulge** of the segment that *starts* at this vertex and ends at the next one
  (for the last vertex of a closed polyline, the closing segment back to the first vertex).
  `bulge = tan(sweep/4)`; positive = counter-clockwise, negative = clockwise; absent ⇒ 0 (straight).
  Examples: `0.41421356237309503` = +90° arc, `-0.41421356237309503` = −90°, `-0.19891236737965942` = −45°,
  `0.57735026918962573` = +120°.
  Check: `<p b="-0.414…">38.5,80,0</p>` → `<p>40,78.5,0</p>` is a quarter arc of radius 1.5, CW.
* The help warns that coincident first/last points do not imply closed; `Closed` is authoritative.

#### 1.4.2 `circle`

```xml
<circle id="5" c="34.7934777166053,37.6949945068995,0" d="12.891874375741431">
  <ModificationCount>7</ModificationCount>
  <mat m="Identity" />
</circle>
<circle id="9" d="10"> ... </circle>   <!-- c omitted => centre 0,0,0 -->
```

* `c` – centre `x,y,z`; **absent ⇒ 0,0,0**.
* `d` – diameter (round-trip double).

#### 1.4.3 `rect`

```xml
<rect id="2" Closed="true" p="-100,-100,0" w="100" h="100" radius="5">
  <ModificationCount>14</ModificationCount>
  <mat m="Identity" />
  <pts>
    <p>-95,-100,0</p>
    <p b="0.41421356237309503">-5,-100,0</p>
    <p>0,-95,0</p>
    <p b="0.41421356237309503">0,-5,0</p>
    <p>-5,0,0</p>
    <p b="0.41421356237309503">-95,0,0</p>
    <p>-100,-5,0</p>
    <p b="0.41421356237309503">-100,-95,0</p>
  </pts>
</rect>
```

* `Closed="true"` always present on rects.
* `p` – lower-left corner `x,y,z`; **absent ⇒ 0,0,0** (plasma.cb, nameplate.cb).
* `w`, `h` – width, height (round-trip doubles).
* `radius` – corner radius; absent ⇒ 0.
* `pts` – the **redundant expanded outline** is also stored and must be written:
  * no radius: 4 points CCW from lower-left: `(x,y) (x+w,y) (x+w,y+h) (x,y+h)`.
  * with radius r: 8 points CCW starting at `(x+r, y)`; the point before each corner carries
    `b="0.41421356237309503"` (CCW quarter arc).

#### 1.4.4 `points` (PointList)

```xml
<points id="91">
  <ModificationCount>2</ModificationCount>
  <mat m="Identity" />
  <pts><p>268,70</p><p>208,70</p><p>268,10</p><p>208,10</p></pts>
</points>
```

`p` = `x,y` or `x,y,z` (Z omitted when exactly 0). Used as drill positions (drill MOPs reference
`points` and `circle` entities).

#### 1.4.5 `line`

```xml
<line id="24">
  <ModificationCount>19</ModificationCount>
  <mat m="Identity" />
  <pts><p>0,0</p><p>0,-10</p></pts>
</line>
```

Like a polyline but straight segments only; `p` uses the Z-omitted form. No `Closed` seen.

#### 1.4.6 `text`

```xml
<text id="1" p1="107.576431414123,16.538320659093,0" p2="95.0087234303664,9.79144584676074,0" Height="40" Font="" align="bottom,center">
  <ModificationCount>120</ModificationCount>
  <mat m="Identity" />Nameplate</text>
<text id="2" p1="1.45520829285599,-6.61875006623566,0" p2="0,-9,0" Height="24" align="bottom,left" style="bold">
```

| Attribute | Values | Meaning |
|---|---|---|
| `p1` | `x,y,z` | insertion/location point (the alignment reference point) |
| `p2` | `x,y,z` | second point – **meaning unknown**; preserve verbatim |
| `Height` | `8`, `24`, `40` | text height (em-square based) in drawing units |
| `Font` | `""` or absent | font family; empty/absent ⇒ config default font |
| `align` | `bottom,left`, `bottom,center` | `<vertical>,<horizontal>`; vertical ∈ top/center/bottom (bottom = baseline), horizontal ∈ left/center/right |
| `style` | `bold` | font style; absent ⇒ regular. Help also has italic (presumably `italic`, possibly comma-combined – UNVERIFIED) |

The **text string is a mixed-content text node after `<mat/>`**. Help mentions CharSpace / LineSpace
(UNVERIFIED attribute names). Multi-line text not observed (expect CRLF inside the text node).

#### 1.4.7 `script`

```xml
<script id="6">
  <ModificationCount>23</ModificationCount>
  <mat m="Identity" /># see Script property ...
sungear = sun.getTeethProfile()
this.Entities.Add(sungear)
</script>
<script id="20"> ... view.Animating = 0<ExecuteOnUpdate>false</ExecuteOnUpdate></script>
```

* Script source = text node after `<mat/>` (IronPython in samples).
* Optional trailing `ExecuteOnUpdate` (`false`) **after** the text (klannimate, n=1); absent ⇒ true.
* Geometry produced by scripts is **not** saved in the file.
* Script order of evaluation (help): header + drawing script + layer script + entity script.

#### 1.4.8 `surface` (triangle mesh)

```xml
<surface id="1">
  <ModificationCount>61</ModificationCount>
  <mat m="-4.37113882867379E-08 -1 0 0 1 -4.37113882867379E-08 0 0 0 0 1 0 0 0 0 1" />
  <faces>
    <f>0,1,2</f>
    ...
  </faces>
  <verts>
    <v>37.8260600486615,2.80252932122094,-11.4640542575194</v>
    ...
  </verts>
</surface>
```

* `faces/f` = three **0-based** indices into `verts` (winding defines front face, right-hand rule – the help
  mentions back-face culling by normal direction).
* `verts/v` = `x,y,z`. Note: `faces` precedes `verts`.

### 1.5 `<parts>` / `<part>`

```xml
<part Name="Part1">
  <machineops> ...MOPs... </machineops>
  <Stock><PMin>0,0</PMin><PMax>0,0</PMax><Color>0,0,0</Color></Stock>
  <ToolProfile>Unspecified</ToolProfile>
  [<MachiningOrigin>278,80</MachiningOrigin>]
  <Nesting>
    <BasePoint>30,30</BasePoint>
    <NestMethod>Grid</NestMethod>
    <Rows>2</Rows>
    <Columns>3</Columns>
    <Spacing>8</Spacing>
  </Nesting>
  [<NestingItem> <NestingItem><Location>0,0</Location><Transform m="Identity" /></NestingItem> ... </NestingItem>]
</part>
```

| Item | Values seen | Notes |
|---|---|---|
| `@Name` | `Part1`, `PlateA`, `spiral-feed`, `lead out`, `base-front face`, `V Corner`… | names may contain spaces; not necessarily unique MOP names across parts |
| `machineops` | MOP list (1.7) | MOP order = G-code order |
| `Stock` | same structure as 1.6.1 | `PMin 0,0 / PMax 0,0` ⇒ undefined (inherit machining-level stock) |
| `ToolProfile` | `Unspecified` | part-level default tool profile |
| `MachiningOrigin` | `80,223`, `60,-60`, `248,80`, `278,80` | part G-code origin (C-BEAM only) |
| `Nesting/BasePoint` | `30,30`, `0,0`, empty | |
| `Nesting/NestMethod` | `None`, `Grid` | absent ⇒ None. Help: also Iso Grid, Manual, Point List (XML spellings UNVERIFIED, e.g. `IsoGrid`, `Manual`, `PointList`) |
| `Nesting/Rows`, `Columns`, `Spacing` | `2`, `3`, `8` | only snap-together-box |
| `NestingItem` (outer, collection) / `NestingItem` (inner) | 6 inner items | list of nest copies; inner has `Location` (2D offset) and `Transform m=` |

Help-listed Part properties not observed: Enabled, Style, StyleLibrary, ToolLibrary, ToolNumber,
ToolDiameter, OutFile, Tag, GCode Order, Grid Order, Grid Alternate, Point List ID (all UNVERIFIED names).
ToolLibrary/StyleLibrary/ToolNumber/ToolDiameter presumably mirror the MachiningOptions element names.

Nesting semantics (help + .nc evidence): copies are machined in the listed order; the snap box
order is (0,0) (70,0) (140,0) (140,70) (70,70) (0,70) (serpentine). Copy offset = `Location`.

### 1.6 `<MachiningOptions>`

```xml
<MachiningOptions>
  <OutFile>{$cbfile.name}.nc</OutFile>
  <RebuildToolpathBeforePost>Prompt</RebuildToolpathBeforePost>
  <FastPlungeHeight>0.2</FastPlungeHeight>
  <PostProcessor>Plasma</PostProcessor>
  <Stock>
    <Material>Aluminium</Material>
    <PMin>-150,-12.7,-12.7</PMin>
    <PMax>5,12.7,12.7</PMax>
    <Color>255,128,0</Color>
  </Stock>
  <StyleLibrary>Plasma-mm</StyleLibrary>
  <ToolDiameter>2</ToolDiameter>
  <ToolLibrary>Lathe-mm</ToolLibrary>
  <ToolNumber>1</ToolNumber>
  <ToolProfile>Unspecified</ToolProfile>
  <MachiningOrigin>145,0</MachiningOrigin>
</MachiningOptions>
```

Observed children, in this order (only those present per file):

| Element | n/f | Values | Meaning / default |
|---|---|---|---|
| `OutFile` | 10/10 | `lathe-test.nc`, `{$cbfile.name}.nc`, empty | G-code destination; may contain macros |
| `RebuildToolpathBeforePost` | 1/1 | `Prompt` | help: Always / Prompt / If Needed (spellings for others UNVERIFIED, e.g. `Always`, `IfNeeded`) |
| `FastPlungeHeight` | 9/9 | `0.1`, `0.2`, `0.4` | height above stock/last cut level to which a rapid is used before feeding; help: default −1 (= one minor grid unit), 0 = feed all the way |
| `PostProcessor` | 7/7 | `Mach3-CutViewer+G43`, `Laser2`, `Mach3-Turn-CutViewer`, `Plasma`, `Mach3-CV` (not shipped!) | name of `.cbpp` without extension; absent/empty ⇒ default post (config `DefaultPostProcessor`) |
| `Stock` | 15/15 | 1.6.1 | |
| `StyleLibrary` | 2/2 | `Plasma-mm`, `Mill-mm` (neither shipped) | style library name; may use `{$Material}`, `{$Units}` |
| `ToolDiameter` | 7/7 | `2`, `6`, `0.18`, `3` | global default tool diameter |
| `ToolLibrary` | 2/2 | `Mill-mm` (not shipped), `Lathe-mm` | absent ⇒ `Default-{units}` |
| `ToolNumber` | 6/6 | `1`, `3` | global default tool number |
| `ToolProfile` | 15/15 | `Unspecified`, `BullNose` | always written |
| `MachiningOrigin` | 1/1 | `145,0` | G-code origin (drawing point that becomes X0 Y0) |

Help-listed but not observed (UNVERIFIED names): ArcCenterMode, CustomFileHeader, CustomFileFooter,
InnerTabScale, OuterTabScale, NumberFormat, PostProcessorMacros, Style, VelocityMode.
Missing referenced libraries/posts must be tolerated (fall back to defaults, keep the name).

#### 1.6.1 `<Stock>`

| Child | Values | Meaning |
|---|---|---|
| `Material` | `Aluminium`, `Foam`, `Acrylic`, `Engraving Laminate - Yellow / Black` | optional; drives `{$Material}` in style library lookup |
| `PMin` | `0,0,-13`, `-10,-10,-3.5`, `-150,-12.7,-12.7`, `0,0` | lower corner `x,y[,z]` (Z omitted if 0) |
| `PMax` | `160,85`, `215,145`, `5,12.7,12.7`, `0,0` | upper corner; Z omitted if 0 (i.e. stock top at Z=0) |
| `Color` | `255,165,0`, `255,255,0`, `0,0,0`, `0,0,0,0` | display colour |

Stock size = PMax − PMin; stock surface = PMax.z; stock is undefined when all sizes are 0.
Stock is present (as an empty `0,0`/`0,0`) in every MachiningOptions and every part.

### 1.7 Machining operations (MOPs)

Element names (children of `machineops`):

| Element | n / files | Kind |
|---|---|---|
| `profile` | 35 / 10 | 2.5D profile |
| `pocket` | 11 / 4 | pocket |
| `drill` | 14 / 1 (C-BEAM) | drilling |
| `surface3d` | 9 / 1 (skull-big-foam) | 3D profile |
| `mop xsi:type="MOPLathe"` | 2 / 1 (lathe-test) | plugin MOP – generic element `mop` with `xsi:type` naming the class |

Engrave and NC File MOPs are not in the corpus (names UNVERIFIED; the plugin pattern suggests
other plugin MOPs would also appear as `<mop xsi:type="...">`).

#### 1.7.1 The `state` attribute (style inheritance)

Nearly every MOP property is a *state-wrapped value*:

```xml
<TargetDepth state="Default">-3.5</TargetDepth>   <!-- inherited from the CAM style; text = cached resolved value -->
<CornerOvercut state="Value">True</CornerOvercut> <!-- explicit user override -->
<CustomMOPHeader state="Default" />               <!-- empty value -->
<LeadInMove state="Default" />                    <!-- composite with no cached content -->
```

| `state` | Meaning |
|---|---|
| `Default` | value comes from the style chain (MOP Style → Part Style → Machining Style → library root style `Name=""`). The element text is the **cached value last resolved** – CamBam warns ("property cache conflict") if the newly resolved value differs, which lets files travel without their style libraries. |
| `Value` | explicit value, overrides the style |
| `Auto` | value computed by the program (seen only in style libraries: `ToolDiameter`, `ToolNumber`, `ToolProfile`, `StockSurface`, `TargetDepth`, `HoleDiameter`, `DrillingMethod`, `RetractHeight`). E.g. Auto TargetDepth = cut through full stock; Auto StockSurface = stock top. |

BamCam model: `CBValue<T> { State: Default|Value|Auto; T Cached; }`. When writing, always emit the cached
value text (even for `Default`) so CamBam can detect cache conflicts.

Properties **without** a state attribute: `Name`, `primitive`, `Style`, `Enabled`, `Tabs`, `BoundaryShapes`.

Composite state-wrapped properties (`LeadInMove`, `LeadOutMove`, `HoldingTabs`, `SideProfile`) contain
sub-elements, or are empty (`<SideProfile state="Default" />`) when nothing cached.

#### 1.7.2 Primitive references

```xml
<primitive>
  <prim>103</prim>
  <prim>148</prim>
</primitive>
<primitive />            <!-- no source objects -->
```

Each `prim` is an entity `id` (1.4). Order preserved as selected. All references in the corpus resolve.
Other id references: `Tabs/HoldingTab/ParentEntityID`, `surface3d/BoundaryShapes/id`.
Converting an entity (e.g. rect → polyline) gives it a new id, breaking links (help).

#### 1.7.3 Element order

Two serialization orders exist; the parser must be order-independent.

* **Older order** (heart-shaped-box, side profiles, CamBam, nameplate, snap-together-box, continuous-spiral-feed,
  opto, skull, lathe): common base properties first, then type-specific ones.
* **Newer order** (CamBam 1.0 templates laser/plasma and C-BEAM): type-specific first, then base.

Recommended writer order (newer), profile:
`TargetDepth DepthIncrement FinalDepthIncrement CutOrdering CutWidth StepOver StepoverFeedrate InsideOutside MillingDirection CollisionDetection CornerOvercut LeadInMove LeadOutMove HoldingTabs [Tabs] SideProfile StockSurface RoughingClearance ClearancePlane SpindleDirection SpindleSpeed SpindleRange VelocityMode primitive [Style] [Enabled] WorkPlane OptimisationMode RoughingFinishing ToolDiameter ToolNumber ToolProfile PlungeFeedrate CutFeedrate MaxCrossoverDistance StartPoint CustomMOPHeader CustomMOPFooter Name`

pocket:
`TargetDepth DepthIncrement FinalDepthIncrement CutOrdering StepOver StepoverFeedrate MillingDirection CollisionDetection LeadInMove LeadOutMove RegionFillStyle FinishStepover FinishStepoverAtTargetDepth StockSurface RoughingClearance ClearancePlane SpindleDirection SpindleSpeed SpindleRange VelocityMode primitive WorkPlane OptimisationMode RoughingFinishing ToolDiameter ToolNumber ToolProfile PlungeFeedrate CutFeedrate MaxCrossoverDistance StartPoint CustomMOPHeader CustomMOPFooter Name`

drill:
`TargetDepth DepthIncrement HoleDiameter DrillLeadOut SpiralFlatBase LeadOutLength DrillingMethod PeckDistance RetractHeight Dwell CustomScript StockSurface RoughingClearance ClearancePlane SpindleDirection SpindleSpeed SpindleRange VelocityMode primitive WorkPlane OptimisationMode RoughingFinishing ToolDiameter ToolNumber ToolProfile PlungeFeedrate CutFeedrate MaxCrossoverDistance StartPoint CustomMOPHeader CustomMOPFooter Name`

surface3d (only older order observed):
`WorkPlane OptimisationMode RoughingFinishing ToolDiameter ToolNumber ToolProfile PlungeFeedrate CutFeedrate MaxCrossoverDistance StartPoint CustomMOPHeader CustomMOPFooter Name StockSurface RoughingClearance ClearancePlane SpindleDirection SpindleSpeed SpindleRange VelocityMode primitive DepthIncrement TargetDepth StepOver StepoverFeedrate Resolution ClipAreaMin ClipAreaMax StartCorner Profile3DMethod BackFaceCulling PlaneSliceOnly ScanlineGradientThreshold Mold Additive BackFace BackFaceZeroZ FlipAxis CutOrdering MillingDirection LeadInMove LeadOutMove BoundaryMethod [BoundaryShapes] BoundaryMargin BoundaryTaper RegionFillStyle`

mop/MOPLathe (older order):
`WorkPlane OptimisationMode RoughingFinishing ToolDiameter ToolNumber ToolProfile PlungeFeedrate CutFeedrate MaxCrossoverDistance CustomMOPHeader CustomMOPFooter Name StockSurface RoughingClearance ClearancePlane SpindleDirection SpindleSpeed SpindleRange VelocityMode primitive [DepthIncrement] [LatheLeadOutLength] [LatheLeadInLength]`
(the lathe MOP omits default-valued type-specific properties entirely; `StartPoint` not written in older files).

`Style` position: older files put it first (opto: `<profile><Style>PCB Isolate</Style><WorkPlane …`), newer after `primitive`.
`Enabled` (laser.cb) appears after `primitive` and only when `false`.

#### 1.7.4 Base properties (all MOP types)

| Element | Type | Values observed (state) | Meaning |
|---|---|---|---|
| `Name` | string, no state | `Profile1`, `Chamfer`, `5mm bolt`, `front rough`, `inside +ve convex radius` | output as G-code comment |
| `Enabled` | bool, no state | `false` (laser.cb only) | absent ⇒ true |
| `Style` | string, no state | `Chamfer`, `PCB Isolate` | CAM style name; absent/empty ⇒ inherit part/machining style |
| `WorkPlane` | enum | `XY` (Default), `XZ` (Value, lathe) | help: XY / XZ / YZ |
| `OptimisationMode` | enum | `Experimental` | help names New (0.9.8)/Legacy (0.9.7)/None; `Experimental` = the "New" optimiser. Others UNVERIFIED (`Legacy`, `None`) |
| `RoughingFinishing` | enum | `Roughing`, `Finishing` | |
| `ToolDiameter` | double | `2`, `3`, `6`, `9.525`, `0.18`, `0.008`, `0` | 0 ⇒ look up tool library |
| `ToolNumber` | int | `0`, `1`, `2`, `3`, `4` | 0 ⇒ never issue tool change |
| `ToolProfile` | enum | `EndMill`, `BullNose`, `VCutter`, `Unspecified`, `Lathe` | help adds `BallNose`, `Drill` (spelling of BallNose UNVERIFIED; `Drill` seen in ExcellonIO.config) |
| `PlungeFeedrate` | double | `300`, `50`, `10`, `100`, `150`, `1000` | |
| `CutFeedrate` | double | `800`, `300`, `400`, `500`, `600`, `1400`, `30` | |
| `MaxCrossoverDistance` | double | `0.7`, `0` | fraction of tool diameter; beyond ⇒ retract/rapid/plunge |
| `StartPoint` | point3 or empty | `0,0,0`, empty | preferred start of first toolpath |
| `CustomMOPHeader` / `CustomMOPFooter` | string | empty (both Default and Value seen) | G-code inserted before/after MOP (`{$mop.header}`/`{$mop.footer}`); `\|` = newline per help |
| `StockSurface` | double | `0`, `1.6`, `-7`, `-8`, `12.7` | Z of stock top |
| `RoughingClearance` | double | `0`, `0.1`, `1`, `1.3`, `2.5`, `3`, `5`, `-1` | stock to leave (negative = oversize) |
| `ClearancePlane` | double | `3`, `10`, `14`, `1.5`, `0.125` | safe Z for rapids |
| `SpindleDirection` | enum | `CW` | help: CW / CCW / Off |
| `SpindleSpeed` | double/int | `1000`, `3000` | RPM |
| `SpindleRange` | int | `0` | |
| `VelocityMode` | enum | `Undefined` | help: Default / Constant Velocity / Exact Stop (`Undefined` = "Default"; spellings for others UNVERIFIED, likely `ConstantVelocity`, `ExactStop`) |
| `primitive` | id list | see 1.7.2 | |

Help-listed base properties not observed: `Tag`, `Transform` (toolpath transform), `TargetDepth` for some MOPs.

#### 1.7.5 `profile`-specific

| Element | Values observed | Meaning |
|---|---|---|
| `TargetDepth` | `-3.5`, `-1`, `-5`, `-9`, `-10`, `-13`, `0`, `1.5` | final Z |
| `DepthIncrement` | `0.4`, `0.1`, `1`, `2`, `0.015` | step-down per pass |
| `FinalDepthIncrement` | `0` | depth of last pass (0 = not used) |
| `CutOrdering` | `DepthFirst`, `LevelFirst` | |
| `CutWidth` | `0`, `4`, `7`, `0.02` | total cut width; > tool dia ⇒ parallel passes; 0 ⇒ tool dia |
| `StepOver` | `0.4`, `0.3` | fraction of tool diameter |
| `StepoverFeedrate` | `Plunge Feedrate`, `PlungeFeedrate` | **both spellings exist** (with/without space); accept both, write `Plunge Feedrate`. Other values UNVERIFIED (e.g. `Cut Feedrate`) |
| `InsideOutside` | `Inside`, `Outside` | |
| `MillingDirection` | `Conventional`, `Climb` (`Mixed` for pocket/3D) | |
| `CollisionDetection` | `True` | union overlapping toolpaths |
| `CornerOvercut` | `True`, `False` | adds moves into inside corners |
| `LeadInMove` / `LeadOutMove` | composite, see below | |
| `HoldingTabs` | composite, see below | |
| `Tabs` | list of `HoldingTab` (no state) | actual tab placements |
| `SideProfile` | composite | |

Lead move composite (`LeadInMove` and `LeadOutMove` share the same children – note the lead-out still uses
`LeadInType`/`LeadInFeedrate` names):

```xml
<LeadInMove state="Value">
  <LeadInType>Spiral</LeadInType>     <!-- None | Spiral | Tangent -->
  <SpiralAngle>0</SpiralAngle>        <!-- ramp angle in degrees; 0 = auto (one depth increment per lap) -->
  <TangentRadius>0</TangentRadius>    <!-- 0.7, 2, 5 seen -->
  <LeadInFeedrate>0</LeadInFeedrate>  <!-- 0 = use CutFeedrate -->
</LeadInMove>
```

Holding tabs composite:

```xml
<HoldingTabs state="Value">
  <TabMethod>Manual</TabMethod>      <!-- None | Automatic | Manual (help: Automatic Outer/Inner – spelling UNVERIFIED) -->
  <Width>0.8</Width>                 <!-- narrowest tab width -->
  <Height>0.8</Height>               <!-- above TargetDepth -->
  <MinimumTabs>2</MinimumTabs>
  <MaximumTabs>6</MaximumTabs>
  <TabDistance>35</TabDistance>      <!-- approx spacing for auto tabs; 0 => MinimumTabs -->
  <SizeThreshold>0</SizeThreshold>   <!-- shapes with smaller perimeter get no auto tabs -->
  <UseLeadIns>False</UseLeadIns>
  <TabStyle>Square</TabStyle>        <!-- Square | Triangle (help: Skip) -->
</HoldingTabs>
<HoldingTabs state="Default" />      <!-- also valid (plasma.cb) -->
```

Tab placements:

```xml
<Tabs>
  <HoldingTab>
    <NormalInverted>false</NormalInverted>   <!-- optional (snap-together-box only) -->
    <ParentEntityID>244</ParentEntityID>
    <ParametricPoint>0.01483519531268529</ParametricPoint>
    <Normal><X>-0</X><Y>1</Y></Normal>
  </HoldingTab>
</Tabs>
```

* `ParametricPoint` = **fraction (0..1) of the arc length** along the parent entity's outline measured from its
  first vertex (closing segment included). Verified: the snap box perimeter is 272; the six values map to
  (6.035,60) (44.312,60) (60,35.379) (53.378,0) (14.849,0) (0,25.055), which are exactly the tab centres in the G-code.
* `Normal` = unit outward normal of the outline at that point (drawing XY).
* `Tabs` is written right after `HoldingTabs` (older order) and only when tabs exist.

Side profile composite:

```xml
<SideProfile state="Value">
  <Method>ConvexRadius</Method>      <!-- Slope | ConvexRadius | ConcaveRadius  (help: None) -->
  <Value>10</Value>                  <!-- radius, or slope angle from vertical in degrees; sign matters -->
  <AdjustCutWidth>False</AdjustCutWidth>  <!-- optional (13 of 16) -->
</SideProfile>
```

#### 1.7.6 `pocket`-specific

Shares `TargetDepth DepthIncrement FinalDepthIncrement CutOrdering StepOver StepoverFeedrate MillingDirection CollisionDetection LeadInMove LeadOutMove` with profile, plus:

| Element | Values | Meaning |
|---|---|---|
| `RegionFillStyle` | `VerticalHatch`, `HorizontalHatch`, `OutsideOffsets`, `InsideOffsets`, `InsideOutsideOffsets` | fill pattern |
| `FinishStepover` | `0` | stepover for final cleanup pass |
| `FinishStepoverAtTargetDepth` | `False` | only apply finish stepover at the last level |
| `MillingDirection` | `Conventional`, `Climb`, `Mixed` | |
| `MaxCrossoverDistance` | `0.7`, `0` (Value) | |

#### 1.7.7 `drill`-specific

| Element | Values | Meaning |
|---|---|---|
| `TargetDepth` | `-4`, `-8`, `-13`, `-1.6` | |
| `DepthIncrement` | `0.4`, `0.7` | spiral pitch per lap for spiral milling |
| `HoleDiameter` | `26`, `5.1`, `4`, `10`, `20` (all Value) | for spiral milling; Auto ⇒ from circle size |
| `DrillLeadOut` | `True`, `False` | spiral: move toward/away from centre before retract |
| `SpiralFlatBase` | `True` | add a full circle at the bottom |
| `LeadOutLength` | `0`, `2` | + toward centre, − away |
| `DrillingMethod` | `CannedCycle`, `SpiralMill_CW`, `SpiralMill_CCW` | help adds `CustomScript` |
| `PeckDistance` | `0` | 0 = no pecking (G81/G82 vs G83) |
| `RetractHeight` | `3` | R plane of canned cycles |
| `Dwell` | `0` | dwell (G82 P) |
| `CustomScript` | empty | drilling script; help macros `$c $d $f $h $n $p $q $r $s $t $x $y $z`, `\|` = newline |

Drill primitives in corpus: 11 `points`, 6 `circle`.

#### 1.7.8 `surface3d`-specific

| Element | Values | Meaning |
|---|---|---|
| `Resolution` | `0.2` | scanline sample spacing (fraction of tool dia) |
| `ClipAreaMin`, `ClipAreaMax` | `0,0` | 2D clip box (both 0 ⇒ no clip) |
| `StartCorner` | `BottomLeft` | scanline start corner |
| `Profile3DMethod` | `WaterlineRough`, `Horizontal` | help: also Vertical, Waterline Finish (`Vertical`, `WaterlineFinish` UNVERIFIED) |
| `BackFaceCulling` | `True` | |
| `PlaneSliceOnly` | `False` | |
| `ScanlineGradientThreshold` | `90` | degrees |
| `Mold` | `True`/`False` | negative mould |
| `Additive` | `True`/`False` | experimental extrusion |
| `BackFace` | `True`/`False` | machine back face |
| `BackFaceZeroZ` | `0`, `-104` | |
| `FlipAxis` | `X`, `Y` | |
| `BoundaryMethod` | `ShapeOutline`, `SelectedShapes` | help: Bounding Box (`BoundingBox` UNVERIFIED) |
| `BoundaryShapes` | `<id>1</id>…` (no state) | entity ids for SelectedShapes |
| `BoundaryMargin` | `0`, `2`, `3` | |
| `BoundaryTaper` | `0`, `3` | degrees |
| `RegionFillStyle` | `InsideOutsideOffsets` | for waterline rough |
| `CutOrdering`, `MillingDirection` (`Mixed` seen), `DepthIncrement` (`0`,`1`,`3`), `TargetDepth` (`-50`), `StepOver` (`0.1`,`0.2`,`0.4`), `StepoverFeedrate`, `LeadInMove`, `LeadOutMove` | | as profile |

Help also: AutoArcFitting, ArcFitTolerance (UNVERIFIED names).

#### 1.7.9 `mop xsi:type="MOPLathe"`

Base properties plus: `DepthIncrement` (`0.25`, radial step), `LatheLeadInLength` (`0.5`), `LatheLeadOutLength` (`0.5`).
`WorkPlane` = `XZ` (Value). `ToolProfile` = `Lathe`. Help also: Lathe Cut Direction (UNVERIFIED name).
Drawing convention (help): lathe +X is drawn along drawing −Y, lathe +Z along drawing +X; `MachiningOrigin` on the spindle axis.

Full example (trimmed):

```xml
<mop xsi:type="MOPLathe">
  <WorkPlane state="Value">XZ</WorkPlane>
  <OptimisationMode state="Default">Experimental</OptimisationMode>
  <RoughingFinishing state="Value">Roughing</RoughingFinishing>
  <ToolDiameter state="Default">2</ToolDiameter>
  <ToolNumber state="Default">1</ToolNumber>
  <ToolProfile state="Default">Lathe</ToolProfile>
  <PlungeFeedrate state="Default">300</PlungeFeedrate>
  <CutFeedrate state="Default">500</CutFeedrate>
  <MaxCrossoverDistance state="Default">0.7</MaxCrossoverDistance>
  <CustomMOPHeader state="Default" />
  <CustomMOPFooter state="Default" />
  <Name>rough</Name>
  <StockSurface state="Default">12.7</StockSurface>
  <RoughingClearance state="Value">0.1</RoughingClearance>
  <ClearancePlane state="Value">14</ClearancePlane>
  <SpindleDirection state="Default">CW</SpindleDirection>
  <SpindleSpeed state="Default">1000</SpindleSpeed>
  <SpindleRange state="Default">0</SpindleRange>
  <VelocityMode state="Default">Undefined</VelocityMode>
  <primitive><prim>39</prim></primitive>
  <DepthIncrement state="Value">0.25</DepthIncrement>
  <LatheLeadOutLength state="Value">0.5</LatheLeadOutLength>
  <LatheLeadInLength state="Value">0.5</LatheLeadInLength>
</mop>
```

---

## 2. Unknown / rare elements

### 2.1 Seen exactly once (or in a single file)

| Item | Where | Notes |
|---|---|---|
| `CADFile@units="Inches"` | opto_input.pcb.output_back.cb | only non-mm drawing |
| `ShowDirectionVector`, `ShowRapids` | opto_input | view flags |
| `RebuildToolpathBeforePost` | skull-big-foam | value `Prompt` |
| `MachiningOptions/MachiningOrigin` | lathe-test | |
| `part/MachiningOrigin` | C-BEAM (7 parts) | |
| `Nesting/Rows`, `Columns`, `Spacing`, `part/NestingItem` collection | snap-together-box | |
| `HoldingTab/NormalInverted` | snap-together-box | |
| `mop xsi:type="MOPLathe"`, `LatheLeadInLength`, `LatheLeadOutLength` | lathe-test | |
| `line` entity | lathe-test (2) | |
| `surface` entity, `faces/f`, `verts/v` | skull-big-foam | |
| `surface3d` MOP and all its specific properties | skull-big-foam | |
| `drill` MOP and all its specific properties | C-BEAM-Belt-Drive | |
| `script/ExecuteOnUpdate` | klannimate | `false`, after the script text |
| layer-level script text node | klannimate | |
| `profile/Enabled` | templates/laser.cb | `false` |
| `text@style` | solids-example | `bold` |
| `PostProcessor` = `Mach3-CV` | opto_input | post not shipped |
| `StyleLibrary` `Mill-mm`/`Plasma-mm`, `ToolLibrary` `Mill-mm` | C-BEAM, plasma | libraries not shipped |
| `Stock/Material` values | 6 files | free text |
| `Stock/Color` `0,0,0,0` | part stocks | 4-component colour |
| `StepoverFeedrate` = `PlungeFeedrate` (no space) | continuous-spiral-feed, C-BEAM | vs `Plunge Feedrate` elsewhere |
| `ToolpathVisiblility` (typo) | 3 older files | vs `ToolpathVisibility` |
| drawing-level script text in `CADFile` | gear-script, klannimate | |
| `rect@radius` | laser.cb, nameplate.cb | |
| `points` entities with 3-component `p` | klannimate | tiny Z like `-1.05836760413361E-14` |
| `Tabs` element | 5 files only (older order) | |

### 2.2 Documented in help but not present in any file (names unknown – preserve raw)

Entities: **arc** (properties Center, Radius, Start angle, Sweep angle), **region** (outer shape + holes),
**spline/NURBS**, **bitmap**. MOPs: **engrave**, **NC File**. Layer `Alpha`, `Locked`, `Tag`; entity `Tag`;
text CharSpace/LineSpace/italic; part Enabled/Style/GCodeOrder/GridOrder/GridAlternate/PointListID/OutFile;
machining ArcCenterMode/CustomFileHeader/CustomFileFooter/InnerTabScale/OuterTabScale/NumberFormat/PostProcessorMacros/VelocityMode/Style;
3D AutoArcFitting/ArcFitTolerance; lathe cut direction; tool library Coating/Material/Notes/PartCode/ToolChange.
BamCam must keep any unrecognised child element (and attribute) as an opaque XML fragment and write it back.
Until a real file is obtained, BamCam should export arcs as `pline` with one bulged segment, and regions as
separate closed `pline`s, which CamBam is known to read.

---

## 3. Post processor `.cbpp` format

### 3.1 Structure

```xml
<?xml version="1.0" encoding="utf-8"?>
<PostProcessor xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" Version="0.9.8.0">
  <PostFile>{$comment} Made using CamBam - http://www.cambam.co.uk {$endcomment}
{$header}
{$mops}
{$footer}
</PostFile>
  <Header>...</Header>
  <Footer>...</Footer>
  <MOP>...</MOP>
  <MinimumArcLength>0.0001</MinimumArcLength>
  <MaximumArcRadius>10000</MaximumArcRadius>
</PostProcessor>
```

* Root `PostProcessor@Version="0.9.8.0"`. File name (without `.cbpp`) is the post's name.
* Each child is one property. **Only non-default properties are stored**; omitted ones take built-in
  defaults (see 3.4). An **empty element** (`<SpindleCW />`, `<ToolChange />`, `<CannedCycleStart />`)
  explicitly sets the value to an empty string (suppressing the default) – used by laser/GRBL posts.
* Section text is literal, multi-line (CRLF inside the element), XML-escaped; leading/trailing whitespace and
  tabs inside the text are part of the value (e.g. `Fanuc DK` indents lines with tabs/spaces; several MOP
  sections end with `\r\n  ` before `</MOP>`).

### 3.2 Every element observed (with files)

Template (text) sections:

| Element | Files | Purpose |
|---|---|---|
| `PostFile` | all 21 | overall file layout; usually `{$header}` `{$mops}` `{$footer}` |
| `Header` | 20 (not Fanuc) | expands `{$header}` |
| `Footer` | 21 | expands `{$footer}` |
| `MOP` | 21 | per machining operation, expands each item of `{$mops}` |
| `ToolChange` | 8 | expands `{$toolchange}` / `{$toolchange(first)}` |
| `ToolTableItem` | Centroid, Mach3-Laser (empty) | one line per tool for `{$tooltable}` |
| `PostProcessorMacros` | Centroid | default user macros, lines `$name=value`, `#` comment lines allowed (`# default program address` / `$o=1`) |
| `StartCut` | GRBL-laser, LinuxCNC-Laser, Mach3-Laser, Plasma | emitted when a feed move goes below stock surface (laser/plasma on) |
| `EndCut` | same 4 | emitted on rapid or feed at/above stock surface (off) |
| `Rapid` | 9 | G0 move format |
| `FeedMove` | 9 | G1 move format |
| `ArcCW` | 10 | G2 format |
| `ArcCCW` | 10 | G3 format |
| `Drill` | 11 | G81 format |
| `DrillDwell` | 11 | G82 format |
| `DrillPeck` | 11 (GRBL/GRBL-laser: empty) | G83 format |
| `CannedCycleStart` | Centroid `G99`, GRBL/GRBL-laser empty | before a group of canned cycles (help: typically G98) |
| `CannedCycleEnd` | GRBL empty | after group (help: typically G80) |
| `Comment` | Centroid `:` | comment opener (default `(`) |
| `EndComment` | Centroid empty | comment closer (default `)`) |
| `SpindleCW` / `SpindleCCW` / `SpindleOff` | laser posts: empty | defaults M3 / M4 / M5 |
| `VelocityModeExactStop` / `VelocityModeConstantVelocity` | GRBL, GRBL-laser: empty | defaults G61 / G64 |
| `ArcCenterAbsolute` | Mach3*, LinuxCNC* `G90.1` | output of `{$arccentermode}` for absolute IJ |
| `ArcCenterIncremental` | Mach3*, LinuxCNC* `G91.1` | output of `{$arccentermode}` for incremental IJ |
| `XModeDiameter` / `XModeRadius` | LinuxCNC-Turn `G7` / `G8` | output of `{$lathexmode}` |

Option properties:

| Element | Values seen | Meaning |
|---|---|---|
| `UpperCase` | `true` (Centroid, Fanuc, Fanuc DK) | upper-case all output |
| `MinimumArcLength` | `0.0001` | arcs shorter ⇒ G1 |
| `MaximumArcRadius` | `1000`, `10000` | arcs with larger radius ⇒ G1 |
| `NumberFormat` | `0.0#` (GRBL), `0.0####` (LinuxCNC) | .NET custom numeric format for all values; `0` = forced digit, `#` = optional digit |
| `ArcOutput` | `ConvertToLines` (Laser) | help: Normal / Convert To Lines / Helix Convert To Lines (`HelixConvertToLines` UNVERIFIED) |
| `ArcToLinesTolerance` | `0.1` | chord tolerance for ArcOutput conversion |
| `ClearancePlaneAxis` | `X` (turning posts) | axis used for clearance moves (default Z) |
| `LatheXMode` | `Diameter` | lathe X as diameter (other value presumably `Radius`) |
| `InvertArcs` | `true` (Mach3-Turn*) | swap G2/G3 for XZ arcs |
| `AddLineNumbers` | `true` (Plasma) | prefix lines with N numbers |
| `LineNumberFormat` | `0000` | format for line numbers |
| `SuppressParserErrors` | `true` (Centroid) | hide internal G-code parser warnings |
| `RotaryWrap` | `true` (RotaryX, RotaryY) | wrap toolpaths around rotary axis (radius = MOP StockSurface) |
| `AxisOfSpin` | `X` (RotaryX) | stock rotation axis (RotaryY omits ⇒ default, presumably Y) |

Help-listed properties without observed XML name (UNVERIFIED): Arc Center Mode, Cutter Comp Off/Left/Right,
Distance Absolute/Incremental, Units Inches/Metric, Workplane XY/XZ/YZ, G0/G1/G2/G3/G81/G82/G83 code text,
End Rewind, Repeat, Stop, End Of Line, Rapid Down To Clearance, Lathe Tool Radius Offset, Rotary Axis,
Line Number Increment/Prefix/Skip/Space After/Start, Post-Build Command / Post-Build Command Args.

### 3.3 Macro syntax and catalogue

Syntax: `{$name}` or `{$name(arg[,arg])}`. Names are dotted; case appears insensitive
(Plasma uses `{$mop.first.X}`). User macros: defined as `$name=value` (one per line) in the drawing's
"Post Processor Macros" property or the post's `PostProcessorMacros` section (drawing wins).
Register macros include the register letter and value (`{$x}` → `X1.23`); a **leading underscore makes
the token modal** (only emitted when value changed); without it the token is always emitted.

All macros found in shipped posts (count):

| Macro | Meaning (own wording) |
|---|---|
| `{$header}` `{$mops}` `{$footer}` | expand Header section / list of MOP blocks / Footer section |
| `{$blocks}` | all motion lines of the current MOP (generated) |
| `{$comment}` `{$endcomment}` | comment open/close strings (default `(` `)`) |
| `{$cbfile.name}` | drawing `Name` |
| `{$cbfile.header}` `{$cbfile.footer}` | drawing custom file header/footer |
| `{$date}` | current date-time (sample: `10/23/2012 11:49:42 PM`, en-US general format) |
| `{$tooltable}` | one ToolTableItem per tool used |
| `{$toolchange}` | ToolChange text if tool number changed (nothing if same tool or tool 0) |
| `{$toolchange(first)}` | ToolChange for the first tool of the job |
| `{$tool.index}` `{$tool.diameter}` `{$tool.radius}` `{$tool.length}` `{$tool.comment}` | current tool data (radius: 0 for end mill, d/2 for bull nose per help; comment from tool library) |
| `{$clearance}` | rapid to clearance plane (nothing if already there) |
| `{$units}` | G21 / G20 |
| `{$distancemode}` | G90 (always absolute) |
| `{$velocitymode}` | G64 / G61 per MOP/global setting; nothing if unchanged/undefined |
| `{$cuttercomp(off)}` | G40 (L/R variants exist: G41/G42) |
| `{$arccentermode}` | ArcCenterAbsolute or ArcCenterIncremental text |
| `{$lathexmode}` | XModeDiameter / XModeRadius text |
| `{$workplane}` | G17 / G18 / G19 (modal: nothing if unchanged) |
| `{$spindle}` | M3/M4/M5 per MOP spindle direction; nothing if spindle already in that state |
| `{$spindle(off)}` | SpindleOff text (M5) |
| `{$s}` | spindle speed word (`S1000`) |
| `{$endrewind}` | M30 |
| `{$mop.name}` `{$mop.header}` `{$mop.footer}` | MOP name / custom header / custom footer |
| `{$mop.clearanceplane}` `{$mop.retractheight}` `{$mop.targetdepth}` `{$mop.plungefeedrate}` | MOP values (bare numbers, no register letter) |
| `{$mop.first.x}` `{$mop.first.y}` | first toolpath point of the MOP |
| `{$g0}` `{$g1}` `{$g2}` `{$g3}` `{$g81}` `{$g82}` `{$g83}` | G-code word (always output) |
| `{$_g0}` `{$_g1}` `{$_g2}` `{$_g3}` `{$_g81}` `{$_g82}` `{$_g83}` | modal G-code word |
| `{$_x}` `{$_y}` `{$_z}` `{$_a}` `{$_b}` `{$_c}` `{$_f}` `{$_r}` `{$_q}` | modal register words |
| `{$i}` `{$j}` `{$k}` `{$p}` `{$q}` `{$r}` | always-output register words (arc centre, dwell, peck, retract) |
| `{$set(z,NaN)}` | set internal register (here: forget Z) without output |
| `{$stock_width}` `{$stock_length}` `{$stock_height}` `{$stock_x}` `{$stock_y}` `{$stock_z}` | stock size and negated lower-left offsets (CutViewer STOCK) |
| `{$o}` | user macro (program number) – must be defined via macros |

Additional macros described in help only: `$arc.i/j/k`, `$arc.radius`, `$arc.start/sweep/end`, `$check(x,y,z)`,
`$mop.cutfeedrate`, `$mop.depthincrement`, `$mop.dwell`, `$mop.holediameter`, `$mop.peckdistance`,
`$mop.stocksurface`, `$mop.tag`, `$mop.first.z`, `$move.x/y/z`, `$part.name`, `$post.toolchange`, `$repeat`,
`$stop`, `$tool.name`, `$tool.profile`, `$tool.veeangle`, `$stock.xsize/ysize/zsize`, `$stock.width/length/height`,
`$stock.xoffset/yoffset`, `$-stock.xoffset/yoffset`, `$xneg/$yneg/$zneg…` (sign-reversed), `$xabs…` (absolute),
`$outfile` (post-build args). Style library macros `{$Material}`, `{$Units}`; tool name macros
`{$diameter} {$flutes} {$index} {$length} {$partcode} {$profile} {$veeangle}`; OutFile may use `{$cbfile.name}`.

### 3.4 Built-in defaults and expansion rules (inferred from Default.cbpp + snap-together-box.nc)

Defaults that are **not** in Default.cbpp but appear in its output (so they are built-in):

| Property | Built-in value (inferred) | Evidence |
|---|---|---|
| Comment / EndComment | `(` / `)` | `( Profile1 )` |
| ToolTableItem | `{$comment} T{$tool.index} : {$tool.diameter} {$endcomment}` | `( T1 : 2.0 )` line 3 |
| ToolChange | `{$clearance}` ⏎ `{$comment} T{$tool.index} : {$tool.diameter} {$endcomment}` ⏎ `T{$tool.index} M6` | lines 5-7 `G0 Z3.0` / `( T1 : 2.0 )` / `T1 M6` (same text as Laser.cbpp) |
| Rapid | `{$g0} {$_f} {$_x} {$_y} {$_z} {$_a} {$_b} {$_c}` | G0 on every rapid line, coords modal |
| FeedMove | `{$g1} {$_f} {$_x} {$_y} {$_z} {$_a} {$_b} {$_c}` | `G1` repeated on every line (not modal), F modal |
| ArcCW / ArcCCW | `{$g2} {$_f} {$_x} {$_y} {$_z} {$i} {$j}` / same with g3 | `G3 X-1.0 Y50.0 I0.0 J-1.0`: I and J always, no K |
| Units metric / inch | `G21` / `G20` | |
| Distance absolute | `G90` | |
| Velocity constant / exact | `G64` / `G61` | header `G64` |
| Cutter comp off | `G40` | |
| Workplane XY/XZ/YZ | `G17` / `G18` / `G19` | `G17` |
| Spindle CW / CCW / off | `M3` / `M4` / `M5` | `M3`, `M5` |
| End rewind | `M30` | last line |
| NumberFormat | `0.0###` (≥1, ≤4 decimals) | `3.0`, `0.2`, `1.2929`, `F300.0`; max 4 decimals in 6389 lines |
| Arc centre mode | incremental, I/J = centre − start point | `G3 X-1.0 Y50.0 I0.0 J-1.0` from (0,51): centre (0,50) |
| Line numbers | off | |
| End of line | CRLF | |

Expansion rules (inferred):

1. Evaluate macros in each section; a line whose result is empty or whitespace-only is **dropped**
   (`{$cbfile.header}`, `{$mop.header}`, `{$mop.footer}`, `  ` before `</MOP>` produce no lines).
2. Within a line, empty tokens leave no extra spaces: runs of spaces collapse to one and the line is trimmed
   (`{$g1} {$_f} {$_x} {$_y} {$_z} {$_a}…` → `G1 F300.0 Z-0.4`). Output contains no double spaces and no
   trailing blanks.
3. Modal state (G word for `_g`, X/Y/Z/F/… for `_x` etc., workplane, spindle state, velocity mode) persists
   across MOPs and nest copies.
4. All output is upper-cased only when `UpperCase=true`.

### 3.5 Reproducing Default / Mach3 output

* **Default.cbpp** (shipped):
  ```
  PostFile: {$comment} Made using CamBam - http://www.cambam.co.uk {$endcomment} / {$header} / {$mops} / {$footer}
  Header:   {$comment} {$cbfile.name} {$date} {$endcomment}
            {$comment} Post processor: Default {$endcomment}
            {$tooltable}
            {$cbfile.header}
            {$units} {$distancemode} {$velocitymode} {$cuttercomp(off)}
            {$toolchange(first)}
            {$clearance}
  Footer:   {$clearance} / {$spindle(off)} / {$cbfile.footer} / {$endrewind}
  MOP:      {$comment} {$mop.name} {$endcomment} / {$toolchange} / {$velocitymode} {$workplane} /
            {$mop.header} / {$spindle} {$s} / {$blocks} / {$mop.footer}
  MinimumArcLength 0.0001, MaximumArcRadius 10000
  ```
* **Mach3.cbpp** = Default plus `( Post processor: Mach3 )`, an extra header line `{$arccentermode} `
  (→ `G91.1`), `ArcCenterIncremental=G91.1`, `ArcCenterAbsolute=G90.1`, MinimumArcLength 0.0001 and
  default MaximumArcRadius.
* Mach3-CutViewer adds CutViewer comment lines (`FROM/0,0,5`, `TOOL/MILL,1,0,20.0,0`,
  `STOCK/BLOCK,{$stock_width},…`) and a ToolChange with `TOOL/MILL,{$tool.diameter},{$tool.radius},{$tool.length},0`.
  `+G43` variant emits `T{$tool.index} M6 G43 H{$tool.index}` then `{$set(z,NaN)}` and `{$clearance}`.

Expected Default-post skeleton for a single metric MOP (current Default.cbpp):

```
( Made using CamBam - http://www.cambam.co.uk )
( <name> <date> )
( Post processor: Default )
( T1 : 2.0 )
G21 G90 G64 G40
G0 Z3.0
( T1 : 2.0 )
T1 M6
( Profile1 )
G17
M3 S1000          <- expected from {$spindle} {$s}; the 2012 sample shows bare "M3" (see 4.3)
G0 X.. Y..
...
G0 Z3.0
M5
M30
```

---

## 4. G-code analysis: `snap-together-box.cb` → `snap-together-box.nc`

### 4.1 Inputs (from the .cb)

* One closed `pline` id 244, 44 vertices, castellated 60×60 square (perimeter 272), drawn **clockwise**.
* MachiningOptions: no `PostProcessor` (⇒ default post), `FastPlungeHeight 0.2`, ToolDiameter 2, ToolNumber 1,
  stock `-10,-10,-3.5` .. `215,145`.
* One `profile`: Outside, Conventional, CutWidth 0 (⇒ single pass), TargetDepth −3.5, DepthIncrement 0.4,
  FinalDepthIncrement 0, DepthFirst, StockSurface 0, ClearancePlane 3, Plunge 300, Cut 800, CornerOvercut **True**,
  LeadIn/Out none, HoldingTabs Manual, Square, W 0.8, H 0.8, 6 tabs; SpindleSpeed 1000 CW.
* Part nesting: Grid 2 rows × 3 columns, spacing 8, 6 `NestingItem`s at (0,0) (70,0) (140,0) (140,70) (70,70) (0,70).

### 4.2 Output facts

* 6389 lines, CRLF, final CRLF, no `%`, no line numbers, no blank lines, no double spaces.
* Words: G1 4914, G3 1296, G0 163, comments 10, plus single `G21`, `T1`, `G17`, `M3`, `M5`, `M30`. **No G2**.

Header / footer (verbatim):

```
( Made using CamBam - http://www.cambam.co.uk )
( snap-together-box 10/23/2012 11:49:42 PM )
( T1 : 2.0 )
G21 G90 G64 G40
G0 Z3.0
( T1 : 2.0 )
T1 M6
( Profile1 )
G17
M3
G0 X1.0 Y60.0
G0 Z0.2
G1 F300.0 Z-0.4
G1 F800.0 Y51.0
...
G3 X1.0 Y130.0 I0.0 J-1.0
G0 Z3.0
M5
M30
```

### 4.3 Which post produced it

The structure matches **Default.cbpp** with the built-in defaults of §3.4 (DefaultPostProcessor=`Default` in
config; no PostProcessor in the .cb). It is *not* Mach3/Mach3-CutViewer: no `G91.1` line and no CutViewer
comments. Differences from today's Default.cbpp: no `( Post processor: Default )` line and `M3` without `S1000`
– the file dates from 2012 (CamBam 0.9.8), whose built-in Default post evidently lacked that comment line and
did not output `{$s}` (or `{$s}` was empty). To be byte-compatible with this sample, BamCam's "legacy default"
post = Default.cbpp minus the "Post processor" header line and minus `{$s}`. Number format `0.0###`.

### 4.4 Emission rules observed

**Start of a MOP / nest copy**

* First copy: header ended at clearance (`G0 Z3.0` from the first tool change), MOP block emits
  `( Profile1 )`, `G17` (workplane), `M3` (spindle), then rapid XY `G0 X1.0 Y60.0` (only changed axes,
  X and Y on one line), then rapid down `G0 Z0.2` (= StockSurface 0 + FastPlungeHeight 0.2), then plunge
  `G1 F300.0 Z-0.4` at plunge feed.
* Each further nest copy re-emits the MOP comment `( Profile1 )` then `G0 Z3.0` (retract to clearance),
  `G0 X71.0` (modal: Y unchanged), `G0 Z0.2`, plunge. `G17` and `M3` are **not** repeated (modal / state unchanged).
  Copies are output sequentially = GCode order "nest each MOP"/auto for a single MOP.

**Depth levels**

* Levels: −0.4, −0.8, −1.2, −1.6, −2.0, −2.4, −2.8, −3.2, then −3.5 (step 0.4 from StockSurface, last level
  clamped to TargetDepth; FinalDepthIncrement 0 = no special last step). 9 levels per copy.
* DepthFirst on a closed loop: after completing a lap the tool is back at the start point; the next level is a
  straight plunge `G1 F300.0 Z-0.8` there (no retract, no ramp), followed by `G1 F800.0 …`.
* Rapid-down rule: before a plunge, a rapid is used down to (previous cut level + FastPlungeHeight) **only if that
  height is below the current Z**; otherwise feed directly. (At level −3.5 after a tab: `G0 Z-3.0` = −3.2+0.2, then
  `G1 F300.0 Z-3.5`; at level −3.2 after a tab: −2.8+0.2 = −2.6 is above the tab top −2.7 ⇒ direct `G1 F300.0 Z-3.2`.)

**Feed rates**

* F is modal and printed with the number format (`F300.0`, `F800.0`).
* Every plunge (vertical feed) switches to PlungeFeedrate; the next XY move switches back to CutFeedrate.
* Rapids never print F.

**Toolpath geometry**

* Outside + Conventional with a CW spindle ⇒ travel **counter-clockwise** around the part (the drawn CW
  polyline is reversed). Start point = first polyline vertex (2,60) offset by tool radius → (1,60).
* Convex corners of the offset path become arcs of radius = tool radius (1.0) → all `G3` (CCW), e.g.
  `G3 X-1.0 Y50.0 I0.0 J-1.0`. Straight edges → `G1`.
* Arc words: G-word every line, X/Y modal, I and J **always** both printed (even `I0.0`), K omitted (XY plane),
  I/J incremental from arc start (centre − start).
* Corner overcut at each concave corner: the path goes from the corner point toward the sharp drawing corner along
  the bisector until the tool touches it, and back: `G1 X1.0 Y51.0` → `G1 X1.2929 Y50.7071` → `G1 X1.0 Y51.0`
  (corner (2,50), tool radius 1: 2 − 1·0.7071, 50 + 0.7071).

**Holding tabs (Square)**

* Tab top Z = TargetDepth + Height = −3.5 + 0.8 = **−2.7**. Levels above −2.7 are unaffected; levels −2.8,
  −3.2, −3.5 are interrupted at each tab.
* Tab gap length along the path = Width + tool diameter = 0.8 + 2 = 2.8 (e.g. `Y26.4545` → `Y23.6545`), centred on
  the `ParametricPoint` location.
* Sequence at a tab: `G1` to tab start, **`G0 Z-2.7`** (rapid up), `G1` across at −2.7 (cut feed, modal), then back down
  (`G1 F300.0 Z<level>` or `G0 Z<prev+0.2>` + `G1 F300.0 Z<level>` per the rapid-down rule), then `G1 F800.0 …`.

**Lead-ins/outs**: none in this sample (LeadInMove state Default, empty ⇒ None ⇒ direct plunge at plunge feed).

**Tool change**: only once in the header via `{$toolchange(first)}`: `G0 Z3.0` (clearance) / `( T1 : 2.0 )` /
`T1 M6`; the header's own `{$clearance}` then emits nothing (already at Z3). MOP `{$toolchange}` emits nothing
(same tool). Tool table line `( T1 : 2.0 )`.

**Number formatting**: `0.0###` – at least one decimal, at most four, rounded (`1.29289…` → `1.2929`,
`7.43519…` → `7.4352`); integers print as `3.0`; no `-0.0` produced; no leading `+`; no padding of G/M words
(`G0`, `G1`, `M3`, not `G00`).

**End**: last lap ends at the start point; footer `G0 Z3.0` (clearance), `M5`, `M30` (cbfile.footer empty).

---

## 5. Library and configuration files

### 5.1 CAM style libraries – `styles\<Library>.xml`

Library name = file base name (`Standard-mm`, `Standard-in`). Lookup order when the drawing has no
StyleLibrary: `{$Material}-{$Units}` (if stock material set) then `Standard-{$Units}`.

```xml
<?xml version="1.0" encoding="utf-8"?>
<CAMStyleLibrary Version="0.9.8.0">
<CAMStyle Name="">
<Style />
<OptimisationMode state="Value">Experimental</OptimisationMode>
<ToolDiameter state="Auto">0</ToolDiameter>
...
</CAMStyle>
<CAMStyle Name="cutout+tabs">
<Style>cutout</Style>
<HoldingTabs state="Value"> ... </HoldingTabs>
</CAMStyle>
</CAMStyleLibrary>
```

* Root `CAMStyleLibrary@Version="0.9.8.0"`; children `CAMStyle@Name`. `Name=""` is the **root/default style**
  (must exist, holds the full default set).
* First child `Style` = parent style name (empty ⇒ root style). Inheritance: property missing in a style ⇒ ask parent.
* Properties use the same element names and composite structures as MOPs, with `state="Value"` or `state="Auto"`
  (never `Default` – a style simply omits inherited properties).
* Root style property set (mm file, values): OptimisationMode Value Experimental; ToolDiameter Auto 0; ToolNumber Auto 0;
  ToolProfile Auto Unspecified; PlungeFeedrate Value 300; CutFeedrate Value 800; StockSurface Auto 0;
  ClearancePlane Value 3; SpindleDirection Value CW; SpindleSpeed Value 1000; TargetDepth Auto 0;
  DepthIncrement Value 0.4; CutOrdering Value DepthFirst; CollisionDetection Value True; StepOver Value 0.4;
  MaxCrossoverDistance Value 0.7; HoleDiameter Auto 0; DrillingMethod Auto CannedCycle; RetractHeight Auto 0;
  HoldingTabs Value {None, 3, 3, 3, 6, 40, 4, False, Triangle}; **XStepover** Value 0.5; **YStepover** Value 0.5;
  StartCorner Value BottomLeft; Resolution Value 0.2; BackFaceCulling Value True.
  (Inch file: Plunge 10, Cut 30, Clearance 0.125, DepthIncrement 0.015, tabs 0.1/0.1/…/1.6/0.2, X/YStepover 0.02.)
* `XStepover`, `YStepover` exist only in style libraries (not in any .cb) – keep them.
* Shipped styles: `""`, `3D finish`, `cutout`, `cutout+tabs` (parent cutout), `cutout+tabs+corners` (parent cutout+tabs),
  `engrave`, `PCB Isolate`, `VCorner`. LeadInType `None` appears here.

### 5.2 Tool libraries – `tools\<Library>.xml`

```xml
<?xml version="1.0" encoding="utf-8"?>
<ToolLibrary Version="0.9.8.0" ToolNameFormat="{$diameter}mm {$flutes} flute {$profile}" NumberFormat="0.00">
<ToolDefinition Name="1.00mm 2 flute EndMill">
<Index>1</Index>
<Diameter>1</Diameter>
<ToolProfile>EndMill</ToolProfile>
<Flutes>2</Flutes>
<FluteLength>0</FluteLength>
<Length>0</Length>
<ShankDiameter>0</ShankDiameter>
<HelixAngle>0</HelixAngle>
<VeeAngle>0</VeeAngle>
<MaxRampAngle>0</MaxRampAngle>
<ToothLoad>0</ToothLoad>
<AxialDepthOfCut>0</AxialDepthOfCut>
<RadialDepthOfCut>0</RadialDepthOfCut>
[<Comment>{$comment} TOOL/STANDARD,40,40,{$tool.radius},2,3 {$endcomment}</Comment>]
</ToolDefinition>
</ToolLibrary>
```

* Root attrs: `Version`, `ToolNameFormat` (optional; absent in Lathe-mm; `&quot;` escapes in inch file:
  `{$diameter}&quot; {$flutes} flute {$profile}`), `NumberFormat` (`0.00` mm, `0.0##` in) used when building names.
* `ToolDefinition@Name` (e.g. `45 deg V Cutter`, `0.04" 2 flute EndMill`, `RH 1mm radius`). Definitions are not sorted
  by Index (Index 10 before 6; inch file 1,2,5,4,3).
* Children (fixed order): `Index` (tool number; lookup key), `Diameter`, `ToolProfile` (`EndMill`, `VCutter`,
  `Unspecified`, `Lathe`), `Flutes`, `FluteLength`, `Length`, `ShankDiameter`, `HelixAngle`, `VeeAngle`,
  `MaxRampAngle`, `ToothLoad`, `AxialDepthOfCut`, `RadialDepthOfCut`, optional `Comment` (post-processor text for
  `{$tool.comment}`; may contain macros).
* Lookup: MOP ToolNumber → Part ToolLibrary → Machining ToolLibrary → `Default-{mm|in}`. MOP explicit diameter/profile win.

### 5.3 `CamBam1.0.config` (root `CamBamConfig`, xsi/xsd namespaces, 2-space indent)

Relevant settings (values shipped):

| Element | Value | Use in BamCam |
|---|---|---|
| `DefaultPostProcessor` | `Default` | post used when drawing has none |
| `OutFilePrompt` | `IfExists` | prompt before overwriting G-code |
| `DefaultLayerColor` | `127,255,0` | new layer colour (matches `Default` layer in all samples) |
| `DefaultStockColor` | `255,165,0` | stock colour |
| `ViewBackColor` / `SelectColor` / `MarkerColor` / `CutWidthColor` / `ViewTextColor` / `GridColor` | `50,50,60` / `255,0,0` / `255,255,0` / `0,139,139` / `255,255,0` / `65,65,65` | UI theme |
| `ToolpathLineColor` / `ToolpathArcColor` / `ToolpathRapidColor` | `35,85,205` / `0,220,0` / `255,69,0` | toolpath display |
| `ToolpathViewFilter` | `CutToolpathColor 255,0,0,120`, `ToolpathColor 255,255,0`, `ToolpathLineWidth 0` | |
| `GridInfo_Metric` | `DrawingUnits Millimeters`, `Minimum -50,-50`, `Maximum 50,50`, `MajorScale 10`, `MinorScale 1` | minor grid = default fast-plunge unit |
| `GridInfo_Inches` | `Inches`, `-3,-3`, `3,3`, `1`, `0.0625` | |
| `ArcDisplayDegrees` | `2.5` | arc tessellation for display |
| `TextCurveTolerance` | `0.5` (font units) | text → polyline |
| `SplineToPolylineTolerance` | `0.001` | |
| `SplineCurveSteps` | `12` | |
| `BackfaceCulling` | `false` | |
| `Export3DPolylines` | `false` | DXF export |
| `DefaultScriptEntity` | `this.Entities.Add(CADUtils.GetCamBamLogo(50 / Unit.GetFactor(doc.DrawingUnits)))` | |
| `ScriptTextStyle` | `Name default`, `color 120,255,180`, `backcolor 0,0,0`, `font Lucida Console;9.75;0` | editor font format `family;size;style` |
| `Language` | `de` | |
| `SaveSettingsOnExit` | `true` | |
| `formstates/state@name,x,y,w,h,state` + `usermetrics/user` | window geometry | UI only |
| `PropertyGridMetrics` | `ColumnWidth 150`, `ShowInfo true`, `ShowAdvanced false`, `GroupProperties true`, `InfoHeight 77` | UI only |
| `PrintOptions/Margins` | Bottom/Left/Right/Top `0` | |

`ExcellonIO.config`: root `ExcellonIOConfig`, child `ToolProfile` = `Drill` (tool profile for imported drill files).

Other system folder facts: `machinedefs\`, `materials\` are empty; `scripts\` holds `.py/.vbs/.js` scripts that
drawing scripts can import (e.g. `Gears`, `Solids`, `Pulleys`); backups are named `<file>.b<N>` (e.g. `translate_de.b1`).
