# Plugin Demos (Example)

One menu entry for each way of writing a BamCam plugin, each in its own source file. Plugin authors look up the demo
they need, read its short explanation at the top of the file, and copy it. Users can install it to try the demos.

- **Author:** BamCam project (GitHub: mottobug)
- **Version:** 2.0.0
- **Licence:** MIT
- **Needs BamCam:** 0.9.4-beta.7 or later

## The demos

| File | Menu entry | Shows |
|---|---|---|
| [`Demo1_SimpleCommand.cs`](Demo1_SimpleCommand.cs) | Draw ▸ Regular Polygon (Demo)... | A command that asks one or two numbers (`AskNumber`) and draws. |
| [`Demo2_AskValues.cs`](Demo2_AskValues.cs) | Draw ▸ Bolt Circle (Demo)... | Many values in one window without designing it (`AskValues`): numbers, a check box, a list, text, with a preview; the values are kept for the session. |
| [`Demo3_OwnWindow.cs`](Demo3_OwnWindow.cs) | Draw ▸ Rounded Rectangle (Demo)... | A window of your own layout with Avalonia, with a preview. |
| [`Demo4_MouseTool.cs`](Demo4_MouseTool.cs) | Draw ▸ Star (Demo, 2 clicks), toolbar button | An interactive tool that picks points with the mouse, with a preview and keys. |
| [`Demo5_ChangeSelection.cs`](Demo5_ChangeSelection.cs) | Edit ▸ Raise / Lower Selection (Demo)..., toolbar button with a menu | Changing the selected objects as one undo step; two commands sharing a toolbar button. |
| [`Demo6_ParametricObject.cs`](Demo6_ParametricObject.cs) | Draw ▸ Gear (Demo) | An object computed from values: changed in the properties or by double-click (`AddEntityEditor` with `AskValues`). |
| [`Demo7_MachiningOperation.cs`](Demo7_MachiningOperation.cs) | Machining ▸ Facing (sample) | A machining operation with its own values and toolpaths. |
| [`Demo8_FileFormat.cs`](Demo8_FileFormat.cs) | File ▸ Open / Export, *XYZ point list* | Reading and writing a file type. |
| [`Demo9_GCode.cs`](Demo9_GCode.cs) | Machining ▸ G-code Statistics (Demo) | Reading the G-code of the drawing (`PostGCode`), and the post processor macro `{$sample.opcount}`. |

[`Plugin.cs`](Plugin.cs) registers them all. The full description of the plugin interface is
[docs/plugins.md](../../../docs/plugins.md).

## Trying it

1. Download `community-BamCam.Example-<version>.zip` from the assets of a BamCam release.
2. **Tools ▸ Plugins ▸ Install from ZIP...**, choose the file, restart BamCam and allow the plugin.
3. The demos are in the menus above. Remove the plugin again in **Tools ▸ Plugins** when you no longer need it.

## Starting your own plugin from it

Copy this folder, rename it and its `.csproj` to `<Author>.<Name>`, change the namespace, Product, Company and
Version, and keep the demos you need. The `.csproj` builds against your installed BamCam (as
[`_Template`](../_Template) does); it references Avalonia only for Demo 3, so drop that block if you have no own
window. Your installed BamCam must be at least the version named above, or the compiler does not know the newer
parts of the plugin interface. See [Sharing a plugin](../../../docs/plugins.md#sharing-a-plugin).

## Changes

- 2.0.0: nine demos, one per way of writing a plugin (the former sample plugin of the repository included).
- 1.0.0: first version (a square).
