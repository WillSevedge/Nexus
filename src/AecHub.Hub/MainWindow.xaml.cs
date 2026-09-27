using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AecHub.Hub.Core;
using AecHub.Hub.ViewModels;

namespace AecHub.Hub;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.TableReady += OnTableReady;
        Loaded += async (_, _) =>
        {
            HubLog.Info("Hub started. Log: " + HubLog.FilePath);
            await _vm.RefreshAsync();
        };
    }

    private void OnTableReady(DataTable table, IReadOnlyList<(string Column, string Header)> headers)
    {
        TableGrid.ItemsSource = null;
        TableGrid.Columns.Clear();
        foreach (var (column, header) in headers)
        {
            TableGrid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(column),
                MaxWidth = 400,
            });
        }
        TableGrid.ItemsSource = table.DefaultView;
    }

    private void OnItemSelected(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedItem = e.NewValue as ItemNode;
}
