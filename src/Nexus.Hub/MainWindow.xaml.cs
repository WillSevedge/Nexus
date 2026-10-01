using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

public partial class MainWindow : Window
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly MainViewModel _vm;
    private DateTime _lastRefresh = DateTime.UtcNow;
    private bool _editingCell;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = _vm;
        _vm.TableReady += OnTableReady;
        _vm.ColumnsChanged += BuildColumns;
        _vm.CommitGridEdits += () => TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _vm.SelectedRowsProvider = SelectedRows;

        // Commit the whole row after each cell edit, so the edit counts as pending straight away.
        TableGrid.CellEditEnding += (_, e) =>
        {
            _editingCell = false;
            if (e.EditAction == DataGridEditAction.Commit)
                Dispatcher.BeginInvoke(() => TableGrid.CommitEdit(DataGridEditingUnit.Row, true));
        };

        // Coming back to the hub: pick up files opened or closed meanwhile.
        Activated += async (_, _) =>
        {
            if (DateTime.UtcNow - _lastRefresh < AutoRefreshInterval) return;
            _lastRefresh = DateTime.UtcNow;
            await _vm.RefreshIfHostsChangedAsync(force: true);
        };

        InputBindings.Add(new KeyBinding(new RelayCommand(() => { SearchBox.Focus(); SearchBox.SelectAll(); return Task.CompletedTask; }), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.RefreshCommand, Key.F5, ModifierKeys.None));

        // The N follows Windows light/dark mode, like the rest of the window.
        UpdateBrandLogo();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateBrandLogo);

    private void UpdateBrandLogo()
    {
        bool dark = false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch { /* default: light */ }
        BrandLogo.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri($"pack://application:,,,/Assets/Nexus-{(dark ? "light" : "dark")}.png"));
    }

    // ------------------------------------------------------------------ columns

    private void OnTableReady(DataTable table, IReadOnlyList<GridColumnSpec> specs)
    {
        TableGrid.ItemsSource = null;
        BuildColumns(specs);
        TableGrid.ItemsSource = table.DefaultView;
    }

    private void BuildColumns(IReadOnlyList<GridColumnSpec> specs)
    {
        TableGrid.Columns.Clear();
        var baseCellStyle = TableGrid.TryFindResource(typeof(DataGridCell)) as Style;
        foreach (var spec in specs)
        {
            var column = new DataGridTextColumn
            {
                Header = Header(spec),
                Binding = new Binding(spec.Column) { Mode = spec.Editable ? BindingMode.TwoWay : BindingMode.OneWay },
                SortMemberPath = spec.Column,
                MaxWidth = 420,
                MinWidth = 40,
                IsReadOnly = !spec.Editable,
            };
            if (spec.Editable) column.CellStyle = CellStyle(spec.Column, baseCellStyle);
            else
            {
                var style = new Style(typeof(DataGridCell), baseCellStyle);
                style.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush")));
                column.CellStyle = style;
            }
            TableGrid.Columns.Add(column);
        }
        TableGrid.FrozenColumnCount = specs.Count(s => s.Frozen);
    }

    private static object Header(GridColumnSpec spec)
    {
        var panel = new StackPanel { ToolTip = spec.Group.Length == 0 ? spec.Name : $"{spec.Group} › {spec.Name}" };
        var group = new TextBlock
        {
            Text = spec.Group.Length == 0 || spec.Group == Nexus.Hub.Core.SheetFieldMap.Group ? " " : spec.Group,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
        };
        group.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        panel.Children.Add(group);
        panel.Children.Add(new TextBlock { Text = spec.Name, FontWeight = FontWeights.SemiBold });
        return panel;
    }

    /// <summary>Edited cells are highlighted; cells that cannot be edited are greyed, with the reason as a tooltip.</summary>
    private Style CellStyle(string column, Style? basedOn)
    {
        var style = new Style(typeof(DataGridCell), basedOn);
        style.Setters.Add(new Setter(BackgroundProperty, StateBinding(column, CellAspect.Background)));
        style.Setters.Add(new Setter(ForegroundProperty, StateBinding(column, CellAspect.Foreground)));
        style.Setters.Add(new Setter(ToolTipProperty, StateBinding(column, CellAspect.ToolTip)));
        return style;
    }

    private MultiBinding StateBinding(string column, CellAspect aspect)
    {
        var binding = new MultiBinding { Converter = new CellStateConverter(_vm, column, aspect) };
        binding.Bindings.Add(new Binding(column)); // re-evaluates when the value changes
        binding.Bindings.Add(new Binding());       // the DataRowView
        return binding;
    }

    private void OnColumns(object sender, RoutedEventArgs e)
    {
        var groups = _vm.BuildColumnGroups();
        if (groups.Count == 0)
        {
            _vm.Status = "Load some data first.";
            return;
        }
        var window = new ColumnsWindow(groups) { Owner = this };
        if (window.ShowDialog() == true) _vm.SetVisibleColumns(window.CheckedColumnIds);
    }

    private void OnDropDown(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    /// <summary>The views list sits inside the sidebar's scroll viewer: pass the mouse wheel on to it.</summary>
    private void OnNestedScroll(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement element) return;
        e.Handled = true;
        var parent = VisualTreeHelper.GetParent(element) as UIElement;
        parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    private void OnMessages(object sender, RoutedEventArgs e) => Dialogs.ShowMessages(this, _vm.IssueLines(), _vm.Log, _vm.EditingReport());

    // ------------------------------------------------------------------ selection

    private void OnSelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        var current = TableGrid.CurrentCell.Item as DataRowView
                      ?? TableGrid.SelectedCells.Select(c => c.Item).OfType<DataRowView>().FirstOrDefault();
        if (current is not null && !ReferenceEquals(current, _vm.SelectedRow)) _vm.SelectedRow = current;
    }

    private IReadOnlyList<DataRowView> SelectedRows() =>
        TableGrid.SelectedCells.Select(c => c.Item).OfType<DataRowView>().Distinct().ToList();

    /// <summary>Selected cells in display order (rows as sorted/filtered, columns as arranged).</summary>
    private List<(DataRowView View, string Column, int RowIndex, int ColumnIndex)> SelectedCellsOrdered()
    {
        var rowIndex = new Dictionary<object, int>();
        var list = new List<(DataRowView, string, int, int)>();
        foreach (var cell in TableGrid.SelectedCells)
        {
            if (cell.Item is not DataRowView view || cell.Column is not DataGridBoundColumn { Binding: Binding b }) continue;
            if (!rowIndex.TryGetValue(view, out int r)) rowIndex[view] = r = TableGrid.Items.IndexOf(view);
            list.Add((view, b.Path.Path, r, cell.Column.DisplayIndex));
        }
        return list.OrderBy(c => c.Item3).ThenBy(c => c.Item4).ToList();
    }

    private List<DataGridBoundColumn> ColumnsInDisplayOrder() =>
        TableGrid.Columns.OfType<DataGridBoundColumn>().OrderBy(c => c.DisplayIndex).ToList();

    // ------------------------------------------------------------------ editing

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not DataRowView view || e.Column is not DataGridBoundColumn { Binding: Binding b })
        {
            e.Cancel = true;
            return;
        }
        var blocker = _vm.EditBlocker(view, b.Path.Path);
        if (blocker is null)
        {
            _editingCell = true;
            return;
        }
        e.Cancel = true;
        _vm.Status = "Cannot edit: " + blocker;
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (_editingCell) return; // the cell's text box handles its own keys
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.V when ctrl: Paste(); e.Handled = true; break;
            case Key.D when ctrl: FillDown(); e.Handled = true; break;
            case Key.H when ctrl: FindReplace(); e.Handled = true; break;
            case Key.Z when ctrl: Revert(); e.Handled = true; break;
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.None: Clear(); e.Handled = true; break;
            case Key.F2 when SelectedCellsOrdered().Count > 1: SetValue(); e.Handled = true; break;
        }
    }

    private void OnPaste(object sender, RoutedEventArgs e) => Paste();
    private void OnFillDown(object sender, RoutedEventArgs e) => FillDown();
    private void OnFindReplace(object sender, RoutedEventArgs e) => FindReplace();
    private void OnSetValue(object sender, RoutedEventArgs e) => SetValue();
    private void OnRevert(object sender, RoutedEventArgs e) => Revert();
    private void OnClear(object sender, RoutedEventArgs e) => Clear();

    /// <summary>
    /// Pastes text copied from Excel (tab/newline separated). One value fills every selected cell;
    /// a block is pasted from the top-left selected cell across and down.
    /// </summary>
    private void Paste()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!Clipboard.ContainsText()) return;
        var lines = Clipboard.GetText().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var block = lines.Select(l => l.Split('\t')).ToList();
        var selected = SelectedCellsOrdered();
        if (selected.Count == 0)
        {
            _vm.Status = "Select the cell to paste into first.";
            return;
        }

        int done = 0;
        var skipped = new List<string>();
        void Set(DataRowView view, string column, string value)
        {
            var reason = _vm.TrySetCell(view, column, value);
            if (reason is null) done++;
            else skipped.Add(reason);
        }

        if (block.Count == 1 && block[0].Length == 1)
        {
            foreach (var c in selected) Set(c.View, c.Column, block[0][0]);
        }
        else
        {
            var columns = ColumnsInDisplayOrder();
            var anchor = selected[0];
            int startColumn = columns.FindIndex(c => ((Binding)c.Binding).Path.Path == anchor.Column);
            for (int i = 0; i < block.Count; i++)
            {
                int r = anchor.RowIndex + i;
                if (r >= TableGrid.Items.Count || TableGrid.Items[r] is not DataRowView view) break;
                for (int j = 0; j < block[i].Length; j++)
                {
                    int c = startColumn + j;
                    if (c >= columns.Count) break;
                    Set(view, ((Binding)columns[c].Binding).Path.Path, block[i][j]);
                }
            }
        }
        Report("Pasted", done, skipped);
    }

    /// <summary>Copies the first selected value in each column down to the other selected cells of that column.</summary>
    private void FillDown()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        int done = 0;
        var skipped = new List<string>();
        foreach (var column in SelectedCellsOrdered().GroupBy(c => c.Column))
        {
            var cells = column.OrderBy(c => c.RowIndex).ToList();
            if (cells.Count < 2) continue;
            string value = cells[0].View[column.Key] as string ?? "";
            foreach (var c in cells.Skip(1))
            {
                var reason = _vm.TrySetCell(c.View, c.Column, value);
                if (reason is null) done++;
                else skipped.Add(reason);
            }
        }
        Report("Filled", done, skipped);
    }

    private void SetValue()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var cells = SelectedCellsOrdered();
        if (cells.Count == 0) return;
        var value = Dialogs.AskValue(this, "Set value", $"New value for the {cells.Count} selected cell(s):",
            cells[0].View[cells[0].Column] as string ?? "");
        if (value is null) return;
        int done = 0;
        var skipped = new List<string>();
        foreach (var c in cells)
        {
            var reason = _vm.TrySetCell(c.View, c.Column, value);
            if (reason is null) done++;
            else skipped.Add(reason);
        }
        Report("Set", done, skipped);
    }

    private void FindReplace()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = SelectedCellsOrdered();
        var request = Dialogs.AskFindReplace(this, selected.Count > 1);
        if (request is null) return;

        IEnumerable<(DataRowView View, string Column)> cells = request.SelectionOnly
            ? selected.Select(c => (c.View, c.Column))
            : TableGrid.Items.OfType<DataRowView>().SelectMany(v => ColumnsInDisplayOrder()
                .Select(col => ((Binding)col.Binding).Path.Path)
                .Where(_vm.IsPropertyColumn)
                .Select(col => (v, col)));

        var comparison = request.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int done = 0;
        var skipped = new List<string>();
        foreach (var (view, column) in cells.ToList())
        {
            string current = view[column] as string ?? "";
            string? next = request.WholeCell
                ? (string.Equals(current.Trim(), request.Find, comparison) ? request.Replace : null)
                : (current.Contains(request.Find, comparison) ? current.Replace(request.Find, request.Replace, comparison) : null);
            if (next is null) continue;
            var reason = _vm.TrySetCell(view, column, next);
            if (reason is null) done++;
            else skipped.Add(reason);
        }
        Report("Replaced", done, skipped);
    }

    private void Clear()
    {
        var cells = SelectedCellsOrdered().Where(c => _vm.IsPropertyColumn(c.Column)).ToList();
        int done = 0;
        var skipped = new List<string>();
        foreach (var c in cells)
        {
            var reason = _vm.TrySetCell(c.View, c.Column, "");
            if (reason is null) done++;
            else skipped.Add(reason);
        }
        Report("Cleared", done, skipped);
    }

    private void Revert() => _vm.Revert(SelectedCellsOrdered().Select(c => (c.View, c.Column)));

    private void Report(string verb, int done, List<string> skipped)
    {
        _vm.Status = skipped.Count == 0
            ? $"{verb} {done} cell(s). Changes are pending until you apply them."
            : $"{verb} {done} cell(s); {skipped.Count} skipped ({skipped.GroupBy(s => s).OrderByDescending(g => g.Count()).First().Key}).";
    }

    private void OnDetailKeyDown(object sender, KeyEventArgs e)
    {
        // Enter in a single-line detail value commits it (Shift+Enter keeps a line break).
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
}

internal enum CellAspect { Background, Foreground, ToolTip }

internal sealed class CellStateConverter : IMultiValueConverter
{
    // Theme brushes (light or dark), with fallbacks if the theme does not define them.
    private static Brush EditedBackground => Theme("SystemFillColorCautionBackgroundBrush", Color.FromRgb(0xFF, 0xF1, 0xB8));
    private static Brush LockedForeground => Theme("TextFillColorTertiaryBrush", Color.FromRgb(0x80, 0x80, 0x80));

    private static Brush Theme(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? Frozen(new SolidColorBrush(fallback));

    private readonly MainViewModel _vm;
    private readonly string _column;
    private readonly CellAspect _aspect;

    public CellStateConverter(MainViewModel vm, string column, CellAspect aspect)
    {
        _vm = vm;
        _column = column;
        _aspect = aspect;
    }

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is not DataRowView view) return DependencyProperty.UnsetValue;
        bool edited = _vm.IsEdited(view, _column, out var original);
        switch (_aspect)
        {
            case CellAspect.Background:
                return edited ? EditedBackground : DependencyProperty.UnsetValue;
            case CellAspect.Foreground:
                return !edited && _vm.EditBlocker(view, _column) is not null ? LockedForeground : DependencyProperty.UnsetValue;
            default:
                if (edited) return $"Edited. Was: {original}";
                var blocker = _vm.EditBlocker(view, _column);
                return blocker is null ? DependencyProperty.UnsetValue : "Not editable: " + blocker;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}
