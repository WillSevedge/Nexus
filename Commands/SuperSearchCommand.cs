using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MyRevitAddin.UI;

namespace MyRevitAddin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SuperSearchCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Get the current document
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // Retrieve all families from the document
                IEnumerable<Family> families = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .ToList();

                // If no families are found, show a message and exit
                if (!families.Any())
                {
                    TaskDialog.Show("SuperSearch", "No families found in the document.");
                    return Result.Failed;
                }

                // Open the selection window with available families
                SuperSearchSelectionWindow selectionWindow = new SuperSearchSelectionWindow(families);
                if (selectionWindow.ShowDialog() == true)
                {
                    // If families are selected, open the results window
                    if (selectionWindow.SelectedFamilies.Any())
                    {
                        SuperSearchResultsWindow resultsWindow = new SuperSearchResultsWindow(selectionWindow.SelectedFamilies);
                        resultsWindow.ShowDialog();
                    }
                    else
                    {
                        TaskDialog.Show("SuperSearch", "No families were selected.");
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
