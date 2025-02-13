using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;

namespace MyRevitAddin.UI
{
    public partial class SuperSearchSelectionWindow : Window
    {
        public List<Family> SelectedFamilies { get; private set; }

        public SuperSearchSelectionWindow(IEnumerable<Family> families)
        {
            InitializeComponent();
            familyListBox.ItemsSource = families.Select(f => f.Name).ToList();
            SelectedFamilies = new List<Family>();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            familyListBox.SelectAll();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            familyListBox.SelectedItems.Clear();
        }

        private void BtnInverse_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = familyListBox.SelectedItems.Cast<string>().ToList();
            familyListBox.SelectedItems.Clear();
            foreach (var item in familyListBox.Items.Cast<string>())
            {
                if (!selectedItems.Contains(item))
                    familyListBox.SelectedItems.Add(item);
            }
        }

        private void BtnQuery_Click(object sender, RoutedEventArgs e)
        {
            SelectedFamilies = familyListBox.SelectedItems.Cast<string>()
                .Select(name => familyListBox.Items.Cast<Family>().FirstOrDefault(f => f.Name == name))
                .Where(f => f != null)
                .ToList();

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
