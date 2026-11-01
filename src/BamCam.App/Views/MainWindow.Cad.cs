using BamCam.App.Drawing;
using BamCam.Core.Cad;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    /// <summary>
    /// Edit ▸ Polyline ▸ Fillet / Chamfer: corners of the one selected polyline. A rectangle or line is turned into a
    /// polyline first (one undoable step, same id so that machining operations keep referring to it).
    /// </summary>
    private void FilletChamfer()
    {
        var selected = Selected;
        if (selected.Count != 1 || selected[0] is not (PolylineEntity or RectEntity or LineEntity))
        {
            Log(T("Fillet / Chamfer: select one polyline, rectangle or line first."));
            return;
        }
        if (selected[0] is not PolylineEntity polyline)
        {
            var source = selected[0];
            if (CadOps.ToPolylines(source) is not [var converted])
            {
                Log(T("Fillet / Chamfer: select one polyline, rectangle or line first."));
                return;
            }
            var doc = _session.Document;
            _session.Execute(T("Convert to polyline"), () =>
            {
                var layer = source.Layer ?? doc.ActiveLayer;
                doc.RemoveEntity(source);
                converted.Id = source.Id;
                converted.Tag = source.Tag;
                doc.AddEntity(converted, layer);
            });
            _session.Select([converted]);
            polyline = converted;
        }
        View.SetTool(new CornerTool(polyline));
    }

    /// <summary>Reference of the align commands (toolbar list), kept in the settings.</summary>
    private AlignReference AlignTo
    {
        get => _settings.AlignReference;
        set
        {
            _settings.AlignReference = value;
            SaveSettings();
        }
    }

    /// <summary>Aligns the selected objects (as CamBam's Align Toolbar plugin).</summary>
    private void AlignSelection(AlignEdge edge)
    {
        var selected = Selected;
        if (selected.Count == 0)
        {
            Log(T("Align: select the objects first (in order: the last or first selected one can be the reference)."));
            return;
        }
        var ok = false;
        _session.Execute(T("Align"), () => ok = Align.Apply(_session.Document, selected, edge, AlignTo));
        if (!ok)
            Log(AlignTo == AlignReference.Stock ? T("Align: there is no stock.")
                : T("Align: select at least two objects to align them to the first or last one."));
    }

    private static readonly (AlignReference Value, string Text)[] AlignReferences =
    [
        (AlignReference.LastSelected, "To last selected"),
        (AlignReference.FirstSelected, "To first selected"),
        (AlignReference.Selection, "To selection"),
        (AlignReference.Drawing, "To drawing"),
        (AlignReference.Origin, "To origin"),
        (AlignReference.Stock, "To stock"),
    ];
}
