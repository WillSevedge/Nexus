using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;
using Nexus.Hub.Excel;

namespace Nexus.Hub.ViewModels;

/// <summary>One drawing number in the comparison: the list's values next to the model's.</summary>
public sealed class DeliverableRow
{
    public DeliverableRow(DeliverableComparison c) => C = c;

    public DeliverableComparison C { get; }
    public string Number => C.Number;
    public string Discipline => C.List?.Discipline ?? "";
    public string ListTitle => C.List?.Title ?? "";
    public string ListRev => C.List?.Rev ?? "";
    public string ListDate => C.List?.Date ?? "";
    public string ListStatus => C.List?.Status ?? "";
    public string ModelTitle => C.ModelTitle;
    public string ModelRev => C.ModelRev;
    public string ModelDate => C.ModelDate;
    public string ModelStatus => C.ModelStatus;
    public string Source => C.Model is null ? "" : $"{C.Program} · {C.File}";
    public bool TitleDiffers => C.TitleDiffers;
    public bool RevDiffers => C.RevDiffers;
    public bool DateDiffers => C.DateDiffers;
    public bool StatusDiffers => C.StatusDiffers;
    public DeliverableResult Result => C.Result;

    public string ResultText => C.Result switch
    {
        DeliverableResult.Match => "Match",
        DeliverableResult.Differs => "Differs",
        DeliverableResult.NotInModel => "Not in model",
        _ => "Not on list",
    };
}

/// <summary>
/// The workbook's deliverables list next to what Nexus reads from the models. The workbook is the source of truth:
/// selected drawings are matched in the model (new sheets for drawings the model lacks, the list's values for those
/// that differ), and Nexus only writes the workbook's Model Export table.
/// </summary>
public sealed class DeliverablesViewModel : Observable
{
    public static readonly string[] Filters = { "Differences", "Everything", "Differs", "Not in model", "Not on list", "Match" };

    private readonly MainViewModel _main;
    private DeliverablesWorkbook? _workbook;
    private string _filter = Filters[0];
    private string _summary = "";
    private string _message = "";
    private bool _busy;
    private int _version;
    private int _selectedCount;
    private List<DocumentNode> _targets = new();
    private DocumentNode? _target;

    public DeliverablesViewModel(MainViewModel main)
    {
        _main = main;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is DeliverableRow r && Shows(r);
        RefreshCommand = new RelayCommand(RefreshAsync, () => !_busy);
        ChangeWorkbookCommand = new RelayCommand(async () =>
        {
            if (_main.ChooseWorkbook()) await RefreshAsync();
        }, () => !_busy);
        ExcelSelectionCommand = new RelayCommand(UseExcelSelectionAsync, () => !_busy && Rows.Count > 0);
        MatchCommand = new RelayCommand(MatchAsync, () => !_busy && _selectedCount > 0);
        ExportCommand = new RelayCommand(async () =>
        {
            if (await _main.ExportModelAsync()) Message = _main.Status;
        }, () => !_busy);
    }

    public Window? Window { get; set; }
    /// <summary>The window supplies the selected rows, and selects rows (from Excel's selection).</summary>
    public Func<IReadOnlyList<DeliverableRow>>? SelectedRows { get; set; }
    public Action<IReadOnlyList<DeliverableRow>>? SelectRows { get; set; }

    public ObservableCollection<DeliverableRow> Rows { get; } = new();
    public ICollectionView View { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ChangeWorkbookCommand { get; }
    public RelayCommand ExcelSelectionCommand { get; }
    public RelayCommand MatchCommand { get; }
    public RelayCommand ExportCommand { get; }

    public string Title => "Deliverables vs model — " + _main.ExcelLinkTitle;
    public string WorkbookPath => _main.WorkbookPath ?? "";

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) View.Refresh();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    /// <summary>Open Revit models new sheets can go in.</summary>
    public List<DocumentNode> Targets
    {
        get => _targets;
        private set
        {
            Set(ref _targets, value);
            Raise(nameof(HasTargets));
        }
    }

    public bool HasTargets => _targets.Count > 0;

    public DocumentNode? Target
    {
        get => _target;
        set => Set(ref _target, value);
    }

    public string MatchText => _selectedCount == 0 ? "Match model to list" : $"Match model to list ({_selectedCount})";

    /// <summary>The window reports the selection count (for the button text).</summary>
    public void SelectionChanged(int count)
    {
        _selectedCount = count;
        Raise(nameof(MatchText));
    }

    private bool Shows(DeliverableRow r) => _filter switch
    {
        "Everything" => true,
        "Differs" => r.Result == DeliverableResult.Differs,
        "Not in model" => r.Result == DeliverableResult.NotInModel,
        "Not on list" => r.Result == DeliverableResult.NotOnList,
        "Match" => r.Result == DeliverableResult.Match,
        _ => r.Result != DeliverableResult.Match,
    };

    /// <summary>Reads the workbook (as last saved) and compares it with the loaded sheets.</summary>
    public async Task RefreshAsync()
    {
        Raise(nameof(Title));
        Raise(nameof(WorkbookPath));
        Targets = _main.SheetTargets();
        if (_target is null || !_targets.Contains(_target))
            Target = _targets.FirstOrDefault(d => d.IsChecked) ?? _targets.FirstOrDefault();
        if (_main.WorkbookPath is not { } path) return;
        var table = _main.Table;
        if (table is null || !_main.IsSheetsView)
        {
            Message = "Show Sheets in the main window (and tick the models) to compare them with the deliverables list.";
            return;
        }
        int version = ++_version;
        IsBusy = true;
        Message = $"Reading {Path.GetFileName(path)}…";
        try
        {
            var (info, comparison) = await Task.Run(() =>
            {
                var wb = Deliverables.Read(path);
                return (wb, DeliverablesCompare.Compare(wb.Items, table));
            });
            if (version != _version) return;
            _workbook = info;
            Rows.Clear();
            foreach (var c in comparison
                         .OrderBy(c => c.Result == DeliverableResult.NotOnList)
                         .ThenBy(c => c.Number, NaturalComparer.Instance))
                Rows.Add(new DeliverableRow(c));

            int Count(DeliverableResult r) => comparison.Count(c => c.Result == r);
            Summary = $"{info.Items.Count} on the list · {Count(DeliverableResult.Match)} match · {Count(DeliverableResult.Differs)} differ · " +
                      $"{Count(DeliverableResult.NotInModel)} not in the model · {Count(DeliverableResult.NotOnList)} model sheet(s) not on the list";
            var notes = new List<string>(info.Warnings);
            if (info.ListSheet.Length > 0) notes.Insert(0, $"List read from '{info.ListSheet}' as last saved in Excel ({DateTime.Now:t}).");
            if (info.Unnumbered > 0) notes.Add($"{info.Unnumbered} item(s) on the list have no drawing number yet and are not compared.");
            Message = string.Join(" ", notes);
        }
        catch (Exception ex)
        {
            if (version == _version) Message = "Could not read the workbook: " + ex.Message;
        }
        finally
        {
            if (version == _version) IsBusy = false;
        }
    }

    /// <summary>Selects the drawings that are selected in Excel (rows of the list, or cells holding drawing numbers).</summary>
    private async Task UseExcelSelectionAsync()
    {
        if (_main.WorkbookPath is not { } path) return;
        string listSheet = _workbook?.ListSheet ?? "";
        ExcelLive.ExcelSelection? selection;
        try
        {
            selection = await ExcelLive.RunSta(() =>
            {
                object? wb = ExcelLive.FindOpenWorkbook(path);
                if (wb is null) return null;
                try { return ExcelLive.Selection(wb); }
                finally { Marshal.ReleaseComObject(wb); }
            });
        }
        catch (Exception ex)
        {
            Message = "Could not read the selection in Excel: " + ex.Message;
            return;
        }
        if (selection is null)
        {
            Message = $"Open {Path.GetFileName(path)} in Excel and select the drawings there (rows of '{listSheet}', or cells with drawing numbers), then try again.";
            return;
        }

        var picked = Rows.Where(r =>
                (r.C.List is { } item && string.Equals(selection.Worksheet, listSheet, StringComparison.OrdinalIgnoreCase) && selection.Rows.Contains(item.Row))
                || selection.Texts.Any(t => string.Equals(ExcelCompare.NormalizeKey(t), ExcelCompare.NormalizeKey(r.Number), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (picked.Count == 0)
        {
            Message = $"Nothing selected in Excel matches a drawing here. Select rows on '{listSheet}' (or cells with drawing numbers), then try again.";
            return;
        }
        if (picked.Any(r => !Shows(r))) Filter = "Everything";
        SelectRows?.Invoke(picked);
        Message = $"{picked.Count} drawing(s) selected from Excel. Match model to list makes the model match them.";
    }

    /// <summary>Makes the model match the list for the selected drawings.</summary>
    private async Task MatchAsync()
    {
        var selected = SelectedRows?.Invoke() ?? Array.Empty<DeliverableRow>();
        var items = selected.Where(r => r.C.List is not null && r.Result is DeliverableResult.Differs or DeliverableResult.NotInModel)
            .Select(r => r.C.List!).ToList();
        int notOnList = selected.Count(r => r.Result == DeliverableResult.NotOnList);
        if (items.Count == 0)
        {
            Message = selected.Count == 0
                ? "Select the drawings to match first (click, Ctrl+click, Shift+click, Ctrl+A), or pick them in Excel and use Excel selection."
                : notOnList > 0
                    ? "These sheets are not on the deliverables list. The list is the source of truth: add them to it in Excel, or leave them."
                    : "The selected drawings already match.";
            return;
        }

        IsBusy = true;
        try
        {
            string result = await _main.MatchToListAsync(items, Target);
            if (notOnList > 0) result += $" {notOnList} selected sheet(s) not on the list were left alone.";
            await RefreshAsync();
            Message = result;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
