using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace Nexus.Common;

/// <summary>
/// Auto-fit for every table: each column is as wide as its header and its widest cell (up to a limit, past which
/// the text wraps), and each row is as tall as its text. Applied to every table in the hub and in the add-ins'
/// windows, so no table opens with squeezed or cut-off columns. Columns can still be dragged wider or narrower.
/// </summary>
internal static class GridAutoFit
{
    /// <summary>Widest a column gets by itself; longer text wraps onto more lines.</summary>
    private const double MaxColumnWidth = 520;
    private const double MinColumnWidth = 44;
    /// <summary>A column that is typed into is never narrower than this, also while it is still empty.</summary>
    private const double MinEditableWidth = 120;
    /// <summary>Narrow columns that hold a tick box or an icon keep their width.</summary>
    private const double KeepBelow = 64;

    private static readonly ConditionalWeakTable<DataGrid, object> Done = new();

    /// <summary>Set by the program to give a table its look before it is fitted (the hub gives every table its own table style).</summary>
    public static Action<DataGrid>? Prepare { get; set; }

    /// <summary>Every table in the window (also those on tabs not opened yet).</summary>
    public static void ApplyAll(DependencyObject root)
    {
        if (root is DataGrid grid) Apply(grid);
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d) ApplyAll(d);
    }

    public static void Apply(DataGrid grid)
    {
        if (Done.TryGetValue(grid, out _)) return;
        Done.Add(grid, new object());
        try { Prepare?.Invoke(grid); } catch { /* the table keeps the look it has */ }

        // Rows: as tall as their content, never shorter than the height the table was designed with.
        if (!double.IsNaN(grid.RowHeight))
        {
            grid.MinRowHeight = Math.Max(grid.MinRowHeight, grid.RowHeight);
            grid.RowHeight = double.NaN;
        }
        if (grid.MinRowHeight < 24) grid.MinRowHeight = 24;
        grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

        foreach (var column in grid.Columns) Fit(grid, column);
        // Columns added later (tables whose columns come from the data).
        ((INotifyCollectionChanged)grid.Columns).CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (var item in e.NewItems)
                if (item is DataGridColumn column) Fit(grid, column);
        };
    }

    private static void Fit(DataGrid grid, DataGridColumn column)
    {
        bool narrowFixed = column.Width.IsAbsolute && column.Width.Value <= KeepBelow
                           && (column is DataGridTemplateColumn or DataGridCheckBoxColumn || IsBlank(column.Header));
        if (narrowFixed) return;

        // A star column takes "what is left", and a table with one squeezes its other columns when it is opened
        // narrow (and never widens them again). Sized to content, nothing is squeezed.
        column.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
        double min = !grid.IsReadOnly && !column.IsReadOnly && column is DataGridTextColumn ? MinEditableWidth : MinColumnWidth;
        if (column.MinWidth < min) column.MinWidth = min;
        if (double.IsInfinity(column.MaxWidth) || double.IsNaN(column.MaxWidth)) column.MaxWidth = MaxColumnWidth;

        if (column is DataGridTextColumn text)
        {
            // Text longer than the widest column wraps, and the row grows to show it.
            var style = new Style(typeof(TextBlock), text.ElementStyle);
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.None));
            text.ElementStyle = style;
        }
    }

    private static bool IsBlank(object? header) => header is null || (header is string s && s.Trim().Length == 0);
}
