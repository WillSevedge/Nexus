using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;

namespace Nexus.Hub.ViewModels;

/// <summary>A Nexus column the user can link an Excel column to.</summary>
public sealed record ColumnChoice(string Id, string Label);

/// <summary>One Excel header and the Nexus column it is linked to.</summary>
public sealed class MappingRow : Observable
{
    private string _columnId;

    public MappingRow(string header, string columnId)
    {
        Header = header;
        _columnId = columnId;
    }

    public string Header { get; }

    public string ColumnId
    {
        get => _columnId;
        set
        {
            if (Set(ref _columnId, value ?? "")) Raise(nameof(IsLinked));
        }
    }

    public bool IsLinked => _columnId.Length > 0;
}

/// <summary>Link an Excel workbook: pick the file, the sheet, the header row, and match its columns to Nexus columns.</summary>
public sealed class ExcelLinkViewModel : Observable
{
    private readonly ResultTable _table;
    private readonly SheetFieldMap _fields;
    private readonly ExcelLink? _previous;
    private string _workbookPath = "";
    private List<ExcelSheetInfo> _sheets = new();
    private ExcelSheetInfo? _sheet;
    private int _headerRow = 1;
    private string? _keyHeader;
    private string _message = "Choose the workbook (your template or sheet index).";

    public ExcelLinkViewModel(ResultTable table, SheetFieldMap fields, ExcelLink? previous)
    {
        _table = table;
        _fields = fields;
        _previous = previous;
        ColumnChoices = new List<ColumnChoice> { new("", "(not linked)") }
            .Concat(table.Columns.Select(c => new ColumnChoice(c.Id, c.Group == SheetFieldMap.Group ? $"Sheet › {c.Name}" : c.Id)))
            .ToList();
        BrowseCommand = new RelayCommand(() => { Browse(); return Task.CompletedTask; });
        if (previous is not null && File.Exists(previous.WorkbookPath)) Open(previous.WorkbookPath);
    }

    public RelayCommand BrowseCommand { get; }
    public List<ColumnChoice> ColumnChoices { get; }
    public ObservableCollection<MappingRow> Mappings { get; } = new();
    public ExcelLink? Result { get; private set; }

    public string WorkbookPath
    {
        get => _workbookPath;
        private set => Set(ref _workbookPath, value);
    }

    public List<ExcelSheetInfo> Sheets
    {
        get => _sheets;
        private set => Set(ref _sheets, value);
    }

    public ExcelSheetInfo? SelectedSheet
    {
        get => _sheet;
        set
        {
            if (!Set(ref _sheet, value) || value is null) return;
            _headerRow = Math.Max(1, value.HeaderRow);
            Raise(nameof(HeaderRow));
            LoadHeaders();
        }
    }

    public int HeaderRow
    {
        get => _headerRow;
        set
        {
            if (value < 1 || !Set(ref _headerRow, value)) return;
            LoadHeaders();
        }
    }

    /// <summary>The Excel column rows are matched on (normally the sheet number).</summary>
    public string? KeyHeader
    {
        get => _keyHeader;
        set => Set(ref _keyHeader, value);
    }

    public IEnumerable<string> KeyChoices => Mappings.Select(m => m.Header);

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            FileName = WorkbookPath,
        };
        if (dialog.ShowDialog() == true) Open(dialog.FileName);
    }

    private void Open(string path)
    {
        try
        {
            Sheets = ExcelWorkbook.Inspect(path);
            WorkbookPath = path;
            var preferred = _previous is not null && string.Equals(_previous.WorkbookPath, path, StringComparison.OrdinalIgnoreCase)
                ? Sheets.FirstOrDefault(s => s.Name == _previous.Worksheet)
                : null;
            if (preferred is not null) _headerRow = _previous!.HeaderRow;
            _sheet = preferred ?? Sheets.FirstOrDefault(s => s.Headers.Count > 0) ?? Sheets.FirstOrDefault();
            if (preferred is null && _sheet is not null) _headerRow = Math.Max(1, _sheet.HeaderRow);
            Raise(nameof(SelectedSheet));
            Raise(nameof(HeaderRow));
            LoadHeaders();
        }
        catch (Exception ex)
        {
            Message = "Could not open the workbook: " + ex.Message;
        }
    }

    private void LoadHeaders()
    {
        Mappings.Clear();
        if (_sheet is null || WorkbookPath.Length == 0) return;
        List<string> headers;
        try { headers = ExcelWorkbook.HeadersAt(WorkbookPath, _sheet.Name, _headerRow); }
        catch (Exception ex)
        {
            Message = "Could not read the headers: " + ex.Message;
            return;
        }

        var suggested = ExcelCompare.AutoMap(headers, _table, _fields);
        bool samePlace = _previous is not null && string.Equals(_previous.WorkbookPath, WorkbookPath, StringComparison.OrdinalIgnoreCase)
                         && _previous.Worksheet == _sheet.Name && _previous.HeaderRow == _headerRow;
        foreach (var s in suggested)
        {
            string id = samePlace ? _previous!.Columns.FirstOrDefault(c => c.Header == s.Header)?.ColumnId ?? s.ColumnId : s.ColumnId;
            if (id.Length > 0 && ColumnChoices.All(c => c.Id != id)) id = "";
            Mappings.Add(new MappingRow(s.Header, id));
        }
        Raise(nameof(KeyChoices));
        string keyId = samePlace ? _previous!.KeyColumnId : SheetFieldMap.NumberColumnId;
        KeyHeader = Mappings.FirstOrDefault(m => m.ColumnId == keyId)?.Header ?? Mappings.FirstOrDefault(m => m.IsLinked)?.Header;

        Message = headers.Count == 0
            ? $"Row {_headerRow} of '{_sheet.Name}' has no headers. Pick the row that holds the column titles."
            : $"{Mappings.Count(m => m.IsLinked)} of {headers.Count} columns matched automatically. Check them, link the rest, and pick the column rows are matched on.";
    }

    /// <summary>Builds the link; null with a message when something is missing.</summary>
    public bool TryAccept()
    {
        if (WorkbookPath.Length == 0 || _sheet is null)
        {
            Message = "Choose a workbook and a worksheet.";
            return false;
        }
        var key = Mappings.FirstOrDefault(m => m.Header == KeyHeader);
        if (key is null || !key.IsLinked)
        {
            Message = "Pick the column rows are matched on (e.g. the sheet number), and link it to the matching Nexus column.";
            return false;
        }
        Result = new ExcelLink
        {
            WorkbookPath = WorkbookPath,
            Worksheet = _sheet.Name,
            HeaderRow = _headerRow,
            KeyColumnId = key.ColumnId,
            Columns = Mappings.Select(m => new ExcelColumnMap { Header = m.Header, ColumnId = m.ColumnId }).ToList(),
        };
        return true;
    }
}

public enum DiffAction { Skip, UseExcel, UseModel, AddToExcel }

/// <summary>One difference in the Compare window, and what to do about it.</summary>
public sealed class DiffRow : Observable
{
    private DiffAction _action;

    public DiffRow(ExcelDiff diff) => Diff = diff;

    public ExcelDiff Diff { get; }
    public string Key => Diff.Key;
    public string Field => Diff.Header.Length > 0 ? Diff.Header : "(whole row)";
    public string ModelValue => Diff.Kind == ExcelDiffKind.OnlyInExcel ? "(not in the model)" : Diff.ModelValue ?? "";
    public string ExcelValue => Diff.Kind == ExcelDiffKind.OnlyInModel ? "(not in the workbook)" : Diff.ExcelValue ?? "";
    public string Where => Diff.Row is null ? "" : $"{Diff.Row.Document}";

    public string KindText => Diff.Kind switch
    {
        ExcelDiffKind.Different => "Differs",
        ExcelDiffKind.OnlyInModel => "Only in model",
        _ => "Only in Excel",
    };

    public IReadOnlyList<DiffAction> Choices => Diff.Kind switch
    {
        ExcelDiffKind.Different => new[] { DiffAction.Skip, DiffAction.UseExcel, DiffAction.UseModel },
        ExcelDiffKind.OnlyInModel => new[] { DiffAction.Skip, DiffAction.AddToExcel },
        _ => new[] { DiffAction.Skip },
    };

    public DiffAction Action
    {
        get => _action;
        set => Set(ref _action, Choices.Contains(value) ? value : DiffAction.Skip);
    }
}

/// <summary>
/// Compare the linked workbook with the loaded data, and choose per difference which side wins:
/// Excel → model (staged as normal pending edits, applied with Review &amp; apply) or model → Excel
/// (written to the workbook straight away, after a backup).
/// </summary>
public sealed class CompareViewModel : Observable
{
    private readonly ExcelLink _link;
    private readonly MainViewModel _main;

    public CompareViewModel(ExcelLink link, List<ExcelDiff> diffs, List<string> warnings, MainViewModel main)
    {
        _link = link;
        _main = main;
        foreach (var d in diffs) Rows.Add(new DiffRow(d));
        Warnings = warnings;
        int differ = diffs.Count(d => d.Kind == ExcelDiffKind.Different);
        int onlyModel = diffs.Count(d => d.Kind == ExcelDiffKind.OnlyInModel);
        int onlyExcel = diffs.Count(d => d.Kind == ExcelDiffKind.OnlyInExcel);
        Summary = diffs.Count == 0
            ? $"The model and {link.Title} match on every linked column."
            : $"{differ} value(s) differ · {onlyModel} row(s) only in the model · {onlyExcel} row(s) only in Excel.  Choose what to do with each, then Apply.";

        UseExcelForAll = new RelayCommand(() => SetAll(DiffAction.UseExcel));
        UseModelForAll = new RelayCommand(() => SetAll(DiffAction.UseModel));
        AddAllToExcel = new RelayCommand(() => SetAll(DiffAction.AddToExcel));
        SkipAll = new RelayCommand(() => SetAll(DiffAction.Skip));
    }

    public ObservableCollection<DiffRow> Rows { get; } = new();
    public List<string> Warnings { get; }
    public bool HasWarnings => Warnings.Count > 0;
    public string WarningText => string.Join(Environment.NewLine, Warnings);
    public string Summary { get; }
    public string Title => "Compare with " + _link.Title;

    public RelayCommand UseExcelForAll { get; }
    public RelayCommand UseModelForAll { get; }
    public RelayCommand AddAllToExcel { get; }
    public RelayCommand SkipAll { get; }

    /// <summary>Status line for the main window after Apply.</summary>
    public string? Outcome { get; private set; }

    private Task SetAll(DiffAction action)
    {
        foreach (var r in Rows)
            if (r.Choices.Contains(action)) r.Action = action;
        return Task.CompletedTask;
    }

    /// <summary>Carries out the chosen actions. Returns false to keep the window open.</summary>
    public bool Apply()
    {
        var toModel = Rows.Where(r => r.Action == DiffAction.UseExcel).ToList();
        var toExcel = Rows.Where(r => r.Action == DiffAction.UseModel).ToList();
        var addRows = Rows.Where(r => r.Action == DiffAction.AddToExcel).ToList();
        if (toModel.Count + toExcel.Count + addRows.Count == 0)
        {
            Outcome = "Nothing applied.";
            return true;
        }

        var problems = new List<string>();
        int staged = 0;
        foreach (var r in toModel)
        {
            var reason = _main.TrySetValue(r.Diff.Row!, r.Diff.ColumnId, r.Diff.ExcelValue ?? "");
            if (reason is null) staged++;
            else problems.Add($"{r.Key} › {r.Field}: {reason}");
        }

        int written = 0, added = 0;
        var updates = new List<ExcelCellUpdate>();
        foreach (var r in toExcel)
        {
            if (r.Diff.Cell is not { } cell) continue;
            if (cell.HasFormula)
            {
                problems.Add($"{r.Key} › {r.Field}: the Excel cell has a formula; it is not overwritten.");
                continue;
            }
            updates.Add(new ExcelCellUpdate(cell.Row, cell.Column, r.Diff.ModelValue ?? ""));
        }
        var newRows = addRows.Where(r => r.Diff.Row is not null).Select(r => (IReadOnlyDictionary<string, string>)_link.Columns
                .Where(c => c.ColumnId.Length > 0 && r.Diff.Row!.Values.ContainsKey(c.ColumnId))
                .ToDictionary(c => c.Header, c => r.Diff.Row!.Values[c.ColumnId].Value ?? ""))
            .ToList();

        if (updates.Count + newRows.Count > 0)
        {
            if (!Dialogs.Confirm($"Update {Path.GetFileName(_link.WorkbookPath)}?",
                    $"Write {updates.Count} cell(s){(newRows.Count > 0 ? $" and add {newRows.Count} row(s)" : "")}. Only those cells change; " +
                    "a backup copy is saved first. The workbook must be closed in Excel.",
                    "Write to Excel", kind: NoticeKind.Info))
                return false;
            try
            {
                string backup = ExcelWorkbook.Write(_link, updates, newRows);
                written = updates.Count;
                added = newRows.Count;
                HubLog.Info($"Updated {_link.Title}: {written} cell(s), {added} new row(s). Backup: {backup}");
            }
            catch (Exception ex)
            {
                Dialogs.Message("Excel was not updated", ex.Message, NoticeKind.Warning);
                return false;
            }
        }

        var parts = new List<string>();
        if (staged > 0) parts.Add($"{staged} value(s) from Excel put in the table (Review & apply to write them to the model)");
        if (written > 0) parts.Add($"{written} cell(s) updated in Excel");
        if (added > 0) parts.Add($"{added} row(s) added to Excel");
        if (problems.Count > 0)
        {
            parts.Add($"{problems.Count} skipped");
            Dialogs.Message($"{problems.Count} value(s) were skipped",
                string.Join(Environment.NewLine, problems.Take(30)) + (problems.Count > 30 ? "\n…" : ""), NoticeKind.Info);
        }
        Outcome = string.Join("; ", parts) + ".";
        return true;
    }
}
