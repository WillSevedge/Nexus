using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;

namespace MyRevitAddin
{
    [Transaction(TransactionMode.Manual)]
    public class ExecutableCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            TaskDialog.Show("ExecutableCommand", "ExecutableCommand executed successfully!");
            return Result.Succeeded;
        }
    }
}
