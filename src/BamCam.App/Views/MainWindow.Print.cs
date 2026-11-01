using Avalonia.Platform.Storage;
using BamCam.App.Services;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    private readonly PrintOptions _printOptions = new();

    /// <summary>
    /// File ▸ Print (PDF opened in the PDF viewer, which prints) or File ▸ Export ▸ PDF (saved where the user chooses).
    /// </summary>
    private async void PrintPdf(bool export)
    {
        if (!_session.Document.AllEntities().Any())
        {
            Log(T("The drawing is empty."));
            return;
        }
        if (!await new ObjectDialog(export ? T("Export PDF") : T("Print"), _printOptions,
                T("The drawing is written as PDF true to scale; printing happens in your PDF viewer (use \"actual size\" there for 1:1).")).ShowFor(this)) return;
        string path;
        if (export)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Export PDF"),
                SuggestedFileName = _session.Document.Name + ".pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
            });
            if (file?.TryGetLocalPath() is not { } chosen) return;
            path = chosen;
        }
        else
        {
            var dir = Path.Combine(Path.GetTempPath(), "BamCam");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, string.Concat((_session.Document.Name is { Length: > 0 } n ? n : "BamCam").Split(Path.GetInvalidFileNameChars())) + ".pdf");
        }
        try
        {
            var pages = PdfPrint.Write(_session.Document, _session.Toolpaths, _printOptions, path);
            Log(F("PDF written: {0} ({1} pages)", path, pages));
            if (!export) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log(F("PDF could not be written: {0}", ex.Message));
        }
    }
}
