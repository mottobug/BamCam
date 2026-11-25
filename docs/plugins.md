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

**Own editor:** by default a double-click on such an object edits its points. With an editor, a double-click (in the
drawing or in the Drawing tab) opens it instead, e.g. a window with all parameters and a preview:

```csharp
host.AddEntityEditor<GearEntity>(async (ctx, gear) =>
{
    var values = new GearEntity();
    values.CopyValues(gear);                        // your own method: the window changes a copy
    if (await GearWindow.Show(values))              // true on OK
        ctx.Change("Change gear", () => { gear.CopyValues(values); gear.Invalidate(); });   // one undo step
});
```

### Windows with many values

For one or two values `ctx.AskNumber` and `ctx.AskText` are enough. For many values (8 or 10, as many CamBam plugins
have) **`ctx.AskValues`** shows a window without any designing: write a class with a property for each value, and
BamCam shows a field for each, as in its property grid.

```csharp
public sealed class RevolveValues
{
    [DisplayName("Angle"), Description("Degrees, more than 0 and at most 360.")]
    public double Angle { get; set; } = 360;
    [Category("Mesh"), Description("Number of steps around the axis.")]
    public int Segments { get; set; } = 36;
    [Category("Mesh"), DisplayName("Close Ends")]
    public bool CloseEnds { get; set; } = true;
    public Direction Direction { get; set; }           // an enum: a list to choose from
}

private static readonly RevolveValues Last = new();    // keeps the values for the session

Execute = async ctx =>
{
    if (!await ctx.AskValues("Revolve", Last, preview: () => BuildOutlines(Last))) return;   // false = Cancel
    // use Last.Angle, Last.Segments, ...
};
```

- Every public property with a getter and a setter is a field: numbers, yes/no (a check box), an enum (a list to
  choose from) and text. `[DisplayName]` sets the label, `[Description]` the help shown below the fields,
  `[Category]` groups them (`using System.ComponentModel;`).
- The fields change the object directly; **Cancel** puts back what it held before. Keeping the object in a `static`
  field keeps the values between uses in a session.
- `preview` is optional: a function that returns outlines (`Path2`, drawing coordinates) for the current values. A
  picture beside the fields shows them and is redrawn after every change. It may return null while the values do
  not make a shape.
- Scripts of the Scripts menu can use `AskValues` the same way.
- Labels and descriptions are shown as written. A label that is also one of BamCam's own texts (such as "Points")
  appears in the user interface language.

**An own window** is for a layout of your own (as the Squircle plugin has). It is built with BamCam's user interface
library [Avalonia](https://avaloniaui.net) (Windows Forms is not available: BamCam also runs on Linux and macOS).
Add the Avalonia package as described under [For plugin authors](#for-plugin-authors) and build the window in code,
or in XAML. The *Avalonia for Visual Studio* extension shows a live preview of the XAML while you type it; there is no
drag-and-drop designer as for Windows Forms.

The plugin **Squircle** that comes with BamCam is a complete example:
[`plugins/BamCam.SquirclePlugin/SquircleWindow.cs`](../plugins/BamCam.SquirclePlugin/SquircleWindow.cs) builds a
window in code with number fields, radio buttons, a live preview and Make / Close buttons:

- Show it with BamCam's main window as the owner, so that it stays in front:
  `Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner }`,
  then `await window.ShowDialog(owner)`.
- Let the window change a copy of the values, and change the drawing only on Make or OK, through
  `ctx.AddEntities` or `ctx.Change`, so that each is one undo step.
- To keep the values between uses in a session, keep the last ones in a `static` field (Squircle does that); to keep
  them in the drawing, use an object type (above), whose parameters are saved with it.

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

A plugin delivered as a ZIP file (such as a [community plugin](#sharing-a-plugin) from a release) is installed with
**Tools ▸ Plugins ▸ Install from ZIP...**:

- The ZIP is unpacked into a folder of its own in that folder, named after the file without its version:
  `community-GeoffRoy.Revolve-1.2.0.zip` goes to `GeoffRoy.Revolve`. A ZIP holding a single folder is unpacked without
  it.
- If that folder exists (an older version), BamCam asks before replacing it. A plugin that is loaded keeps its files
  open: it is then left as it is, and BamCam says to switch it off, restart and install again.
- A ZIP without a `.dll`, or with paths that would leave the folder (`../`), is refused.

BamCam asks at the next start whether the plugin may be loaded, as for a plugin copied by hand.

BamCam also loads plugins from a `plugins` folder next to the program, which is meant for plugins bundled with
BamCam. Those run without asking, because the program contains their SHA-256; a changed file is asked about like any
other. If the same plugin (same file name, product and company) is in several places, the one in your own folder is
used and BamCam warns about the others, which are not loaded. **Tools ▸ Plugins** shows the full path of each plugin.

At the next start BamCam asks once per new or changed plugin file whether it may be loaded, showing product,
company and version. Plugins run with the same rights as BamCam; the approval is bound to the file's SHA-256,
so a changed file is asked about again. **Tools ▸ Plugins** lists the plugins with their state and switches them
on or off (after a restart). Discovery only reads the DLL's metadata; plugin code runs only after approval.

## Sharing a plugin

Plugins written by users can live in [`plugins/community`](../plugins/community), one folder each, as source code.
The release build makes the DLLs: every BamCam release has a ready-made ZIP file per community plugin.

### Using a community plugin (nothing to build)

1. Open the [releases](https://github.com/mottobug/BamCam/releases) and, under **Assets** of the latest one,
   download `community-<Author>.<Name>-<version>.zip`.
2. In BamCam, **Tools ▸ Plugins ▸ Install from ZIP...** and choose the downloaded file. It is unpacked into a folder
   of its own in the plugin folder; a newer version replaces the older one.
3. Restart BamCam and allow the plugin when it asks.

No Visual Studio, .NET SDK or GitHub account is needed for this.

### For plugin authors

The steps below need only a GitHub account and the GitHub website; no Git program is needed.

**Where to develop:** wherever you like, e.g. in your own projects folder on any drive. Copy
[`_Template`](../plugins/community/_Template) there and rename it to `<Author>.<Name>`. Its `.csproj` builds against
your installed BamCam: BamCam installed in its default folder is found by itself; otherwise build once with
`-p:BamCamDir="folder of BamCam.exe\"` or set `BamCamDir` in the `.csproj`. Inside the BamCam repository the same
`.csproj` builds against BamCam's source code, which is what the release build does. So keep the template's `.csproj`
(only rename it and change Product, Company and Version): a `.csproj` with paths of your own computer would not build
there.

**Keep your BamCam current:** building against your installed BamCam means you can use what that version offers.
Examples and this guide follow the newest BamCam, including betas (Help ▸ Also Offer Betas). When the compiler says
that `IPluginHost` or `ICommandContext` has no member such as `AddEntityEditor` or `AskValues`, your installed BamCam
is older than the example: update it.

**Own windows or controls (Avalonia):** BamCam's user interface library is [Avalonia](https://avaloniaui.net). A plugin
that opens its own window uses BamCam's Avalonia: in the template's `.csproj`, remove the two comment lines around the
`ItemGroup` with `<PackageReference Include="Avalonia" ... ExcludeAssets="runtime" />`. That is all: no references to
Avalonia files or projects. `ExcludeAssets="runtime"` keeps the Avalonia DLLs out of the plugin's output, because
BamCam brings them. For simple questions `AskNumber` and `AskText` need no Avalonia at all.

### Adding a plugin

1. **Fork** the repository: on [github.com/mottobug/BamCam](https://github.com/mottobug/BamCam) click **Fork** (top
   right), then **Create fork**. You now have your own copy, `github.com/<your account>/BamCam`, which you may change
   freely.

   **Already have a fork from an earlier plugin?** Bring it up to date first, once your earlier pull request has
   been merged: on the start page of your fork click **Sync fork**. If it offers **Discard N commits**, click that;
   otherwise **Update branch**. The maintainer merges a pull request as one new commit, so your fork still holds your
   original commits of the earlier plugin. Without discarding them they come along into the next pull request and
   clash with BamCam's version of the same files (GitHub then reports a conflict, and the pull request cannot be
   merged). Discarding loses nothing: the earlier plugin is in BamCam by then, and the synced fork has it again.
2. In your fork, open `plugins/community`. Click **Add file ▸ Upload files** and drag the **folder** of your plugin into
   the page. Name the folder `<Author>.<Name>`, e.g. `GeoffRoy.Revolve`, and start from a copy of
   [`_Template`](../plugins/community/_Template): its `.csproj` already references BamCam correctly. Upload the source
   files (`.cs`, `.csproj`), not the `bin` and `obj` folders and no DLLs: the build makes the plugin from the source.
3. Still in that folder, **Add file ▸ Create new file**, name it `README.md` and describe the plugin: what it does,
   how to use it, author, version, licence (the template's `README.md` shows the parts). Markdown is plain text with
   a few marks: `#` for a heading, `-` for a list item, `**bold**`, `` `code` ``. The **Preview** tab shows how it will
   look.
4. Add a line for your plugin to the table in [`plugins/community/README.md`](../plugins/community/README.md): open
   it, click the pencil (**Edit**), add the line, **Commit changes**.
5. Open the pull request: on the start page of your fork click **Contribute ▸ Open pull request**, write a few words
   about the plugin and click **Create pull request**.

**Leave the pull request open.** It is your request to take the plugin into BamCam; closing it withdraws it. The
build then checks that the plugin builds (you see a green tick or a red cross in the pull request; for your first pull
request the maintainer starts the build by hand, so it may wait a while). The maintainer reviews it, may ask questions
in the pull request, and merges it. You can still change files in your fork while the pull request is open: the pull
request takes the changes and the build runs again.

Your fork holds only the source files, never a DLL: the plugin's ZIP with the DLL is made by the release build after
the merge and appears under **Assets** of the next BamCam release.

### Changing a plugin

1. In your fork, first click **Sync fork**, so that it has the current BamCam: **Discard N commits** if it offers that
   (see above), otherwise **Update branch**.
2. Open the file, click the pencil to edit it (or **Add file ▸ Upload files** to replace files), raise `<Version>` in
   the `.csproj` and add a line to *Changes* in your `README.md`, then **Commit changes**.
3. **Contribute ▸ Open pull request** as before.

### Trying your plugin before sharing it (authors only)

Build it in Visual Studio (open the `.csproj`) or with the [.NET 10 SDK](https://dotnet.microsoft.com/download):
`dotnet build -c Release` in its folder. Copy `<Author>.<Name>.dll` and `<Author>.<Name>.deps.json` from
`bin/Release/net10.0` into a folder below your plugin folder (**Tools ▸ Plugins ▸ Open Plugin Folder**) and restart
BamCam.
