using BamCam.App.Drawing;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    /// <summary>Edit ▸ Polyline ▸ Fillet / Chamfer: corners of the one selected polyline.</summary>
    private void FilletChamfer()
    {
        if (Selected.Count() != 1 || Selected.First() is not PolylineEntity polyline)
        {
            Log(T("Fillet / Chamfer: select one polyline (other shapes: Edit > Convert To > Polyline)."));
            return;
        }
        View.SetTool(new CornerTool(polyline));
    }
}