using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;

namespace Nexus.Hub.ViewModels;

/// <summary>One drawing number in the comparison: the list's values next to the model's.</summary>
public sealed class DeliverableRow : Observable
{
    private bool _take;

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

    /// <summary>Only a drawing on both sides with different values can take the list's values.</summary>
    public bool CanTake => C.Result == DeliverableResult.Differs;

    /// <summary>Take the list's values into the model for this drawing.</summary>
    public bool Take
    {
        get => _take;
        set => Set(ref _take, value && CanTake);
    }
}

/// <summary>
/// The workbook's deliverables list next to what Nexus reads from the models. The workbook is the source of truth:
/// differences are fixed in the model (staged as edits, written with Review &amp; apply), and Nexus only writes the
/// workbook's Model Export table.
/// </summary>
public sealed class DeliverablesViewModel : Observable
{
    public static readonly string[] Filters = { "Differences", "Everything", "Differs", "Not in model", "Not on list", "Match" };

    private readonly MainViewModel _main;
    private string _filter = Filters[0];
    private string _summary = "";
    private string _message = "";
    private bool _busy;
    private int _version;

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
        TakeAllCommand = new RelayCommand(() =>
        {
            bool any = Rows.Any(r => r.CanTake && !r.Take && Shows(r));
            foreach (var r in Rows.Where(r => r.CanTake && Shows(r))) r.Take = any;
            return Task.CompletedTask;
        });
        UpdateModelCommand = new RelayCommand(() => { UpdateModel(); return Task.CompletedTask; }, () => Rows.Any(r => r.Take));
        ExportCommand = new RelayCommand(async () =>
        {
            if (await _main.ExportModelAsync()) Message = _main.Status;
        }, () => !_busy);
    }

    public Window? Window { get; set; }
    public ObservableCollection<DeliverableRow> Rows { get; } = new();
    public ICollectionView View { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ChangeWorkbookCommand { get; }
    public RelayCommand TakeAllCommand { get; }
    public RelayCommand UpdateModelCommand { get; }
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

    /// <summary>Stages the list's values for the ticked drawings as edits in the main table.</summary>
    private void UpdateModel()
    {
        int staged = 0;
        var problems = new List<string>();
        foreach (var r in Rows.Where(r => r.Take).ToList())
        {
            void Put(bool differs, string columnId, string value, string label)
            {
                if (!differs || value.Trim().Length == 0) return; // an empty list cell does not clear the model
                var reason = _main.TrySetValue(r.C.Model!, columnId, value);
                if (reason is null) staged++;
                else problems.Add($"{r.Number} › {label}: {reason}");
            }
            Put(r.TitleDiffers, DeliverablesCompare.TitleId, r.ListTitle, "Sheet Name");
            Put(r.RevDiffers, DeliverablesCompare.RevId, r.ListRev, "Rev");
            Put(r.DateDiffers, DeliverablesCompare.DateId, r.ListDate, "Date");
            Put(r.StatusDiffers, DeliverablesCompare.StatusId, r.ListStatus, "Status");
            r.Take = false;
        }
        Message = (staged > 0
                      ? $"{staged} value(s) from the list are in the main table (yellow). Use Review & apply there to write them to the model. "
                      : "Nothing was changed. ")
                  + (problems.Count > 0 ? $"{problems.Count} could not be changed: " + string.Join("; ", problems.Take(5)) + (problems.Count > 5 ? "…" : "") : "");
        if (problems.Count > 5)
            Dialogs.Message($"{problems.Count} value(s) could not be changed", string.Join(Environment.NewLine, problems.Take(40)), NoticeKind.Info, Window);
    }
}
