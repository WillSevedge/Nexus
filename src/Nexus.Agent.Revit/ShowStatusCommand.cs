using System.Diagnostics;
using Nexus.Contracts;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Nexus.Agent.Revit;

[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowStatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var app = AgentApplication.Instance;
            var status = app?.Server?.Status;
            var dialog = new TaskDialog("Nexus Agent")
            {
                MainInstruction = status is null ? "The Nexus agent is not running." : $"Agent is {status.State}",
                MainContent = status is null
                    ? "See the log for details."
                    : $"Pipe: {status.PipeName}\n" +
                      $"Hub connections: {status.Clients}\n" +
                      $"Requests handled: {status.RequestsHandled}\n" +
                      $"Host: {app?.Host?.DisplayName}\n" +
                      (status.LastError is null ? "" : $"Last error: {status.LastError}\n"),
                FooterText = "Log: " + (app?.Log.FilePath ?? NexusPaths.LogsDir),
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open log folder");
            if (dialog.Show() == TaskDialogResult.CommandLink1)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{NexusPaths.LogsDir}\"") { UseShellExecute = true });
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
