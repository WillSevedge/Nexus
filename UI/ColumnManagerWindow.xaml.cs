using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MyRevitAddin.UI
{
    public partial class ColumnManagerWindow : Window
    {
        public readonly Dictionary<DataGridColumn, bool> ColumnVisibility;

        public ColumnManagerWindow(IEnumerable<DataGridColumn> columns)
        {
            InitializeComponent();
            ColumnVisibility = columns.ToDictionary(col => col, col => col.Visibility == Visibility.Visible);

            foreach (var col in columns)
            {
                var cb = new CheckBox
                {
                    Content = col.Header.ToString(),
                    IsChecked = ColumnVisibility[col],
                    Tag = col
                };
                cb.Checked += Checkbox_Checked;
                cb.Unchecked += Checkbox_Unchecked;
                columnListPanel.Children.Add(cb);
            }
        }

        private void Checkbox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.Tag is DataGridColumn col)
            {
                ColumnVisibility[col] = true;
            }
        }

        private void Checkbox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.Tag is DataGridColumn col)
            {
                ColumnVisibility[col] = false;
            }
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox cb in columnListPanel.Children.OfType<CheckBox>())
            {
                cb.IsChecked = true;
                if (cb.Tag is DataGridColumn col) ColumnVisibility[col] = true;
            }
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox cb in columnListPanel.Children.OfType<CheckBox>())
            {
                cb.IsChecked = false;
                if (cb.Tag is DataGridColumn col) ColumnVisibility[col] = false;
            }
        }

        private void BtnInverse_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox cb in columnListPanel.Children.OfType<CheckBox>())
            {
                cb.IsChecked = !cb.IsChecked;
                if (cb.Tag is DataGridColumn col) ColumnVisibility[col] = cb.IsChecked == true;
            }
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
        private void OK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
