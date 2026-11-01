# Porting CamBam plugins to BamCam

CamBam plugins do not run in BamCam as they are: they are .NET Framework assemblies built against CamBam's own
DLLs and Windows Forms. A port keeps the plugin's logic (geometry, calculations, toolpath generation) and replaces
everything that talks to CamBam with the BamCam plugin interface described in [plugins.md](plugins.md).
For most drawing and machining plugins this is a matter of hours, not a rewrite.

> BamCam is an independent implementation and does not contain CamBam code. Port your own plugins, or plugins
> whose licence allows it.

## 1. New project

Create a .NET 10 class library instead of the .NET Framework project:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Nullable>enable</Nullable>
    <RestoreAdditionalProjectSources>C:\packages\bamcam</RestoreAdditionalProjectSources>
    <Product>My Plugin</Product>
    <Company>My Name</Company>
    <Version>2.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BamCam.PluginApi" Version="0.7.0" ExcludeAssets="runtime" />
    <PackageReference Include="BamCam.Core" Version="0.7.0" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

The two `.nupkg` files are attached to every BamCam release on GitHub; put them into the folder named in
`RestoreAdditionalProjectSources`. See [plugins.md](plugins.md#project) for referencing the DLLs of an
installation instead.

- Remove the references to CamBam's DLLs and to `System.Windows.Forms`.
- Copy the source files over and let the compiler show every place that used CamBam: that is the list of
  things to port.
- Plugins written in VB.NET work the same way (`Microsoft.VisualBasic` is part of .NET 10, also on Linux and
  macOS, no extra package needed).

## 2. Entry point

A CamBam plugin registers itself in a static initialisation method that receives CamBam's main UI object and adds
Windows Forms menu items. In BamCam a public class implements `IBamCamPlugin`; BamCam calls `Initialize` once and
creates the menu entries itself:

```csharp
public sealed class GearPlugin : IBamCamPlugin
{
    public void Initialize(IPluginHost host)
    {
        host.AddCommand(new PluginCommand
        {
            Header = host.Language == "de" ? "_Zahnrad..." : "_Gear...",
            Menu = "Draw",                 // or "Plugins", "Edit", "Machining"
            Execute = DrawGear,            // async ctx => { ... }
        });
    }
}
```

There is no access to BamCam's window, menus or toolbars: a command is a menu entry plus an action.

## 3. Concepts side by side

| In a CamBam plugin | In BamCam |
|---|---|
| Static init method, menu items added to CamBam's menus | `IBamCamPlugin.Initialize`, `host.AddCommand(new PluginCommand { ... })` |
| Menu item click handler | `PluginCommand.Execute`, an `async` function receiving an `ICommandContext` |
| The active drawing (CAD file) of the active view | `ctx.Document` (`CadDocument`: `Layers`, `ActiveLayer`, `Parts`, `AllEntities()`, `FindEntity(id)`) |
| The selected entities of the view | `ctx.Selection` |
| Adding entities to the active layer | `ctx.AddEntities("Draw gear", entities)` – one undoable step, selects them |
| Changing entities, then an undo point / refresh | `ctx.Change("Description", () => { /* change */ })` – undo point and refresh in one |
| Entity classes (polyline, circle, arc, line, point list, region, text) | `PolylineEntity`, `CircleEntity`, `ArcEntity`, `LineEntity`, `PointListEntity`, `RegionEntity`, `TextEntity`, `SplineEntity`, `SurfaceEntity` (namespace `BamCam.Core.Model`) |
| Single-precision points (`Point3F`-style) and vectors | `Point3` / `Point2` with `double` coordinates (`BamCam.Core.Geometry`) |
| Polyline segments with bulge | `Path2` with `Vertex(point, bulge)`; `entity.ToPaths()` gives any entity's geometry in drawing coordinates |
| Entity transformation matrix | `entity.Transform` (`Matrix4`); `ApplyTransform` bakes it in |
| Windows Forms input dialogs | `await ctx.AskNumber(...)`, `await ctx.AskText(...)`; for more, a small Avalonia window of your own |
| Mouse handling in the drawing view (picking points, rubber band) | `ctx.StartTool(new MyTool())` with a `PluginTool` (`Clicked`, `Moved`, `KeyPressed`, `Preview`) |
| Own file import / export | `host.AddImporter` / `host.AddExporter` with a `FileFormat` |
| Own post processor macros | `host.AddPostMacro("vendor.name", m => ...)` |
| Message boxes / CamBam's message window | `ctx.Log("...")` (and `host.Log` during initialisation) |
| CamBam's configuration settings | not exposed; keep the plugin's own settings in its own file (e.g. JSON next to the DLL) |
| Machining operation class derived from CamBam's MOP base class | class derived from `MachineOp` or `DepthOp`, registered with `host.AddOperation<TOp>(generator)` |
| MOP properties with CamBam's parameter wrapper types | `CamParam<T>` properties with `[Param(order, "Category")]` |
| Toolpath generation inside the MOP | a static generator `(ToolpathContext ctx, TOp op, Toolpath tp)` that adds `Move.Rapid / Line / Arc` |

Useful helpers in `BamCam.Core` instead of hand-written geometry code: `Polygons` (offsets, booleans; Clipper2),
`CadOps` (explode, join, convert to polylines, bounds), `Arc2` (arcs from bulge or three points), `Nurbs`
(splines) and `ObjectSnap.Intersections` (line/arc intersections).

## 4. Machining operations: keep drawings compatible

CamBam saves plugin operations as `<mop xsi:type="TypeName">` with one element per parameter. BamCam uses the same
format, so drawings made with the CamBam version of your plugin open with the BamCam port if

- `XmlName` returns exactly the type name the CamBam plugin wrote (look into a `.cb` file saved with it), and
- each `CamParam<T>` property is named like the XML element of the parameter (or sets `XmlName` in the
  `[Param]` attribute).

Values BamCam does not know are kept unchanged when the drawing is saved again. Common parameters (tool diameter,
feeds, clearance plane, stock surface, depth levels, spindle speed …) come from the `MachineOp` / `DepthOp` base
classes with CamBam's element names; do not declare them again.

```csharp
public sealed class FacingOp : DepthOp
{
    public override string XmlName => "MOPFacing";      // exactly as in drawings saved by the CamBam plugin
    public override string DisplayType => "Facing";

    [Param(60, "Facing")] public CamParam<double> StepOver { get; set; } = new(0.7);
}
```

The generator receives resolved values (`ctx.StockSurface`, `ctx.Clearance`, `ctx.ToolDiameter`, feeds,
`ctx.DepthLevels(op)`) and the source geometry (`ctx.SourcePaths()`), and appends moves in drawing coordinates.
G-code comes from the post processor, as for the built-in operations: do not write G-code text in the plugin.

## 5. What is not possible (yet)

- Docked panels, or access to BamCam's window and menus (toolbar buttons: give the command an `Icon`).
- Drawing object types other than parametric outlines (`ParametricEntity`), e.g. objects with their own rendering.

Ask in the issues if your plugin needs one of these.

Some popular CamBam plugins are already built into BamCam: snap toolbar and object snap modes, dimensioned
drawing (typed lengths, angles, diameters), fillet / chamfer, trim / extend, align toolbar, text on polyline.

## 6. Test and publish

1. Build, copy the output (plugin DLL, `.deps.json`, own dependencies) into a folder below
   `%APPDATA%\BamCam\plugins\` (Linux and macOS: `~/.config/BamCam/plugins/`).
2. Start BamCam and allow the plugin; it appears in **Tools ▸ Plugins** and in the chosen menu.
3. For operations: open a drawing saved with the CamBam version and check that the operation and its values load,
   then compare the G-code with CamBam's output.

The working example is [`samples/BamCam.SamplePlugin`](../samples/BamCam.SamplePlugin) (a drawing command and a
facing operation).
