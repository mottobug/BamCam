# Writing BamCam plugins

A BamCam plugin is a .NET 10 class library (C#, F#, VB.NET) that references `BamCam.PluginApi`.
It can add

- **commands** to the Draw, Edit or Machining menu or to its own *Plugins* menu, which read and change the
  drawing with undo, and
- **machining operation types** with their own parameters (shown in the property grid and saved in drawings)
  and their own toolpath generator.

CamBam plugins do not run in BamCam: they are built against CamBam's own assemblies. Porting one means
replacing CamBam's API calls with the interfaces described here; see
[Porting CamBam plugins](porting-cambam-plugins.md) for a step-by-step guide.

The complete example is [`samples/BamCam.SamplePlugin`](../samples/BamCam.SamplePlugin): a *Regular Polygon*
command and a *Facing* operation.

## Project

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

## Installing

Copy the build output (the plugin DLL, its `.deps.json` and its own dependencies) into a folder below

- Windows: `%APPDATA%\BamCam\plugins\`
- Linux and macOS: `~/.config/BamCam/plugins/`

Plugins can also be bundled in a `plugins` folder next to the BamCam program.

At the next start BamCam asks once per new or changed plugin file whether it may be loaded, showing product,
company and version. Plugins run with the same rights as BamCam; the approval is bound to the file's SHA-256,
so a changed file is asked about again. **Tools ▸ Plugins** lists the plugins with their state and switches them
on or off (after a restart). Discovery only reads the DLL's metadata; plugin code runs only after approval.
