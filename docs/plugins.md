# Writing BamCam plugins

A BamCam plugin is a .NET 10 class library (C#, F#, VB.NET) that references `BamCam.PluginApi`.
It can add

- **commands** to the Draw, Edit or Machining menu or to its own *Plugins* menu, which read and change the
  drawing with undo,
- **interactive drawing tools** that pick points with the mouse (snapped, with a rubber band preview),
- **machining operation types** with their own parameters (shown in the property grid and saved in drawings)
  and their own toolpath generator,
- **file formats** for File ▸ Open and File ▸ Export, and
- **post processor macros** (`{$name}`) for G-code templates.

CamBam plugins do not run in BamCam: they are built against CamBam's own assemblies. Porting one means
replacing CamBam's API calls with the interfaces described here; see
[Porting CamBam plugins](porting-cambam-plugins.md) for a step-by-step guide.

[`plugins/BamCam.GCodeViewerPlugin`](../plugins/BamCam.GCodeViewerPlugin) is a small real plugin: it gets the G-code
with `await context.PostGCode()`, without writing a file or running the post-build command, and opens it in a viewer
(CutViewer, NC Viewer or CAMotics). It comes with BamCam.

The complete example is [`samples/BamCam.SamplePlugin`](../samples/BamCam.SamplePlugin): a *Regular Polygon*
command, a *Star* tool (two clicks), a *Facing* operation, an *XYZ point list* file format and the macro
`{$sample.opcount}`.

## Project

The interface comes as two NuGet packages, `BamCam.PluginApi` and `BamCam.Core`, attached to every
[GitHub release](https://github.com/mottobug/BamCam/releases) (`*.nupkg`). Download both into a folder and add it as
a package source (or use `RestoreAdditionalProjectSources`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <RestoreAdditionalProjectSources>C:\packages\bamcam</RestoreAdditionalProjectSources>
    <Product>My Plugin</Product>
    <Company>My Company</Company>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <!-- Provided by BamCam at run time: compile against them, do not copy them (ExcludeAssets="runtime"). -->
    <PackageReference Include="BamCam.PluginApi" Version="0.7.0" ExcludeAssets="runtime" />
    <PackageReference Include="BamCam.Core" Version="0.7.0" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

Use the package version of the BamCam release you develop against; plugins keep working with later releases as
long as the interface stays compatible (the assemblies have the fixed version 1.0.0.0). Without the packages,
reference the DLLs of an installation instead:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <!-- Shown to the user when the plugin is approved and in Tools > Plugins -->
    <Product>My Plugin</Product>
    <Company>My Company</Company>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <!-- Provided by BamCam at run time: reference without copying -->
    <Reference Include="BamCam.PluginApi" HintPath="path\to\BamCam\BamCam.PluginApi.dll" Private="false" />
    <Reference Include="BamCam.Core" HintPath="path\to\BamCam\BamCam.Core.dll" Private="false" />
  </ItemGroup>
</Project>
```

`BamCam.PluginApi.dll` and `BamCam.Core.dll` are part of every BamCam installation (next to `BamCam.exe`;
on macOS in `BamCam.app/Contents/MacOS`). Inside this repository, reference the projects instead, as the sample does.
The plugin's own dependencies are copied next to it and are loaded in an isolated context; BamCam's assemblies
are shared with the application.

## Entry point

```csharp
public sealed class MyPlugin : IBamCamPlugin
{
    public void Initialize(IPluginHost host)
    {
        host.AddCommand(new PluginCommand
        {
            Header = "_Gear...",          // underscore = access key
            Menu = "Draw",                // "Plugins" (default), "Draw", "Edit" or "Machining"
            RequiresSelection = false,
            Execute = async ctx =>
            {
                var teeth = await ctx.AskNumber("Gear", "Number of teeth:", 20);
                if (teeth == null) return;
                ctx.AddEntities("Draw gear", [/* entities */]);
            },
        });
        host.AddOperation<MyOp>(MyGenerator.Generate);
    }
}
```

A command with an `Icon` (SVG path data in a 24 x 24 box, drawn as lines, e.g. `"M4,20 L12,4 L20,20 Z"`) also gets
a toolbar button. Commands with the same `ToolbarGroup` share one button with an arrow, which opens a menu of them (as
the align button does); it shows the icon of the first one that has one. The G-code viewer plugin puts its three viewers
behind one button this way.

`host.Language` gives the active user interface language ("en", "de", ...) for the plugin's own texts.

### Commands

`ICommandContext` gives the open `Document` (layers, entities, parts, operations) and the `Selection`.
Every change must run inside `ctx.Change(description, action)` (or `ctx.AddEntities`), which makes it one
undoable step and refreshes the view. `AskNumber` / `AskText` show simple input dialogs; `Log` writes to the
message window.

### Machining operations

```csharp
public sealed class MyOp : DepthOp            // DepthOp: Target Depth, Depth Increment, Final Depth Increment
{
    public override string XmlName => "MOPMyCompanyFacing";   // unique; the xsi:type in .cb files
    public override string DisplayType => "Facing";            // menu entry and tree label

    [Param(60, "Facing")] public CamParam<double> StepOver { get; set; } = new(0.7);
}
```

Parameters are `CamParam<T>` properties with `[Param(order, category)]`; they get the Default / Value / Auto
states, CAM style inheritance and the property grid editor like the built-in operations. In drawings the
operation is saved as `<mop xsi:type="MOPMyCompanyFacing">` (the format CamBam uses for plugin operations).
Drawings with an operation whose plugin is missing still open; the operation is kept unchanged.

An operation that cannot be cut without a tool diameter overrides `NeedsToolDiameter` to return `true`. When the
diameter is 0 and the tool number gives none, BamCam then reports it with its own (translated) message, as for a
pocket, and does not call the generator.

The generator receives a `ToolpathContext` with the resolved values (`StockSurface`, `TargetDepth`,
`Clearance`, `ToolDiameter`, feeds, `DepthLevels(op)`) and the source geometry `SourcePaths()`, and appends
moves in drawing coordinates:

```csharp
static void Generate(ToolpathContext ctx, MyOp op, Toolpath tp)
{
    foreach (var (entity, path) in ctx.SourcePaths()) { /* ... */ }
    tp.Add(Move.Rapid(new Point3(x, y, ctx.Clearance)));
    tp.Add(Move.Line(new Point3(x, y, z), ctx.PlungeFeed));
    tp.Add(Move.Arc(end, center, clockwise: true, ctx.CutFeed));
    tp.Warnings.Add("...");   // shown in the message window
}
```

The post processor turns the moves into G-code like those of the built-in operations.

### Interactive tools

A command can start a tool that picks points in the drawing, like BamCam's own drawing commands:

```csharp
Execute = ctx => { ctx.StartTool(new StarTool()); return Task.CompletedTask; },

sealed class StarTool : PluginTool
{
    Point2? _center;

    public override string Prompt => _center == null ? "Star: click the centre" : "Click the outer radius";

    public override void Clicked(IToolContext ctx, Point2 point, ToolModifiers modifiers)
    {
        if (_center is not { } c) { _center = point; ctx.SetPrompt(Prompt); return; }
        ctx.AddEntities("Draw star", [new PolylineEntity(Star(c, point))]);
        _center = null;
    }

    // Dashed rubber band up to the cursor.
    public override IEnumerable<Path2> Preview(IToolContext ctx) =>
        _center is { } c ? [Star(c, ctx.Cursor)] : [];
}
```

Points arrive in drawing coordinates, already snapped to the grid and to objects as the user switched it on.
`KeyPressed` receives keys by name ("A" … "Z", "0" … "9", "Up", "Back", "Escape", …) before BamCam's single-letter
shortcuts; Enter or a middle click call `Finished` (ends the tool by default), an unused Esc ends it.
`IToolContext` also offers `PickAt(point)` (objects under a point), `PixelSize`, `SetPrompt`, `Redraw` and `End`,
plus everything of a command (`Change`, `AddEntities`, `AskNumber`, …). An exception in the tool ends it with a
message instead of breaking BamCam.

### Object types

A plugin can add drawing objects whose geometry is computed from parameters:

```csharp
host.AddEntityType<GearEntity>();

public sealed class GearEntity : ParametricEntity
{
    public override string ParametricType => "acme.gear";   // unique, saved in drawings
    public override string TypeName => "Gear";              // name in the drawing tree

    [Category("Gear")] public int Teeth { get; set; } = 20;
    [Category("Gear")] public double Module { get; set; } = 2;

    protected override IEnumerable<Path2>? Generate() { /* outlines around the origin */ }
}
```

Public settable properties of simple types (numbers, bool, text, enums) are the parameters: they show in the
property grid, and changing one recomputes the outline. Moving, rotating and scaling work through the object's
transform. Create objects in a command with `ctx.AddEntities`.

Drawings store the object as ordinary polylines (the computed outlines, in drawing coordinates) plus an element
with type and parameters. CamBam and BamCam without the plugin therefore show the polylines and can machine them;
with the plugin the object is parametric again.

### File formats

```csharp
var format = new FileFormat("XYZ point list", ".xyz");
host.AddImporter(format, path => { var doc = new CadDocument(); /* read */ return doc; });
host.AddExporter(format, (doc, path) => { /* write */ });
```

Importers appear in File ▸ Open (the drawing replaces the open one; saving asks for a .cb file name), exporters in
File ▸ Export. The built-in formats (.cb, .dxf, .stl) keep their own readers.

A parametric object can show parameters depending on its values by overriding `HiddenParameters`, as
[`plugins/BamCam.SquirclePlugin`](../plugins/BamCam.SquirclePlugin) does for its two kinds of shape. The same shapes
as a script show [what each way can do](scripts.md#script-or-plugin).

A generator with long loops should check `ctx.Cancel` (a `CancellationToken`, set when the user cancels generating
toolpaths). If it does not, BamCam continues without its result, but the calculation keeps running until it ends.

### Post processor macros

```csharp
host.AddPostMacro("acme.serial", m => NextSerial(m.Document, m.Arguments));
```

Post processor templates can then use `{$acme.serial}` or `{$acme.serial(args)}`. The function gets the drawing,
the operation being posted (null in header and footer) and the arguments. Built-in macros and the post processor's
own macros take precedence: prefix the name with your vendor name.

A macro is not a command you run: it is replaced when the G-code is written. To try `{$sample.opcount}` of the
sample plugin, enter `(ops {$sample.opcount})` in **Custom File Header** of the machining options and produce the
G-code: the file then contains the number of enabled operations. A post processor can also use the macro in its own
templates, such as its Header.

## Installing

Copy the build output (the plugin DLL, its `.deps.json` and its own dependencies) into a folder below

- Windows: `%APPDATA%\BamCam\plugins\`
- Linux and macOS: `~/.config/BamCam/plugins/`

This is the folder that **Open Plugin Folder** in **Tools ▸ Plugins** opens, and the place for your own plugins.

BamCam also loads plugins from a `plugins` folder next to the program, which is meant for plugins bundled with
BamCam. Those run without asking, because the program contains their SHA-256; a changed file is asked about like any
other. If the same plugin (same file name, product and company) is in several places, the one in your own folder is
used and BamCam warns about the others, which are not loaded. **Tools ▸ Plugins** shows the full path of each plugin.

At the next start BamCam asks once per new or changed plugin file whether it may be loaded, showing product,
company and version. Plugins run with the same rights as BamCam; the approval is bound to the file's SHA-256,
so a changed file is asked about again. **Tools ▸ Plugins** lists the plugins with their state and switches them
on or off (after a restart). Discovery only reads the DLL's metadata; plugin code runs only after approval.
