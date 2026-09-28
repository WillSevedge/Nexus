using Nexus.Agent;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Nexus.Agent.Revit;

/// <summary>Ribbon button: start the hub, or bring it to the front.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class OpenHubCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var log = AgentApplication.Instance?.Log ?? new AgentLog("revit");
        string? error = HubLauncher.Open(log);
        if (error is not null)
            TaskDialog.Show("Nexus", error);
        return Result.Succeeded;
    }
}

/// <summary>Keeps ribbon buttons enabled with no document open (e.g. on the Home screen).</summary>
public sealed class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}
