# Example (Square)

An example community plugin: **Draw ▸ Square (Example)...** asks for a side length and draws a square of that size
around the origin. It does nothing you could not draw yourself; it is here to show what a community plugin looks like
and to try **Tools ▸ Plugins ▸ Install from ZIP...**.

- **Author:** BamCam project (GitHub: mottobug)
- **Version:** 1.0.0
- **Licence:** MIT
- **Needs BamCam:** 0.9.2 or later

## How to use it

1. Download `community-BamCam.Example-<version>.zip` from the assets of a BamCam release.
2. **Tools ▸ Plugins ▸ Install from ZIP...**, choose the file, restart BamCam and allow the plugin.
3. **Draw ▸ Square (Example)...**, enter the side length: the square is drawn on the active layer and selected, as one
   step to undo.

## For plugin authors

It was made from [`_Template`](../_Template): the folder and the `.csproj` renamed to `BamCam.Example`, the namespace
changed, Product, Company and the menu text filled in. Do the same for your own plugin, see
[Sharing a plugin](../../../docs/plugins.md#sharing-a-plugin).

## Changes

- 1.0.0: first version.
