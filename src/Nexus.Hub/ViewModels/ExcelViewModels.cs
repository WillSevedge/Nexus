using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;

namespace Nexus.Hub.ViewModels;

/// <summary>A value an Excel column can be linked to: a Nexus column, the program, the file name, or a fixed text.</summary>
public sealed record ColumnChoice(string Id, string Label);

/// <summary>Which way a column goes, as shown in the link window.</summary>
public sealed record DirectionChoice(ExcelDirection Value, string Label);

/// <summary>Where the link works: an Excel table, or the rows under a header row of a worksheet.</summary>
public sealed record PlaceChoice(string Worksheet, ExcelTableInfo? Table, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One Excel column: the Nexus value it is linked to, and which way it goes.</summary>
public sealed class MappingRow : Observable
{
    private string _columnId;
    private ExcelDirection _direction;
    private string _fixedText;

    public MappingRow(string header, string columnId, ExcelDirection direction, string? fixedText, bool isFormula)
    {
        Header = header;
        _columnId = isFormula ? "" : columnId;
        _direction = isFormula ? ExcelDirection.None : direction;
        _fixedText = fixedText ?? "";
        IsFormula = isFormula;
    }

    public string Header { get; }

    /// <summary>Excel calculates this column: Nexus reads around it and never writes it.</summary>
    public bool IsFormula { get; }
    public bool CanLink => !IsFormula;

    public string ColumnId
    {
        get => _columnId;
        set
        {
            if (IsFormula || !Set(ref _columnId, value ?? "")) return;
            // Program, file name and fixed text only ever go to Excel.
            if (ExcelSources.IsSpecial(_columnId)) Direction = ExcelDirection.ToExcel;
            else if (_columnId.Length > 0 && _direction == ExcelDirection.None) Direction = ExcelDirection.Both;
            Raise(nameof(IsLinked));
            Raise(nameof(IsFixed));
            Raise(nameof(DirectionChoices));
            Raise(nameof(Note));
        }
    }

    public ExcelDirection Direction
    {
        get => _direction;
        set
        {
            if (Set(ref _direction, value)) Raise(nameof(Note));
        }
    }

    public string FixedText
    {
        get => _fixedText;
        set => Set(ref _fixedText, value ?? "");
    }

    public bool IsLinked => _columnId.Length > 0;
    public bool IsFixed => _columnId == ExcelSources.Fixed;

    public IReadOnlyList<DirectionChoice> DirectionChoices =>
        IsFormula || !IsLinked ? new[] { Directions[3] }
        : ExcelSources.IsSpecial(_columnId) ? new[] { Directions[1] }
        : Directions;

    public static readonly DirectionChoice[] Directions =
    {
        new(ExcelDirection.Both, "Both ways"),
        new(ExcelDirection.ToExcel, "Nexus → Excel"),
        new(ExcelDirection.ToModel, "Excel → Nexus"),
        new(ExcelDirection.None, "Not used"),
    };

    public string Note => IsFormula ? "Formula in Excel: left alone"
        : !IsLinked ? ""
        : _direction switch
        {
            ExcelDirection.Both => "Filled by Nexus; edits in Excel can go back to the model",
            ExcelDirection.ToExcel => "Filled by Nexus",
            ExcelDirection.ToModel => "Read from Excel into the model",
            _ => "Ignored",
        };
}

/// <summary>
/// Link an Excel workbook: the file (or a new copy of the Budget Tool), where in it (an Excel table or a worksheet's
/// header row), how rows line up (fill the list, or match on a key), each column's Nexus value and direction, and
/// how live the link is.
/// </summary>
public sealed class ExcelLinkViewModel : Observable
{
    /// <summary>The Budget Tool's landing table.</summary>
    public const string BudgetToolTable = "ModelExport";

    private readonly ResultTable _table;
    private readonly SheetFieldMap _fields;
    private readonly ExcelLink? _previous;
    private readonly IReadOnlyList<string> _openFiles;
    private string _workbookPath = "";
    private List<ExcelSheetInfo> _sheets = new();
    private List<PlaceChoice> _places = new();
    private PlaceChoice? _place;
    private int _headerRow = 1;
    private string? _keyHeader;
    private bool _fillMode;
    private bool _autoUpdate;
    private bool _watch = true;
    private bool _busy;
    private int _loadVersion;
    private int _headersVersion;
    private string _message = "Choose the workbook, or start a new one from the Budget Tool.";

    public ExcelLinkViewModel(ResultTable table, SheetFieldMap fields, ExcelLink? previous, IReadOnlyList<string> openFiles)
    {
        _table = table;
        _fields = fields;
        _previous = previous;
        _openFiles = openFiles;
        ColumnChoices = new List<ColumnChoice>
            {
                new("", "(not linked)"),
                new(ExcelSources.Program, "Program (Revit, AutoCAD, Civil 3D, Plant 3D)"),
                new(ExcelSources.File, "File name"),
                new(ExcelSources.Fixed, "Fixed text…"),
            }
            .Concat(table.Columns.Select(c => new ColumnChoice(c.Id, c.Group == SheetFieldMap.Group ? c.Label : c.Id)))
            .ToList();
        BrowseCommand = new RelayCommand(() => { Browse(); return Task.CompletedTask; }, () => !_busy);
        NewFromTemplateCommand = new RelayCommand(() => { NewFromTemplate(); return Task.CompletedTask; }, () => !_busy);
        ForgetClosedCommand = new RelayCommand(() => { ForgetClosed(); return Task.CompletedTask; }, () => ClosedFiles.Count > 0);
        if (previous is not null && File.Exists(previous.WorkbookPath)) _ = OpenAsync(previous.WorkbookPath);
    }

    public RelayCommand BrowseCommand { get; }
    public RelayCommand NewFromTemplateCommand { get; }
    public RelayCommand ForgetClosedCommand { get; }
    public List<ColumnChoice> ColumnChoices { get; }
    public ObservableCollection<MappingRow> Mappings { get; } = new();
    public ExcelLink? Result { get; private set; }

    public string WorkbookPath
    {
        get => _workbookPath;
        private set => Set(ref _workbookPath, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) Raise(nameof(IsReady));
        }
    }

    public bool IsReady => !_busy && _place is not null;

    public List<PlaceChoice> Places
    {
        get => _places;
        private set => Set(ref _places, value);
    }

    /// <summary>The Excel table or worksheet the link works in.</summary>
    public PlaceChoice? SelectedPlace
    {
        get => _place;
        set
        {
            if (!Set(ref _place, value) || value is null) return;
            Raise(nameof(IsTable));
            Raise(nameof(IsReady));
            _headerRow = value.Table?.HeaderRow ?? Math.Max(1, _sheets.FirstOrDefault(s => s.Name == value.Worksheet)?.HeaderRow ?? 1);
            Raise(nameof(HeaderRow));
            _ = LoadHeadersAsync(fromInspect: true);
        }
    }

    /// <summary>An Excel table has its own header row; a worksheet needs one picked.</summary>
    public bool IsTable => _place?.Table is not null;

    public int HeaderRow
    {
        get => _headerRow;
        set
        {
            if (value < 1 || IsTable || !Set(ref _headerRow, value)) return;
            _ = LoadHeadersAsync(fromInspect: false);
        }
    }

    /// <summary>The Excel column rows are matched (and sorted) on: normally the sheet number.</summary>
    public string? KeyHeader
    {
        get => _keyHeader;
        set => Set(ref _keyHeader, value);
    }

    public IEnumerable<string> KeyChoices => Mappings.Where(m => m.IsLinked && !ExcelSources.IsSpecial(m.ColumnId)).Select(m => m.Header);

    /// <summary>Fill the list with every sheet Nexus has (a landing tab), instead of matching existing rows.</summary>
    public bool FillMode
    {
        get => _fillMode;
        set
        {
            if (Set(ref _fillMode, value)) Raise(nameof(MatchMode));
        }
    }

    public bool MatchMode
    {
        get => !_fillMode;
        set => FillMode = !value;
    }

    public bool AutoUpdate
    {
        get => _autoUpdate;
        set => Set(ref _autoUpdate, value);
    }

    public bool Watch
    {
        get => _watch;
        set => Set(ref _watch, value);
    }

    /// <summary>Files whose rows Nexus keeps in the workbook while they are not open.</summary>
    public List<string> ClosedFiles { get; private set; } = new();
    public bool HasClosedFiles => ClosedFiles.Count > 0;
    public string ClosedFilesText => ClosedFiles.Count == 0
        ? "Rows of files that are not open are kept in the workbook until you forget them."
        : $"Kept while not open: {string.Join(", ", ClosedFiles.Take(4))}{(ClosedFiles.Count > 4 ? $" and {ClosedFiles.Count - 4} more" : "")}.";

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    // ------------------------------------------------------------------ workbook

    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            FileName = WorkbookPath,
        };
        if (dialog.ShowDialog() == true) _ = OpenAsync(dialog.FileName);
    }

    private const string MissingTemplate =
        "This build of Nexus has no Budget Tool. Copy Budget_Tool.xlsm to src\\Nexus.Hub\\Templates in your Nexus folder and build again, or use Browse… to link your copy.";

    /// <summary>Saves a copy of the Budget Tool (built into Nexus) where the user chooses, and links it.</summary>
    private void NewFromTemplate()
    {
        if (typeof(ExcelLinkViewModel).Assembly.GetManifestResourceInfo("Budget_Tool.xlsm") is null)
        {
            Message = MissingTemplate;
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "Excel macro-enabled workbook (*.xlsm)|*.xlsm",
            FileName = "Budget_Tool.xlsm",
            Title = "Save a new Budget Tool",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            using var template = typeof(ExcelLinkViewModel).Assembly.GetManifestResourceStream("Budget_Tool.xlsm")
                                 ?? throw new InvalidOperationException(MissingTemplate);
            using (var file = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write))
                template.CopyTo(file);
            _ = OpenAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            Message = "Could not save the Budget Tool: " + ex.Message;
        }
    }

    private async Task OpenAsync(string path)
    {
        int version = ++_loadVersion;
        IsBusy = true;
        Message = $"Reading {Path.GetFileName(path)}…";
        try
        {
            var sheets = await Task.Run(() => ExcelWorkbook.Inspect(path));
            if (version != _loadVersion) return;
            _sheets = sheets;
            WorkbookPath = path;
            LoadClosedFiles();

            var places = new List<PlaceChoice>();
            foreach (var sheet in sheets)
                foreach (var t in sheet.Tables)
                    places.Add(new PlaceChoice(sheet.Name, t, $"Table {t.Name}  (on '{sheet.Name}', {t.DataRows} rows)"));
            foreach (var sheet in sheets)
                places.Add(new PlaceChoice(sheet.Name, null, $"Worksheet '{sheet.Name}'  (rows under a header row)"));
            Places = places;

            bool same = _previous is not null && string.Equals(_previous.WorkbookPath, path, StringComparison.OrdinalIgnoreCase);
            PlaceChoice? pick = null;
            if (same)
                pick = _previous!.Table is { Length: > 0 } pt
                    ? places.FirstOrDefault(p => string.Equals(p.Table?.Name, pt, StringComparison.OrdinalIgnoreCase))
                    : places.FirstOrDefault(p => p.Table is null && p.Worksheet == _previous.Worksheet);
            pick ??= places.FirstOrDefault(p => string.Equals(p.Table?.Name, BudgetToolTable, StringComparison.OrdinalIgnoreCase))
                     ?? places.FirstOrDefault(p => p.Table is not null)
                     ?? places.FirstOrDefault(p => sheets.First(s => s.Name == p.Worksheet).Headers.Count > 0)
                     ?? places.FirstOrDefault();

            bool samePlace = same && pick is not null && (pick.Table?.Name ?? pick.Worksheet) == (_previous!.Table ?? _previous.Worksheet);
            if (samePlace)
            {
                FillMode = _previous!.Mode == ExcelLinkMode.Fill;
                AutoUpdate = _previous.AutoUpdate;
                Watch = _previous.Watch;
            }
            else if (pick?.Table is { } t && string.Equals(t.Name, BudgetToolTable, StringComparison.OrdinalIgnoreCase))
            {
                // The Budget Tool's Model Export: Nexus fills it, live.
                FillMode = true;
                AutoUpdate = true;
                Watch = true;
            }
            _place = null;
            SelectedPlace = pick;
            Raise(nameof(SelectedPlace));
            if (pick is null) Message = "The workbook has no worksheets Nexus can use.";
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) Message = "Could not open the workbook: " + ex.Message;
        }
        finally
        {
            if (version == _loadVersion) IsBusy = false;
        }
    }

    private void LoadClosedFiles()
    {
        ClosedFiles = WorkbookPath.Length == 0 ? new() : ExcelFillMemory.Load(WorkbookPath).Closed(_openFiles).OrderBy(f => f).ToList();
        Raise(nameof(ClosedFiles));
        Raise(nameof(ClosedFilesText));
        Raise(nameof(HasClosedFiles));
    }

    private void ForgetClosed()
    {
        var memory = ExcelFillMemory.Load(WorkbookPath);
        foreach (var file in ClosedFiles) memory.Files.Remove(file);
        memory.Save(WorkbookPath);
        LoadClosedFiles();
        Message = "Forgotten. Their rows leave the workbook at the next update.";
    }

    // ------------------------------------------------------------------ columns

    private async Task LoadHeadersAsync(bool fromInspect)
    {
        var place = _place;
        if (place is null || WorkbookPath.Length == 0) return;
        int version = ++_headersVersion;
        List<string> headers;
        try
        {
            if (place.Table is { } t) headers = t.Headers;
            else if (fromInspect && _sheets.FirstOrDefault(s => s.Name == place.Worksheet) is { } info && info.HeaderRow == _headerRow) headers = info.Headers;
            else
            {
                IsBusy = true;
                string path = WorkbookPath, sheet = place.Worksheet;
                int row = _headerRow;
                headers = await Task.Run(() => ExcelWorkbook.HeadersAt(path, sheet, row));
            }
        }
        catch (Exception ex)
        {
            if (version == _headersVersion) Message = "Could not read the headers: " + ex.Message;
            return;
        }
        finally
        {
            if (version == _headersVersion) IsBusy = false;
        }
        if (version != _headersVersion) return;

        var formulas = place.Table?.FormulaHeaders ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suggested = ExcelCompare.AutoMap(headers, _table, _fields);
        bool samePlace = _previous is not null && string.Equals(_previous.WorkbookPath, WorkbookPath, StringComparison.OrdinalIgnoreCase)
                         && (_previous.Table ?? "") == (place.Table?.Name ?? "") && _previous.Worksheet == place.Worksheet
                         && _previous.HeaderRow == _headerRow;
        Mappings.Clear();
        foreach (var s in suggested)
        {
            var before = samePlace ? _previous!.Columns.FirstOrDefault(c => c.Header == s.Header) : null;
            string id = before?.ColumnId ?? s.ColumnId;
            if (id.Length > 0 && ColumnChoices.All(c => c.Id != id)) id = "";
            var direction = before?.Direction ?? (ExcelSources.IsSpecial(id) ? ExcelDirection.ToExcel : id.Length > 0 ? ExcelDirection.Both : ExcelDirection.None);
            var row = new MappingRow(s.Header, id, direction, before?.FixedText, formulas.Contains(s.Header) || before?.IsFormula == true);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MappingRow.ColumnId)) Raise(nameof(KeyChoices));
            };
            Mappings.Add(row);
        }
        Raise(nameof(KeyChoices));
        string keyId = samePlace ? _previous!.KeyColumnId : SheetFieldMap.NumberColumnId;
        KeyHeader = Mappings.FirstOrDefault(m => m.ColumnId == keyId)?.Header ?? KeyChoices.FirstOrDefault();
        // The key is what rows are found by: Nexus fills it, Excel does not rename sheets through it.
        if (!samePlace && Mappings.FirstOrDefault(m => m.Header == KeyHeader) is { } key) key.Direction = ExcelDirection.ToExcel;

        string where = place.Table is { } table ? $"table {table.Name}" : $"row {_headerRow} of '{place.Worksheet}'";
        Message = headers.Count == 0
            ? $"Row {_headerRow} of '{place.Worksheet}' has no headers. Pick the row that holds the column titles."
            : $"{Mappings.Count(m => m.IsLinked)} of {headers.Count} columns in {where} matched automatically"
              + (formulas.Count > 0 ? $" ({formulas.Count} formula column(s) are left to Excel)" : "")
              + ". Check them, then Save link.";
    }

    /// <summary>Builds the link; false with a message when something is missing.</summary>
    public bool TryAccept()
    {
        if (WorkbookPath.Length == 0 || _place is null)
        {
            Message = "Choose a workbook and where in it to link.";
            return false;
        }
        if (_busy)
        {
            Message = "Still reading the workbook…";
            return false;
        }
        var key = Mappings.FirstOrDefault(m => m.Header == KeyHeader);
        if (key is null || !key.IsLinked || ExcelSources.IsSpecial(key.ColumnId))
        {
            Message = "Pick the column rows are matched on (e.g. Sheet Number), linked to the matching Nexus column.";
            return false;
        }
        if (Mappings.FirstOrDefault(m => m.IsFixed && m.FixedText.Length == 0) is { } noText)
        {
            Message = $"Type the fixed text for '{noText.Header}'.";
            return false;
        }
        Result = new ExcelLink
        {
            WorkbookPath = WorkbookPath,
            Worksheet = _place.Worksheet,
            Table = _place.Table?.Name,
            HeaderRow = _headerRow,
            Mode = FillMode ? ExcelLinkMode.Fill : ExcelLinkMode.Match,
            AutoUpdate = AutoUpdate,
            Watch = Watch,
            KeyColumnId = key.ColumnId,
            Columns = Mappings.Select(m => new ExcelColumnMap
            {
                Header = m.Header,
                ColumnId = m.ColumnId,
                Direction = m.IsLinked ? m.Direction : ExcelDirection.None,
                FixedText = m.IsFixed ? m.FixedText : null,
                IsFormula = m.IsFormula,
            }).ToList(),
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
        ExcelDiffKind.Different => Diff.Direction switch
        {
            ExcelDirection.ToModel => new[] { DiffAction.Skip, DiffAction.UseExcel },
            ExcelDirection.ToExcel => new[] { DiffAction.Skip, DiffAction.UseModel },
            _ => new[] { DiffAction.Skip, DiffAction.UseExcel, DiffAction.UseModel },
        },
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
                .Where(c => ExcelSources.WritesExcel(c) && ExcelSources.ValueFor(c, r.Diff.Row!) is not null)
                .ToDictionary(c => c.Header, c => ExcelSources.ValueFor(c, r.Diff.Row!)!, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (updates.Count + newRows.Count > 0)
        {
            if (!Dialogs.Confirm($"Update {Path.GetFileName(_link.WorkbookPath)}?",
                    $"Write {updates.Count} cell(s){(newRows.Count > 0 ? $" and add {newRows.Count} row(s)" : "")}. Only those cells change. " +
                    "If the workbook is open in Excel the values go straight into it (save it there); otherwise a backup copy is saved first.",
                    "Write to Excel", kind: NoticeKind.Info))
                return false;
            try
            {
                string where = _main.WriteExcelCells(_link, updates, newRows);
                written = updates.Count;
                added = newRows.Count;
                HubLog.Info($"Updated {_link.Title}: {written} cell(s), {added} new row(s), {where}");
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
