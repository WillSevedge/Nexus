using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;

namespace MyRevitAddin.UI
{
    public static class DataGridColumnExtensions
    {
        public static readonly DependencyProperty ParameterProperty =
            DependencyProperty.RegisterAttached(
                "Parameter",
                typeof(Parameter),
                typeof(DataGridColumnExtensions),
                new PropertyMetadata(null));

        public static void SetParameter(DataGridColumn column, Parameter value)
        {
            column.SetValue(ParameterProperty, value);
        }

        public static Parameter GetParameter(DataGridColumn column)
        {
            return (Parameter)column.GetValue(ParameterProperty);
        }
    }
}
