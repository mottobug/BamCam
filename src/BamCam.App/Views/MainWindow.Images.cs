using Avalonia.Platform.Storage;
using BamCam.App.Services;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    private readonly HeightMapOptions _heightMapOptions = new();
    private readonly TraceOptions _traceOptions = new();

    private async Task<string?> PickImage()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Import Image"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(T("Images")) { Patterns = ImageImport.Patterns }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static (int W, int H) Size(float[,] gray) => (gray.GetLength(0), gray.GetLength(1));

    /// <summary>File ▸ Import Image ▸ As Height Map: brightness becomes the height of a surface for the 3D profile.</summary>
    private async void ImportHeightMap()
    {
        if (await PickImage() is not { } path) return;
        try
        {
            var gray = ImageImport.LoadGray(path);
            var (w, h) = Size(gray);
            var hint = F("{0}: {1} x {2} pixels. White is high (Top Z), black is Depth below it.", Path.GetFileName(path), w, h);
            if (!await new ImageImportDialog(T("Height Map"), _heightMapOptions, gray, hint).ShowFor(this)) return;
            var o = _heightMapOptions;
            var surface = ImageGeometry.HeightMap(gray, Math.Max(o.Width, 1e-3), o.Depth, o.TopZ, o.Resolution, o.Invert);
            _session.Execute(T("Import height map"), () => _session.Document.AddEntity(surface));
            _session.Select([surface]);
            Log(F("Height map {0}: {1} triangles. Select it and use Machining > 3D Profile.", Path.GetFileName(path), surface.Faces.Count / 3));
            View.ZoomToFit();
        }
        catch (Exception ex)
        {
            Log(F("Error opening {0}: {1}", path, ex.Message));
        }
    }

    /// <summary>File ▸ Import Image ▸ Trace Outlines: the outlines of dark (or light) areas become closed polylines or splines.</summary>
    private async void TraceImage()
    {
        if (await PickImage() is not { } path) return;
        try
        {
            var gray = ImageImport.LoadGray(path);
            var (w, h) = Size(gray);
            var hint = F("{0}: {1} x {2} pixels. Areas darker than the threshold are traced.", Path.GetFileName(path), w, h);
            if (!await new ImageImportDialog(T("Trace Outlines"), _traceOptions, gray, hint).ShowFor(this)) return;
            var o = _traceOptions;
            var loops = ImageGeometry.TraceOutlines(gray, Math.Max(o.Width, 1e-3), Math.Clamp(o.Threshold, 0, 1), o.Invert, o.Smoothing);
            if (loops.Count == 0)
            {
                Log(T("No outlines found: change the threshold or invert."));
                return;
            }
            var entities = loops.Select(loop => o.AsSplines && loop.Count >= 3
                ? (Entity)SplineEditing.FromAnchors(loop.Vertices.Select(v => v.Point).ToList(), closed: true)
                : new PolylineEntity(loop)).ToList();
            _session.Execute(T("Trace outlines"), () =>
            {
                foreach (var e in entities) _session.Document.AddEntity(e);
            });
            _session.Select(entities);
            Log(F("{0}: {1} outlines traced.", Path.GetFileName(path), entities.Count));
            View.ZoomToFit();
        }
        catch (Exception ex)
        {
            Log(F("Error opening {0}: {1}", path, ex.Message));
        }
    }
}
