using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>The deliverables list next to the model; differing values are highlighted on both sides.</summary>
public partial class DeliverablesWindow : Window
{
    public DeliverablesWindow(DeliverablesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        BuildColumns();
        vm.SelectedRows = () => Grid.SelectedItems.OfType<DeliverableRow>().ToList();
        vm.SelectRows = rows =>
        {
            Grid.SelectedItems.Clear();
            foreach (var r in rows) Grid.SelectedItems.Add(r);
            if (rows.Count > 0) Grid.ScrollIntoView(rows[0]);
            Grid.Focus();
        };
    }

    private void BuildColumns()
    {
        Text("Dwg/Doc No.", nameof(DeliverableRow.Number), null, 110, bold: true);
        Text("Discipline", nameof(DeliverableRow.Discipline), null, 90);
        // The list…
        Text("List · Title", nameof(DeliverableRow.ListTitle), nameof(DeliverableRow.TitleDiffers), 220, list: true);
        Text("List · Rev", nameof(DeliverableRow.ListRev), nameof(DeliverableRow.RevDiffers), 80, list: true);
        Text("List · Date", nameof(DeliverableRow.ListDate), nameof(DeliverableRow.DateDiffers), 95, list: true);
        Text("List · Status", nameof(DeliverableRow.ListStatus), nameof(DeliverableRow.StatusDiffers), 100, list: true);
        // …next to the model.
        Text("Model · Sheet Name", nameof(DeliverableRow.ModelTitle), nameof(DeliverableRow.TitleDiffers), 220);
        Text("Model · Rev", nameof(DeliverableRow.ModelRev), nameof(DeliverableRow.RevDiffers), 90);
        Text("Model · Rev Date", nameof(DeliverableRow.ModelDate), nameof(DeliverableRow.DateDiffers), 120);
        Text("Model · Status", nameof(DeliverableRow.ModelStatus), nameof(DeliverableRow.StatusDiffers), 115);
        Text("Model · Source", nameof(DeliverableRow.Source), null, 200);
        Text("Result", nameof(DeliverableRow.ResultText), null, 110, result: true);
    }

    /// <summary>A read-only text column; the cell is highlighted when <paramref name="differs"/> is true.</summary>
    private void Text(string header, string path, string? differs, double width, bool bold = false, bool list = false, bool result = false)
    {
        var column = new DataGridTextColumn { Header = header, Binding = new Binding(path), IsReadOnly = true, Width = new DataGridLength(width) };
        var element = new Style(typeof(TextBlock));
        element.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        element.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 2, 4, 2)));
        element.Setters.Add(new Setter(ToolTipProperty, new Binding(path)));
        if (bold) element.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        if (list) element.Setters.Add(new Setter(TextBlock.BackgroundProperty, new DynamicResourceExtension("SubtleFillColorSecondaryBrush")));
        if (differs is not null)
        {
            var trigger = new DataTrigger { Binding = new Binding(differs), Value = true };
            trigger.Setters.Add(new Setter(TextBlock.BackgroundProperty, new DynamicResourceExtension("SystemFillColorCautionBackgroundBrush")));
            trigger.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            element.Triggers.Add(trigger);
        }
        if (result)
        {
            foreach (var (value, brush) in new[]
                     {
                         ("Differs", "SystemFillColorCautionBackgroundBrush"),
                         ("Not in model", "SystemFillColorCriticalBackgroundBrush"),
                         ("Not on list", "SystemFillColorAttentionBackgroundBrush"),
                         ("Match", "SystemFillColorSuccessBackgroundBrush"),
                     })
            {
                var t = new DataTrigger { Binding = new Binding(path), Value = value };
                t.Setters.Add(new Setter(TextBlock.BackgroundProperty, new DynamicResourceExtension(brush)));
                element.Triggers.Add(t);
            }
        }
        column.ElementStyle = element;
        Grid.Columns.Add(column);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ((DeliverablesViewModel)DataContext).SelectionChanged(Grid.SelectedItems.Count);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
