using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;
using Nexus.Hub.Excel;

namespace Nexus.Hub.ViewModels;

/// <summary>
/// The live Excel link: link a workbook, update it from the loaded sheets (into Excel itself when the workbook is
/// open there, otherwise into the file), and notice when it is saved with values that differ from the model.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _excelGate = new(1, 1);
    private FileSystemWatcher? _excelWatcher;
    private CancellationTokenSource? _excelSaveDelay;
    /// <summary>Saves Nexus makes itself are not reported as changes (UTC ticks).</summary>
    private long _ignoreExcelSavesUntil;
    private string _excelState = "";

    public RelayCommand UpdateExcelCommand => _updateExcel ??= new RelayCommand(() => UpdateExcelAsync(auto: false), () => _table is not null);
    private RelayCommand? _updateExcel;

    /// <summary>"Updates automatically · watching for saves", "Updated 10:42 (in Excel)"... for the Excel card.</summary>
    public string ExcelState
    {
        get => _excelState;
        private set => Set(ref _excelState, value);
    }

    private string ExcelModeText(ExcelLink link) =>
        (link.AutoUpdate ? "Updates automatically" : "Updates when you ask") + (link.Watch ? " · watching for saves" : "");

    // ------------------------------------------------------------------ link

    private Task LinkExcelAsync()
    {
        if (_table is null) return Task.CompletedTask;
        var vm = new ExcelLinkViewModel(_table, _sheetFields, _excelLink, Runs.Where(r => !r.Failed).Select(r => r.DocumentTitle).ToList());
        var window = new ExcelLinkWindow(vm) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true || vm.Result is not { } link) return Task.CompletedTask;

        link.Save();
        _excelLink = null; // the setter restarts the watcher even when the same link object comes back
        ExcelLink = link;
        _settings.ExcelWorkbook = link.WorkbookPath;
        _settings.Save();
        ExcelState = ExcelModeText(link);
        if (link.AutoUpdate && IsSheetsView && Runs.Any(r => !r.Failed))
            return UpdateExcelAsync(auto: false);
        Status = $"Linked {link.Title}. Use Update Excel to fill it, or Compare to see what differs.";
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ Nexus → Excel

    private sealed record ExcelPush(ExcelFillPlan? Plan, List<ExcelDiff> ExcelEdits, bool Live, bool Written, string Backup);

    /// <summary>
    /// Fills the linked workbook from the loaded sheets. Automatic updates never overwrite values typed in Excel
    /// that are not in the model yet (they ask to Compare first), and never clear rows the first time.
    /// </summary>
    public async Task UpdateExcelAsync(bool auto)
    {
        if (_excelLink is null)
        {
            if (!auto) await LinkExcelAsync();
            return;
        }
        var link = _excelLink;
        var table = _table;
        string file = Path.GetFileName(link.WorkbookPath);
        if (table is null || !IsSheetsView || !table.ColumnsById.ContainsKey(link.KeyColumnId))
        {
            if (!auto)
                Notify(NoticeKind.Info, "Open the Sheets view to update Excel",
                    $"{file} is filled from the sheets of the ticked files.");
            return;
        }
        if (!File.Exists(link.WorkbookPath))
        {
            Notify(NoticeKind.Warning, $"{file} was not found", link.WorkbookPath, "Link a workbook…", LinkExcelAsync);
            return;
        }

        await _excelGate.WaitAsync();
        try
        {
            var memory = ExcelFillMemory.Load(link.WorkbookPath);
            bool firstTime = memory.Files.Count == 0;
            var lastSent = memory.LastSent(link);
            var open = Runs.Where(r => !r.Failed).Select(r => r.DocumentTitle).ToList();
            var rows = link.Mode == ExcelLinkMode.Fill
                ? ExcelFill.Ordered(memory.Merge(ExcelFill.Rows(table, link), open), link)
                : null;
            if (link.Mode == ExcelLinkMode.Match) memory.Merge(ExcelFill.Rows(table, link), open);

            bool overwriteEdits = false, allowClear = !firstTime;
            while (true)
            {
                ExcelState = $"Updating {file}…";
                var push = await PushAsync(link, table, rows, lastSent, overwriteEdits, allowClear);

                if (push.ExcelEdits.Count > 0)
                {
                    string list = DescribeEdits(push.ExcelEdits);
                    if (auto)
                    {
                        ExcelState = "Waiting: Excel has changes not in the model";
                        Notify(NoticeKind.Info, $"{file} has {push.ExcelEdits.Count} value(s) typed in Excel that are not in the model",
                            list + "\nNexus left the workbook as it is so they are not lost. Compare to take them into the model, or Update Excel to overwrite them.",
                            "Compare…", CompareExcelAsync);
                        return;
                    }
                    if (!Dialogs.Confirm($"Overwrite {push.ExcelEdits.Count} value(s) typed in Excel?",
                            list + "\n\nThese were changed in Excel and are not in the model. Overwrite them with the model's values? " +
                            "(Cancel, then Compare, to take them into the model instead.)", "Overwrite", kind: NoticeKind.Warning))
                    {
                        ExcelState = ExcelModeText(link);
                        return;
                    }
                    overwriteEdits = true;
                    continue;
                }

                var plan = push.Plan!;
                if (!push.Written && !plan.IsEmpty)
                {
                    // Rows would be cleared on the first update: rows that were not put there by Nexus.
                    if (auto)
                    {
                        ExcelState = ExcelModeText(link);
                        Notify(NoticeKind.Info, $"Updating {file} would clear {plan.ClearedRows} row(s)",
                            $"{link.Title} has rows that do not come from the files open now. Update Excel to replace them with the {plan.Rows} row(s) from Nexus.",
                            "Update Excel", () => UpdateExcelAsync(auto: false));
                        return;
                    }
                    if (!Dialogs.Confirm($"Replace the rows in {link.Title}?",
                            $"{plan.ClearedRows} row(s) in the workbook do not come from the files open now and will be cleared; " +
                            $"{plan.Rows} row(s) from Nexus take their place. Formula columns are not touched.",
                            "Replace", kind: NoticeKind.Info))
                    {
                        ExcelState = ExcelModeText(link);
                        return;
                    }
                    allowClear = true;
                    continue;
                }

                memory.Save(link.WorkbookPath);
                string time = DateTime.Now.ToString("t");
                if (plan.IsEmpty)
                {
                    ExcelState = $"{ExcelModeText(link)} · up to date {time}";
                    if (!auto) Status = $"{link.Title} is already up to date.";
                }
                else
                {
                    ExcelState = $"{ExcelModeText(link)} · updated {time}{(push.Live ? " in Excel" : "")}";
                    Status = push.Live
                        ? $"{link.Title}: {plan.Summary}. The values are in Excel now; save the workbook there to keep them."
                        : $"{link.Title}: {plan.Summary}.";
                    HubLog.Info($"Updated {link.Title}: {plan.Summary}{(push.Live ? " (in Excel)" : $". Backup: {push.Backup}")}");
                }
                return;
            }
        }
        catch (Exception ex)
        {
            HubLog.Warn($"Updating {link.Title} failed.", ex);
            ExcelState = "Last update failed";
            Notify(NoticeKind.Warning, $"{file} was not updated", ex.Message, "Try again", () => UpdateExcelAsync(auto: false));
        }
        finally
        {
            _excelGate.Release();
        }
    }

    /// <summary>
    /// On an Excel thread: reads the workbook (and what Excel shows now when it is open), makes the plan, and writes
    /// it into Excel or the file, unless values typed in Excel would be overwritten or rows cleared without leave.
    /// </summary>
    private Task<ExcelPush> PushAsync(ExcelLink link, ResultTable table, List<IReadOnlyDictionary<string, string>>? fillRows,
        Dictionary<string, Dictionary<string, string>> lastSent, bool overwriteEdits, bool allowClear) =>
        ExcelLive.RunSta(() =>
        {
            var data = ExcelWorkbook.Read(link);
            object? workbook = ExcelLive.FindOpenWorkbook(link.WorkbookPath);
            try
            {
                if (workbook is not null) ExcelLive.Refresh(workbook, link, data);
                if (!overwriteEdits)
                {
                    var edits = ExcelFill.ExcelEdits(ExcelCompare.Compare(table, data, link, new List<string>()), lastSent);
                    if (edits.Count > 0) return new ExcelPush(null, edits, workbook is not null, false, "");
                }
                ExcelFillPlan plan;
                if (fillRows is not null)
                {
                    plan = ExcelWorkbook.PlanFill(link, data, fillRows);
                }
                else
                {
                    var (updates, newRows) = ExcelFill.MatchUpdates(table, data, link);
                    plan = ExcelWorkbook.PlanMatch(link, data, updates, newRows);
                }
                if (plan.IsEmpty || (plan.ClearedRows > 0 && !allowClear))
                    return new ExcelPush(plan, new(), workbook is not null, false, "");

                if (workbook is not null)
                {
                    ExcelLive.Apply(workbook, plan);
                    return new ExcelPush(plan, new(), true, true, "");
                }
                IgnoreExcelSaves(TimeSpan.FromMinutes(2));
                try
                {
                    string backup = ExcelWorkbook.WriteFill(link, plan);
                    return new ExcelPush(plan, new(), false, true, backup);
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

    /// <summary>Compare's "Use model": writes these cells and rows (into Excel when the workbook is open there).</summary>
    internal string WriteExcelCells(ExcelLink link, IReadOnlyList<ExcelCellUpdate> updates, IReadOnlyList<IReadOnlyDictionary<string, string>> newRows)
    {
        var result = ExcelLive.RunSta(() =>
        {
            var data = ExcelWorkbook.Read(link);
            object? workbook = ExcelLive.FindOpenWorkbook(link.WorkbookPath);
            try
            {
                if (workbook is not null) ExcelLive.Refresh(workbook, link, data);
                var plan = ExcelWorkbook.PlanMatch(link, data, updates, newRows);
                if (workbook is not null)
                {
                    ExcelLive.Apply(workbook, plan);
                    return "in Excel (save the workbook there to keep them)";
                }
                IgnoreExcelSaves(TimeSpan.FromMinutes(2));
                try { return "backup: " + ExcelWorkbook.WriteFill(link, plan); }
                finally { IgnoreExcelSaves(TimeSpan.FromSeconds(4)); }
            }
            finally
            {
                if (workbook is not null) Marshal.ReleaseComObject(workbook);
            }
        }).GetAwaiter().GetResult();
        if (link.Mode == ExcelLinkMode.Fill || link.AutoUpdate)
        {
            // Remember what was sent so these values are not mistaken for Excel edits later.
            var memory = ExcelFillMemory.Load(link.WorkbookPath);
            if (_table is not null) memory.Merge(ExcelFill.Rows(_table, link), Runs.Where(r => !r.Failed).Select(r => r.DocumentTitle));
            memory.Save(link.WorkbookPath);
        }
        return result;
    }

    private static string DescribeEdits(IReadOnlyList<ExcelDiff> edits) =>
        string.Join("\n", edits.Take(6).Select(d => $"• {d.Key} › {d.Header}: Excel '{d.ExcelValue}', model '{d.ModelValue}'"))
        + (edits.Count > 6 ? $"\n• … and {edits.Count - 6} more" : "");

    // ------------------------------------------------------------------ Excel → Nexus

    private async Task CompareExcelAsync()
    {
        if (_table is null) return;
        if (_excelLink is null)
        {
            await LinkExcelAsync();
            if (_excelLink is null) return;
        }
        CommitGridEdits?.Invoke();
        var link = _excelLink;
        var table = _table;
        if (!table.ColumnsById.ContainsKey(link.KeyColumnId))
        {
            Notify(NoticeKind.Info, "Nothing to compare the workbook with yet",
                $"The loaded data has no '{link.KeyColumnId}' column to match rows on. Open the Sheets view, or link the workbook again.");
            return;
        }

        List<ExcelDiff> diffs;
        var warnings = new List<string>();
        try
        {
            Status = $"Reading {Path.GetFileName(link.WorkbookPath)}…";
            diffs = await ExcelLive.RunSta(() =>
            {
                var data = ExcelWorkbook.Read(link);
                object? workbook = ExcelLive.FindOpenWorkbook(link.WorkbookPath);
                try
                {
                    if (workbook is not null) ExcelLive.Refresh(workbook, link, data);
                }
                finally
                {
                    if (workbook is not null) Marshal.ReleaseComObject(workbook);
                }
                warnings.AddRange(data.Warnings);
                return ExcelCompare.Compare(table, data, link, warnings);
            });
        }
        catch (Exception ex)
        {
            HubLog.Warn("Reading the linked workbook failed.", ex);
            Notify(NoticeKind.Warning, $"Could not read {Path.GetFileName(link.WorkbookPath)}", ex.Message);
            return;
        }
        if (link.Mode == ExcelLinkMode.Fill)
        {
            // Fill mode: rows of other files are expected in the workbook.
            var open = Runs.Where(r => !r.Failed).Select(r => r.DocumentTitle).ToList();
            if (ExcelFillMemory.Load(link.WorkbookPath).Closed(open).Any())
                diffs.RemoveAll(d => d.Kind == ExcelDiffKind.OnlyInExcel);
        }

        var vm = new CompareViewModel(link, diffs, warnings, this);
        new CompareWindow(vm) { Owner = Application.Current.MainWindow }.ShowDialog();
        Status = vm.Outcome ?? "Compare closed.";
    }

    // ------------------------------------------------------------------ watching the workbook

    private void IgnoreExcelSaves(TimeSpan span) =>
        Interlocked.Exchange(ref _ignoreExcelSavesUntil, DateTime.UtcNow.Add(span).Ticks);

    /// <summary>Watches the linked workbook's folder for saves (Excel saves to a temporary file and renames it).</summary>
    private void WatchExcel()
    {
        _excelWatcher?.Dispose();
        _excelWatcher = null;
        if (_excelLink is not { } link)
        {
            ExcelState = "";
            return;
        }
        ExcelState = ExcelModeText(link);
        if (!link.Watch) return;
        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(link.WorkbookPath));
            if (folder is null || !Directory.Exists(folder))
            {
                ExcelState = "Workbook folder not found";
                return;
            }
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            string path = Path.GetFullPath(link.WorkbookPath);
            void OnEvent(string changed)
            {
                if (string.Equals(changed, path, StringComparison.OrdinalIgnoreCase))
                    Application.Current?.Dispatcher.BeginInvoke(() => OnExcelSaved(link));
            }
            watcher.Changed += (_, e) => OnEvent(e.FullPath);
            watcher.Created += (_, e) => OnEvent(e.FullPath);
            watcher.Renamed += (_, e) => OnEvent(e.FullPath);
            watcher.Error += (_, e) => HubLog.Warn("Watching the linked workbook stopped.", e.GetException());
            watcher.EnableRaisingEvents = true;
            _excelWatcher = watcher;
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not watch the linked workbook.", ex);
            ExcelState = ExcelModeText(link) + " (not watching: " + ex.Message + ")";
        }
    }

    /// <summary>A save: wait until Excel has finished writing, then look for values that differ from the model.</summary>
    private async void OnExcelSaved(ExcelLink link)
    {
        _excelSaveDelay?.Cancel();
        var delay = _excelSaveDelay = new CancellationTokenSource();
        try { await Task.Delay(1500, delay.Token); }
        catch (OperationCanceledException) { return; }
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _ignoreExcelSavesUntil) || !ReferenceEquals(link, _excelLink)) return;

        string file = Path.GetFileName(link.WorkbookPath);
        var table = _table;
        if (table is null || !IsSheetsView || !table.ColumnsById.ContainsKey(link.KeyColumnId))
        {
            ExcelState = $"{ExcelModeText(link)} · saved {DateTime.Now:t}";
            return;
        }
        List<ExcelDiff> changes;
        try
        {
            changes = await Task.Run(() =>
            {
                var data = ExcelWorkbook.Read(link);
                return ExcelCompare.Compare(table, data, link, new List<string>())
                    .Where(d => d.Kind == ExcelDiffKind.Different && d.Direction is ExcelDirection.Both or ExcelDirection.ToModel).ToList();
            });
        }
        catch (Exception ex)
        {
            HubLog.Warn($"Reading {file} after a save failed.", ex);
            Notify(NoticeKind.Warning, $"Could not read {file} after it was saved", ex.Message);
            return;
        }
        ExcelState = $"{ExcelModeText(link)} · saved {DateTime.Now:t}";
        if (changes.Count == 0)
        {
            Status = $"{file} was saved; it matches the model.";
            return;
        }
        Notify(NoticeKind.Info, $"{file} was saved with {changes.Count} value(s) that differ from the model",
            DescribeEdits(changes) + "\nCompare to take them into the model, or leave them.", "Compare…", CompareExcelAsync);
    }
}
