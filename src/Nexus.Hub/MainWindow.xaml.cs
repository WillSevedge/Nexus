using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

public partial class MainWindow : Window
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly MainViewModel _vm = new();
    private DateTime _lastRefresh = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.TableReady += OnTableReady;
        _vm.CommitGridEdits += () => TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        // Commit the whole row after each cell edit, so the edit counts as pending straight away.
        TableGrid.CellEditEnding += (_, e) =>
        {
            if (e.EditAction == DataGridEditAction.Commit)
                Dispatcher.BeginInvoke(() => TableGrid.CommitEdit(DataGridEditingUnit.Row, true));
        };
        Loaded += async (_, _) =>
        {
            HubLog.Info("Hub started. Log: " + HubLog.FilePath);
            RecordLocation();
            _lastRefresh = DateTime.UtcNow;
            await _vm.RefreshAsync();
        };
        // Coming back to the hub (e.g. from Revit's Open Hub button): pick up newly opened hosts and documents.
        Activated += async (_, _) =>
        {
            if (DateTime.UtcNow - _lastRefresh < AutoRefreshInterval || !_vm.RefreshCommand.CanExecute(null)) return;
            _lastRefresh = DateTime.UtcNow;
            await _vm.RefreshAsync();
        };
    }

    /// <summary>Lets the host add-ins' "Open Hub" button find this executable.</summary>
    private static void RecordLocation()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            Directory.CreateDirectory(NexusPaths.Root);
            File.WriteAllText(NexusPaths.HubLocationFile, exe);
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not record the hub location.", ex);
        }
    }

    private void OnTableReady(DataTable table, IReadOnlyList<(string Column, string Header)> headers)
    {
        TableGrid.ItemsSource = null;
        TableGrid.Columns.Clear();
        var baseCellStyle = TableGrid.TryFindResource(typeof(DataGridCell)) as Style;
        foreach (var (column, header) in headers)
        {
            bool editable = _vm.IsPropertyColumn(column);
            var gridColumn = new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(column) { Mode = editable ? BindingMode.TwoWay : BindingMode.OneWay },
                MaxWidth = 400,
                IsReadOnly = !editable,
            };
            if (editable) gridColumn.CellStyle = CellStyle(column, baseCellStyle);
            TableGrid.Columns.Add(gridColumn);
        }
        TableGrid.ItemsSource = table.DefaultView;
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

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not DataRowView view || e.Column is not DataGridBoundColumn { Binding: Binding b })
        {
            e.Cancel = true;
            return;
        }
        var blocker = _vm.EditBlocker(view, b.Path.Path);
        if (blocker is null) return;
        e.Cancel = true;
        _vm.Status = "Cannot edit: " + blocker;
    }

    private void OnItemSelected(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedItem = e.NewValue as ItemNode;
}

internal enum CellAspect { Background, Foreground, ToolTip }

internal sealed class CellStateConverter : IMultiValueConverter
{
    private static readonly Brush EditedBackground = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xB8)));
    private static readonly Brush LockedForeground = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));

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
