using System.Globalization;
using ClosedXML.Excel;

namespace Nexus.Hub.Core.Excel;

/// <summary>A worksheet's header row and headers, for setting up a link.</summary>
public sealed class ExcelSheetInfo
{
    public string Name { get; init; } = "";
    public int HeaderRow { get; init; }
    public List<string> Headers { get; init; } = new();
}

public sealed class ExcelCell
{
    public int Row { get; init; }
    public int Column { get; init; }
    /// <summary>The text Excel shows (number formats applied).</summary>
    public string Text { get; init; } = "";
    public bool HasFormula { get; init; }
}

public sealed class ExcelRow
{
    public int Row { get; init; }
    public string Key { get; init; } = "";
    /// <summary>Header → cell, for the linked columns.</summary>
    public Dictionary<string, ExcelCell> Cells { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExcelData
{
    public List<ExcelRow> Rows { get; } = new();
    /// <summary>Header → 1-based column number.</summary>
    public Dictionary<string, int> HeaderColumns { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last row that belongs to the table (new rows go below it).</summary>
    public int LastRow { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>Reads and writes linked workbooks (.xlsx, .xlsm). Reading works while the file is open in Excel.</summary>
public static class ExcelWorkbook
{
    /// <summary>Stop reading data after this many empty rows in a row.</summary>
    private const int MaxBlankRows = 20;

    public static List<ExcelSheetInfo> Inspect(string path)
    {
        using var wb = OpenForRead(path);
        var sheets = new List<ExcelSheetInfo>();
        foreach (var ws in wb.Worksheets)
        {
            int headerRow = GuessHeaderRow(ws);
            sheets.Add(new ExcelSheetInfo
            {
                Name = ws.Name,
                HeaderRow = headerRow,
                Headers = headerRow == 0 ? new() : Headers(ws, headerRow).Select(h => h.Header).ToList(),
            });
        }
        return sheets;
    }

    public static List<string> HeadersAt(string path, string worksheet, int headerRow)
    {
        using var wb = OpenForRead(path);
        return Headers(wb.Worksheet(worksheet), headerRow).Select(h => h.Header).ToList();
    }

    public static ExcelData Read(ExcelLink link)
    {
        using var wb = OpenForRead(link.WorkbookPath);
        if (!wb.TryGetWorksheet(link.Worksheet, out var ws))
            throw new InvalidOperationException($"The workbook has no worksheet '{link.Worksheet}'.");

        var data = new ExcelData();
        foreach (var (header, column) in Headers(ws, link.HeaderRow))
            data.HeaderColumns.TryAdd(header, column);

        var linked = link.Columns.Where(c => c.ColumnId.Length > 0 && data.HeaderColumns.ContainsKey(c.Header)).ToList();
        foreach (var missing in link.Columns.Where(c => c.ColumnId.Length > 0 && !data.HeaderColumns.ContainsKey(c.Header)))
            data.Warnings.Add($"Column '{missing.Header}' is no longer in row {link.HeaderRow} of '{link.Worksheet}'.");

        string? keyHeader = link.KeyHeader;
        if (keyHeader is null || !data.HeaderColumns.TryGetValue(keyHeader, out int keyColumn))
            throw new InvalidOperationException("The link has no key column (e.g. the sheet number). Set it up again.");

        int last = ws.LastRowUsed()?.RowNumber() ?? link.HeaderRow;
        int blanks = 0;
        data.LastRow = link.HeaderRow;
        for (int r = link.HeaderRow + 1; r <= last; r++)
        {
            string key = Text(ws.Cell(r, keyColumn)).Trim();
            bool empty = key.Length == 0 && linked.All(c => Text(ws.Cell(r, data.HeaderColumns[c.Header])).Trim().Length == 0);
            if (empty)
            {
                if (++blanks >= MaxBlankRows) break;
                continue;
            }
            blanks = 0;
            data.LastRow = r;
            if (key.Length == 0) continue; // a row without a key (subtotal, note...) is not compared

            var row = new ExcelRow { Row = r, Key = key };
            foreach (var c in linked)
            {
                int col = data.HeaderColumns[c.Header];
                var cell = ws.Cell(r, col);
                row.Cells[c.Header] = new ExcelCell { Row = r, Column = col, Text = Text(cell), HasFormula = cell.HasFormula };
            }
            data.Rows.Add(row);
        }
        return data;
    }

    /// <summary>
    /// Writes values into the workbook, after copying it to the backups folder, and returns the backup path.
    /// Only the given cells change: the edit is made cell by cell on a copy, the copy is compared with the
    /// original (macros, form controls, comments, validation, conditional formatting, tables, custom XML,
    /// every other cell), and the original is replaced only if nothing else changed. New rows go into the
    /// empty rows below the last row of the list; rows are never inserted, so formulas and pre-built rows
    /// further down stay where they are. Fails with a clear message if the file is open in Excel.
    /// </summary>
    public static string Write(ExcelLink link, IReadOnlyList<ExcelCellUpdate> updates, IReadOnlyList<IReadOnlyDictionary<string, string>> newRows)
    {
        var values = updates.Select(u => (u.Row, u.Column, u.Value)).ToList();
        if (newRows.Count > 0) values.AddRange(PlaceNewRows(link, newRows));
        if (values.Count == 0) return "";
        return WriteVerified(link.WorkbookPath, link.Worksheet, values);
    }

    /// <summary>The cells for new rows: the first empty rows below the list (no formula, no value in the written columns).</summary>
    private static List<(int Row, int Column, string Value)> PlaceNewRows(ExcelLink link, IReadOnlyList<IReadOnlyDictionary<string, string>> newRows)
    {
        var data = Read(link);
        using var wb = OpenForRead(link.WorkbookPath);
        var ws = wb.Worksheet(link.Worksheet);
        var cells = new List<(int, int, string)>();
        int row = data.LastRow;
        foreach (var values in newRows)
        {
            var columns = values.Where(v => data.HeaderColumns.ContainsKey(v.Key)).Select(v => (Column: data.HeaderColumns[v.Key], v.Value)).ToList();
            int limit = row + 200;
            do
            {
                if (++row > limit)
                    throw new InvalidOperationException($"No empty row was found below row {data.LastRow} of '{link.Worksheet}' for the new rows.");
            }
            while (columns.Any(c => ws.Cell(row, c.Column).HasFormula || !ws.Cell(row, c.Column).IsEmpty()));
            foreach (var (column, value) in columns) cells.Add((row, column, value));
        }
        return cells;
    }

    private static string WriteVerified(string path, string worksheet, List<(int Row, int Column, string Value)> values)
    {
        string name = Path.GetFileName(path);
        string closeIt = $"'{name}' is open in Excel (or another program). Save and close it there, then try again.";
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        catch (IOException ex)
        {
            throw new IOException(closeIt, ex);
        }

        string backup = Backup(path);
        string work = Path.Combine(Path.GetDirectoryName(backup)!, $"~writing {Guid.NewGuid():N}{Path.GetExtension(path)}");
        File.Copy(backup, work);
        try
        {
            var before = WorkbookSnapshot.Take(backup);
            var stored = ExcelCellWriter.Write(work, worksheet, values);
            var after = WorkbookSnapshot.Take(work);

            var mayChange = PartsAWriteMayChange(before, worksheet);
            // A first text value creates the shared string table, which adds a workbook relationship.
            if (before.SharedStringsPart is null && before.WorkbookPart is { } wbPart)
                mayChange.Add(wbPart[..(wbPart.LastIndexOf('/') + 1)] + "_rels/" + wbPart[(wbPart.LastIndexOf('/') + 1)..] + ".rels");
            var issues = WorkbookSnapshot.Compare(before, after,
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase) { [worksheet] = stored },
                mayChange);
            if (issues.Count > 0)
            {
                foreach (var issue in issues) HubLog.Warn($"Excel write check failed for {name}: {issue}");
                throw new ExcelWriteVerificationException(name, issues);
            }

            try
            {
                File.Copy(work, path, overwrite: true);
            }
            catch (IOException ex)
            {
                throw new IOException(closeIt, ex);
            }
            return backup;
        }
        finally
        {
            try { File.Delete(work); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Parts a cell-level write to <paramref name="worksheet"/> is allowed to change: that worksheet,
    /// the shared strings and the workbook part (to ask Excel to recalculate on open). Every other part
    /// must come out byte-identical.
    /// </summary>
    public static HashSet<string> PartsAWriteMayChange(WorkbookSnapshot snapshot, string worksheet)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snapshot.Sheets.TryGetValue(worksheet, out var sheet)) parts.Add(sheet.PartName);
        if (snapshot.SharedStringsPart is { } sst) parts.Add(sst);
        else parts.Add("/xl/sharedStrings.xml");
        if (snapshot.WorkbookPart is { } wb) parts.Add(wb);
        return parts;
    }

    /// <summary>A new workbook with one header row and the given rows (Export to Excel).</summary>
    public static void Export(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows, string sheetName = "Nexus")
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(sheetName);
        for (int c = 0; c < headers.Count; c++) ws.Cell(1, c + 1).Value = headers[c];
        int r = 2;
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Count; c++)
                if (!string.IsNullOrEmpty(row[c])) ws.Cell(r, c + 1).Value = row[c];
            r++;
        }
        var header = ws.Range(1, 1, 1, Math.Max(1, headers.Count));
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DDE6F2");
        ws.SheetView.FreezeRows(1);
        if (r > 2) ws.Range(1, 1, r - 1, Math.Max(1, headers.Count)).SetAutoFilter();
        ws.Columns().AdjustToContents(1, Math.Min(r, 200), 8, 60);
        wb.SaveAs(path);
    }

    private static string Backup(string path)
    {
        string dir = Path.Combine(Nexus.Contracts.NexusPaths.Root, "excel-backups");
        Directory.CreateDirectory(dir);
        string backup = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)} {DateTime.Now:yyyy-MM-dd HHmmss}{Path.GetExtension(path)}");
        using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var dst = File.Create(backup))
            src.CopyTo(dst);
        return backup;
    }

    private static XLWorkbook OpenForRead(string path)
    {
        // FileShare.ReadWrite: read even while Excel has the workbook open.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var memory = new MemoryStream();
        using (stream) stream.CopyTo(memory);
        memory.Position = 0;
        return new XLWorkbook(memory);
    }

    private static IEnumerable<(string Header, int Column)> Headers(IXLWorksheet ws, int headerRow)
    {
        var row = ws.Row(headerRow);
        int last = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 1; c <= last; c++)
        {
            string text = Text(ws.Cell(headerRow, c)).Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (text.Length == 0) continue;
            string header = text;
            for (int n = 2; !seen.Add(header); n++) header = $"{text} ({n})";
            yield return (header, c);
        }
    }

    private static int GuessHeaderRow(IXLWorksheet ws)
    {
        int last = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 0, 30);
        int best = 0, bestCount = 0;
        for (int r = 1; r <= last; r++)
        {
            var cells = ws.Row(r).CellsUsed().Where(c => c.DataType == XLDataType.Text && c.GetString().Trim().Length > 0).ToList();
            if (cells.Count > bestCount)
            {
                best = r;
                bestCount = cells.Count;
            }
        }
        return bestCount >= 2 ? best : (last > 0 ? 1 : 0);
    }

    private static string Text(IXLCell cell)
    {
        try { return cell.GetFormattedString(); }
        catch { return cell.Value.ToString(CultureInfo.CurrentCulture); }
    }
}

/// <summary>One cell to change in the workbook.</summary>
public sealed record ExcelCellUpdate(int Row, int Column, string Value);

/// <summary>
/// A workbook write would have changed more than the intended cells. The workbook was not changed.
/// </summary>
public sealed class ExcelWriteVerificationException : InvalidOperationException
{
    public ExcelWriteVerificationException(string workbook, IReadOnlyList<FidelityIssue> issues)
        : base($"Nexus did not save '{workbook}': the check after writing found changes beyond the cells being updated, " +
               $"so the workbook was left as it was.\n\n{string.Join("\n", issues.Take(10).Select(i => "• " + i))}" +
               (issues.Count > 10 ? $"\n• … and {issues.Count - 10} more (see the Nexus log)" : ""))
    {
        Issues = issues;
    }

    public IReadOnlyList<FidelityIssue> Issues { get; }
}
