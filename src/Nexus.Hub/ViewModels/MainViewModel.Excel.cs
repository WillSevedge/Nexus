using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;
using Nexus.Hub.Excel;

namespace Nexus.Hub.ViewModels;

/// <summary>
/// The linked workbook (e.g. the Budget Tool) is the source of truth: Nexus compares its deliverables list with the
/// model (differences are fixed in the model), and the only thing it writes is the Model Export table.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _excelGate = new(1, 1);
    private FileSystemWatcher? _excelWatcher;
    private CancellationTokenSource? _excelSaveDelay;
    /// <summary>Saves Nexus makes itself are not reported as changes (UTC ticks).</summary>
    private long _ignoreExcelSavesUntil;
    private string _excelState = "";
    private DeliverablesViewModel? _deliverables;

    public string? WorkbookPath => _settings.ExcelWorkbook is { Length: > 0 } p ? p : null;
    public bool HasExcelLink => WorkbookPath is not null;
    public string ExcelLinkTitle => WorkbookPath is { } p ? Path.GetFileName(p) : "No workbook chosen";

    /// <summary>The loaded table, for the deliverables comparison.</summary>
    internal ResultTable? Table => _table;

    public RelayCommand UpdateExcelCommand => _updateExcel ??= new RelayCommand(() => ExportModelAsync(), () => _table is not null && HasExcelLink);
    private RelayCommand? _updateExcel;

    /// <summary>"Exported 10:42 · watching for saves" for the Excel card.</summary>
    public string ExcelState
    {
        get => _excelState;
        private set => Set(ref _excelState, value);
    }

    // ------------------------------------------------------------------ workbook

    /// <summary>Choose (or change) the workbook. Returns false when cancelled.</summary>
    internal bool ChooseWorkbook()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the workbook with the deliverables list",
            Filter = "Excel workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            FileName = WorkbookPath ?? "",
        };
        if (dialog.ShowDialog() != true) return false;
        _settings.ExcelWorkbook = dialog.FileName;
        _settings.Save();
        Raise(nameof(WorkbookPath));
        Raise(nameof(HasExcelLink));
        Raise(nameof(ExcelLinkTitle));
        WatchExcel();
        return true;
    }

    private Task LinkExcelAsync()
    {
        if (ChooseWorkbook()) Status = $"Using {ExcelLinkTitle}. Compare shows it next to the model.";
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ compare

    private Task CompareExcelAsync()
    {
        if (WorkbookPath is null && !ChooseWorkbook()) return Task.CompletedTask;
        if (_deliverables is not null)
        {
            _deliverables.Window?.Activate();
            return _deliverables.RefreshAsync();
        }
        var vm = new DeliverablesViewModel(this);
        var window = new DeliverablesWindow(vm) { Owner = Application.Current.MainWindow };
        vm.Window = window;
        _deliverables = vm;
        window.Closed += (_, _) => _deliverables = null;
        window.Show();
        return vm.RefreshAsync();
    }

    // ------------------------------------------------------------------ export to Model Export

    private sealed record ExcelPush(ExcelFillPlan Plan, bool Live, bool Written, string Backup);

    /// <summary>
    /// Fills the workbook's Model Export table with the loaded sheets: one row per sheet, sorted by program then
    /// number. Nothing else in the workbook is written. Rows of files that are not open now are kept.
    /// </summary>
    public async Task<bool> ExportModelAsync()
    {
        if (WorkbookPath is null && !ChooseWorkbook()) return false;
        string path = WorkbookPath!;
        string file = Path.GetFileName(path);
        var table = _table;
        if (table is null || !IsSheetsView || !table.ColumnsById.ContainsKey(SheetFieldMap.NumberColumnId))
        {
            Notify(NoticeKind.Info, "Open the Sheets view to export", $"Model Export in {file} is filled from the sheets of the ticked files.");
            return false;
        }
        if (!File.Exists(path))
        {
            Notify(NoticeKind.Warning, $"{file} was not found", path, "Choose workbook…", LinkExcelAsync);
            return false;
        }

        await _excelGate.WaitAsync();
        try
        {
            ExcelState = $"Exporting to {file}…";
            var info = await Task.Run(() => Deliverables.Read(path));
            if (info.ModelExport is not { } me)
            {
                ExcelState = "";
                Notify(NoticeKind.Warning, $"{file} has no Model Export table",
                    "Nexus fills an Excel table named ModelExport (or the table on a 'Model Export' tab). Nothing was written.");
                return false;
            }
            var link = Deliverables.ModelExportLink(path, me, table, _sheetFields);
            var memory = ExcelFillMemory.Load(path);
            bool firstTime = memory.Files.Count == 0;
            var open = Runs.Where(r => !r.Failed).Select(r => r.DocumentTitle).ToList();
            var rows = ExcelFill.Ordered(memory.Merge(ExcelFill.Rows(table, link), open), link);

            var push = await PushAsync(link, rows, allowClear: !firstTime);
            if (!push.Written && !push.Plan.IsEmpty)
            {
                // First export: rows already in Model Export that Nexus did not put there.
                if (!Dialogs.Confirm($"Replace the rows in Model Export?",
                        $"Model Export has {push.Plan.ClearedRows} row(s) that do not come from the files open now. They are replaced by the " +
                        $"{push.Plan.Rows} sheet(s) from Nexus. Formula columns are not touched.", "Replace", kind: NoticeKind.Info))
                {
                    ExcelState = "";
                    return false;
                }
                push = await PushAsync(link, rows, allowClear: true);
            }

            memory.Save(path);
            var plan = push.Plan;
            string time = DateTime.Now.ToString("t");
            ExcelState = plan.IsEmpty ? $"Model Export up to date · {time}" : $"Exported {time}{(push.Live ? " (in Excel: save it there)" : "")}";
            Status = plan.IsEmpty
                ? "Model Export already matches the model."
                : push.Live
                    ? $"Model Export: {plan.Summary}. The values are in Excel now; save the workbook there to keep them."
                    : $"Model Export: {plan.Summary}.";
            if (!plan.IsEmpty)
                HubLog.Info($"Exported to {file} › Model Export: {plan.Summary}{(push.Live ? " (in Excel)" : $". Backup: {push.Backup}")}");
            return true;
        }
        catch (Exception ex)
        {
            HubLog.Warn($"Exporting to {file} failed.", ex);
            ExcelState = "Last export failed";
            Notify(NoticeKind.Warning, $"Model Export in {file} was not updated", ex.Message, "Try again", () => ExportModelAsync());
            return false;
        }
        finally
        {
            _excelGate.Release();
        }
    }

    /// <summary>
    /// On an Excel thread: reads Model Export (as Excel shows it now when the workbook is open), plans the fill and
    /// writes it into Excel or the file, unless rows would be cleared without leave.
    /// </summary>
    private Task<ExcelPush> PushAsync(ExcelLink link, List<IReadOnlyDictionary<string, string>> rows, bool allowClear) =>
        ExcelLive.RunSta(() =>
        {
            var data = ExcelWorkbook.Read(link);
            object? workbook = ExcelLive.FindOpenWorkbook(link.WorkbookPath);
            try
            {
                if (workbook is not null) ExcelLive.Refresh(workbook, link, data);
                var plan = ExcelWorkbook.PlanFill(link, data, rows);
                if (plan.IsEmpty || (plan.ClearedRows > 0 && !allowClear))
                    return new ExcelPush(plan, workbook is not null, false, "");
                if (workbook is not null)
                {
                    ExcelLive.Apply(workbook, plan);
                    return new ExcelPush(plan, true, true, "");
                }
                IgnoreExcelSaves(TimeSpan.FromMinutes(2));
                try
                {
                    return new ExcelPush(plan, false, true, ExcelWorkbook.WriteFill(link, plan));
                }
                finally
                {
                    IgnoreExcelSaves(TimeSpan.FromSeconds(4));
                }
            }
            finally
            {
                if (workbook is not null) Marshal.ReleaseComObject(workbook);
            }
        });

    // ------------------------------------------------------------------ watching the workbook

    private void IgnoreExcelSaves(TimeSpan span) =>
        Interlocked.Exchange(ref _ignoreExcelSavesUntil, DateTime.UtcNow.Add(span).Ticks);

    /// <summary>Watches the workbook's folder for saves (Excel saves to a temporary file and renames it).</summary>
    private void WatchExcel()
    {
        _excelWatcher?.Dispose();
        _excelWatcher = null;
        if (WorkbookPath is not { } workbook) return;
        try
        {
            string path = Path.GetFullPath(workbook);
            string? folder = Path.GetDirectoryName(path);
            if (folder is null || !Directory.Exists(folder)) return;
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            void OnEvent(string changed)
            {
                if (string.Equals(changed, path, StringComparison.OrdinalIgnoreCase))
                    Application.Current?.Dispatcher.BeginInvoke(() => OnExcelSaved(workbook));
            }
            watcher.Changed += (_, e) => OnEvent(e.FullPath);
            watcher.Created += (_, e) => OnEvent(e.FullPath);
            watcher.Renamed += (_, e) => OnEvent(e.FullPath);
            watcher.Error += (_, e) => HubLog.Warn("Watching the workbook stopped.", e.GetException());
            watcher.EnableRaisingEvents = true;
            _excelWatcher = watcher;
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not watch the workbook.", ex);
        }
    }

    /// <summary>A save: wait until Excel has finished writing, then refresh the comparison or say what differs.</summary>
    private async void OnExcelSaved(string workbook)
    {
        _excelSaveDelay?.Cancel();
        var delay = _excelSaveDelay = new CancellationTokenSource();
        try { await Task.Delay(1500, delay.Token); }
        catch (OperationCanceledException) { return; }
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _ignoreExcelSavesUntil) || workbook != WorkbookPath) return;

        if (_deliverables is not null)
        {
            await _deliverables.RefreshAsync();
            return;
        }
        var table = _table;
        if (table is null || !IsSheetsView) return;
        string file = Path.GetFileName(workbook);
        try
        {
            var differences = await Task.Run(() =>
                DeliverablesCompare.Compare(Deliverables.Read(workbook).Items, table).Count(c => c.Result != DeliverableResult.Match));
            if (differences > 0)
                Notify(NoticeKind.Info, $"{file} was saved: {differences} drawing(s) differ from the model",
                    "Compare shows the deliverables list next to the model.", "Compare…", CompareExcelAsync);
            else
                Status = $"{file} was saved; the deliverables list matches the model.";
        }
        catch (Exception ex)
        {
            HubLog.Warn($"Reading {file} after a save failed.", ex);
        }
    }
}
