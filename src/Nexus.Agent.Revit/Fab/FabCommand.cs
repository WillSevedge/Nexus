using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Nexus.Agent;

namespace Nexus.Agent.Revit.Fab;

/// <summary>Nexus tab › Fabrication › Spools &amp; Packages: the fabrication window.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class FabCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var log = AgentApplication.Instance?.Log ?? new AgentLog("revit");
        try
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc is null)
            {
                TaskDialog.Show("Nexus Fabrication", "Open a model first.");
                return Result.Cancelled;
            }
            if (uidoc.Document.IsFamilyDocument)
            {
                TaskDialog.Show("Nexus Fabrication", "This works in project models, not in the family editor.");
                return Result.Cancelled;
            }
            new FabWindow(uidoc, log).ShowDialog();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            log.Error("The Fabrication window could not open.", ex);
            TaskDialog.Show("Nexus Fabrication", "The Fabrication window could not open: " + ex.Message);
            return Result.Failed;
        }
    }
}
