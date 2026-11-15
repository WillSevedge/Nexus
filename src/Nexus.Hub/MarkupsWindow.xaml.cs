using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>PDFs and their markups: markups by sheet, pages, document details, and every value of the selected one.</summary>
public partial class MarkupsWindow : Window
{
    private readonly MarkupsViewModel _vm;

    public MarkupsWindow(MarkupsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.SelectedRows = () => Grid.SelectedItems.OfType<MarkupRow>().ToList();
        Closed += (_, _) => vm.Dispose();
        Loaded += (_, _) => vm.Start();
    }

    /// <summary>Double-click: open the markup's sheet in Revit or AutoCAD.</summary>
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.ShowInModelCommand.CanExecute(null)) _vm.ShowInModelCommand.Execute(null);
    }

    /// <summary>The markup selected last: everything stored on it, in the pane on the right.</summary>
    private void OnMarkupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is MarkupRow row) _vm.ShowDetails(row.Title, row.Properties);
    }

    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PagesGrid.SelectedItem is PageRow row) _vm.ShowDetails(row.Title, row.Properties);
    }

    /// <summary>Files and folders dropped on the window are added like Add files and Watch folder.</summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) await _vm.AddPathsAsync(paths);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
