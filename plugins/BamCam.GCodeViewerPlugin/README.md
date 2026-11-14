# BamCam G-Code Viewer plugin

Adds **Machining ▸ Open in CutViewer** (with a toolbar button), **Open in NC Viewer** and **Open in CAMotics**, so you
choose what simulates the G-code.

One click generates missing toolpaths and writes the G-code of the drawing to a temporary file with the post processor
of the drawing.

- **CutViewer, NC Viewer** (in the browser): the file is shown in the file manager and the viewer opens. You then drag
  the file into the browser window. These sites document no way for another program to hand over a file, so this step
  stays with you.
- **CAMotics** (a separate desktop program, [camotics.org](https://camotics.org), install it first): the plugin also
  writes a CAMotics project next to the G-code and starts CAMotics with it, so the simulation opens at once with:
  - the **tools** of the enabled milling operations by tool number: end mill → cylindrical, ball nose → ball nose,
    V-bit and drill → cone of their angle (a V-bit with a tip diameter → snub nose), bull nose → ball nose when the
    corner radius is at least half the tool radius, else cylindrical (CAMotics has no bull nose);
  - the **stock** as workpiece, moved by the machining origin into G-code coordinates (without stock, or with nested
    copies, CAMotics sizes the workpiece around the toolpaths);
  - the units of the drawing.

  CAMotics is looked for in its usual places (`C:\Program Files\CAMotics\camotics.exe`, `/Applications/CAMotics.app`,
  `camotics` on the PATH). If it is not found, the plugin asks for the program once and remembers it in
  `camotics-path.txt` in the local application data folder (`%LOCALAPPDATA%\BamCam`). CAMotics simulates 3-axis
  milling only: lathe, plasma and drag knife operations are not shown.

No post-build command runs, so nothing is sent to a machine, and the output file of the drawing is not touched.

## Installing

The plugin comes with BamCam (from 0.9.1-beta.7): it is in the `plugins` folder next to the program and runs without
asking; **Tools ▸ Plugins** switches it off. To try a newer build of it, build it:

```bash
dotnet build plugins/BamCam.GCodeViewerPlugin -c Release
```

Then copy `BamCam.GCodeViewerPlugin.dll` and `BamCam.GCodeViewerPlugin.deps.json` from
`plugins/BamCam.GCodeViewerPlugin/bin/Release/net10.0` into a folder below the plugin folder (**Tools ▸ Plugins ▸ Open
Plugin Folder**), restart BamCam and allow the plugin. A copy in that folder is used instead of the one that comes with
BamCam; delete it again to go back to the bundled one.

## Other viewers

The list of viewers is at the top of `ViewerPlugin.cs`: a name, an address and the menu text with its access key.

## The sites

Both are third-party sites and not part of BamCam; check their terms for your use.

- **CutViewer** says it is free and processes files in the browser. It shows 3-axis milling and routing programs, not
  lathe programs.
- **CAMotics** is open source (GPL) and runs on your computer; the plugin only writes its project file and starts it.
- **NC Viewer** says files are processed locally and are not sent to its servers. Its terms limit the licence to
  personal, non-commercial viewing, and the site loads analytics and advertising services.
