using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MyRevitAddin.UI
{
    public partial class SuperSearchResultsWindow : Window
    {
        public List<DataGridColumn> Columns { get; private set; }

        private readonly List<string> _categoryOrder = new List<string>
        {
            "Analysis Results", "Analytical Model", "Constraints", "Construction", "Data",
            "Dimensions", "Electrical", "Energy Analysis", "Family Editor", "Fire Protection",
            "General", "Graphics", "Identity Data", "Mechanical", "Phasing", "Plumbing",
            "Structural", "Structural Analysis", "Text", "Title Text", "Visibility"
        };

        public SuperSearchResultsWindow(IEnumerable<Family> families)
        {
            InitializeComponent();
            var parameters = new Dictionary<string, List<Parameter>>();

            foreach (var family in families)
            {
                foreach (ElementId typeId in family.GetFamilySymbolIds())
                {
                    FamilySymbol symbol = family.Document.GetElement(typeId) as FamilySymbol;
                    if (symbol != null)
                    {
                        foreach (Parameter param in symbol.Parameters)
                        {
                            string category = GetParameterCategory(param);
                            if (!parameters.ContainsKey(category))
                            {
                                parameters[category] = new List<Parameter>();
                            }
                            parameters[category].Add(param);
                        }
                    }
                }
            }

            PopulateTreeView(parameters);
        }

        private void PopulateTreeView(Dictionary<string, List<Parameter>> parameters)
        {
            foreach (var category in _categoryOrder)
            {
                if (parameters.ContainsKey(category))
                {
                    var categoryItem = new TreeViewItem { Header = category, IsExpanded = true };
                    foreach (var param in parameters[category])
                    {
                        var paramItem = new TreeViewItem { Header = param.Definition.Name };
                        categoryItem.Items.Add(paramItem);
                    }
                    treeViewParameters.Items.Add(categoryItem);
                }
            }
        }

        private string GetParameterCategory(Parameter param)
        {
            if (param == null) return "Other";
            switch (param.Definition.ParameterGroup)
            {
                case BuiltInParameterGroup.PG_ANALYSIS_RESULTS: return "Analysis Results";
                case BuiltInParameterGroup.PG_ANALYTICAL_MODEL: return "Analytical Model";
                case BuiltInParameterGroup.PG_CONSTRAINTS: return "Constraints";
                case BuiltInParameterGroup.PG_CONSTRUCTION: return "Construction";
                case BuiltInParameterGroup.PG_DATA: return "Data";
                default: return "Other";
            }
        }

        private void BtnManageColumns_Click(object sender, RoutedEventArgs e)
        {
            if (dataGridResults != null && dataGridResults.Columns.Count > 0)
            {
                var cmw = new ColumnManagerWindow(dataGridResults.Columns);
                if (cmw.ShowDialog() == true)
                {
                    foreach (var col in dataGridResults.Columns)
                    {
                        col.Visibility = cmw.ColumnVisibility[col] ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    }
                }
            }
            else
            {
                TaskDialog.Show("Column Manager", "No columns available to manage.");
            }
        }
    }
}
