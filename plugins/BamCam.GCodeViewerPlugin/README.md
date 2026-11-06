# BamCam G-Code Viewer plugin

Adds **Machining ▸ Open in CutViewer** (with a toolbar button) and **Open in NC Viewer**.

One click generates missing toolpaths, writes the G-code of the drawing to a temporary file with the post processor of
the drawing, shows the file in the file manager and opens the viewer in the browser. You then drag the file into the
browser window. These sites document no way for another program to hand over a file, so this step stays with you.

No post-build command runs, so nothing is sent to a machine, and the output file of the drawing is not touched.

## Installing

Copy `BamCam.GCodeViewerPlugin.dll` and `BamCam.GCodeViewerPlugin.deps.json` into a folder below the plugin folder
(**Tools ▸ Plugins ▸ Open Plugin Folder**), restart BamCam and allow the plugin. The file is attached to each release
as `BamCam.GCodeViewerPlugin-<version>.zip`.

To build it yourself:

```bash
dotnet build plugins/BamCam.GCodeViewerPlugin -c Release
```

## Other viewers

The list of viewers is at the top of `ViewerPlugin.cs`: a name, an address and the menu text with its access key.

## The sites

Both are third-party sites and not part of BamCam; check their terms for your use.

- **CutViewer** says it is free and processes files in the browser. It shows 3-axis milling and routing programs, not
  lathe programs.
- **NC Viewer** says files are processed locally and are not sent to its servers. Its terms limit the licence to
  personal, non-commercial viewing, and the site loads analytics and advertising services.
