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
    }

    private void BuildColumns()
    {
        var take = new DataGridCheckBoxColumn
        {
            Header = "Take",
            Binding = new Binding(nameof(DeliverableRow.Take)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 50,
        };
        var enabled = new Style(typeof(CheckBox));
        enabled.Setters.Add(new Setter(IsEnabledProperty, new Binding(nameof(DeliverableRow.CanTake))));
        enabled.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center));
        take.ElementStyle = enabled;
        take.EditingElementStyle = enabled;
        Grid.Columns.Add(take);

        Text("Dwg/Doc No.", nameof(DeliverableRow.Number), null, 110, bold: true);
        Text("Discipline", nameof(DeliverableRow.Discipline), null, 90);
        // The list…
        Text("List · Title", nameof(DeliverableRow.ListTitle), nameof(DeliverableRow.TitleDiffers), 220, list: true);
        Text("List · Rev", nameof(DeliverableRow.ListRev), nameof(DeliverableRow.RevDiffers), 60, list: true);
        Text("List · Date", nameof(DeliverableRow.ListDate), nameof(DeliverableRow.DateDiffers), 90, list: true);
        Text("List · Status", nameof(DeliverableRow.ListStatus), nameof(DeliverableRow.StatusDiffers), 90, list: true);
        // …next to the model.
        Text("Model · Sheet Name", nameof(DeliverableRow.ModelTitle), nameof(DeliverableRow.TitleDiffers), 220);
        Text("Model · Rev", nameof(DeliverableRow.ModelRev), nameof(DeliverableRow.RevDiffers), 60);
        Text("Model · Rev Date", nameof(DeliverableRow.ModelDate), nameof(DeliverableRow.DateDiffers), 90);
        Text("Model · Status", nameof(DeliverableRow.ModelStatus), nameof(DeliverableRow.StatusDiffers), 90);
        Text("Model · Source", nameof(DeliverableRow.Source), null, 180);
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
