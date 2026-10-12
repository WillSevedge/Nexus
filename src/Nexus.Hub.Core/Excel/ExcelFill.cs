namespace Nexus.Hub.Core.Excel;

/// <summary>The rows Nexus sends to Excel: every row with a key, by program then key (A-2 before A-10).</summary>
public static class ExcelFill
{
    public static List<IReadOnlyDictionary<string, string>> Rows(ResultTable table, ExcelLink link)
    {
        var written = link.Columns.Where(ExcelSources.WritesExcel).ToList();
        var key = link.Columns.FirstOrDefault(c => c.ColumnId == link.KeyColumnId);
        return table.Rows
            .Where(r => r.Values.TryGetValue(link.KeyColumnId, out var k) && !string.IsNullOrWhiteSpace(k.Value))
            .OrderBy(r => ExcelSources.ProgramOf(r), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Values[link.KeyColumnId].Value, NaturalComparer.Instance)
            .ThenBy(r => r.Document, StringComparer.OrdinalIgnoreCase)
            .Select(r => (IReadOnlyDictionary<string, string>)written
                .ToDictionary(c => c.Header, c => ExcelSources.ValueFor(c, r) ?? "", StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Match mode, Nexus → Excel: the cells of matched rows that differ in Nexus → Excel columns, and the rows
    /// only in the model (added below the list).
    /// </summary>
    public static (List<ExcelCellUpdate> Updates, List<IReadOnlyDictionary<string, string>> NewRows) MatchUpdates(
        ResultTable table, ExcelData excel, ExcelLink link)
    {
        var updates = new List<ExcelCellUpdate>();
        var newRows = new List<IReadOnlyDictionary<string, string>>();
        var byKey = new Dictionary<string, ExcelRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in excel.Rows) byKey.TryAdd(ExcelCompare.NormalizeKey(r.Key), r);
        var written = link.Columns.Where(c => ExcelSources.WritesExcel(c) && excel.HeaderColumns.ContainsKey(c.Header) && !excel.FormulaHeaders.Contains(c.Header)).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            if (!row.Values.TryGetValue(link.KeyColumnId, out var k) || string.IsNullOrWhiteSpace(k.Value)) continue;
            string key = ExcelCompare.NormalizeKey(k.Value);
            if (!seen.Add(key)) continue;
            if (!byKey.TryGetValue(key, out var xr))
            {
                newRows.Add(written.ToDictionary(c => c.Header, c => ExcelSources.ValueFor(c, row) ?? "", StringComparer.OrdinalIgnoreCase));
                continue;
            }
            foreach (var c in written)
            {
                if (c.ColumnId == link.KeyColumnId) continue;
                string? want = ExcelSources.ValueFor(c, row);
                if (want is null || !xr.Cells.TryGetValue(c.Header, out var cell) || cell.HasFormula) continue;
                if (!ExcelCompare.SameValue(want, cell.Text)) updates.Add(new ExcelCellUpdate(cell.Row, cell.Column, want));
            }
        }
        return (updates, newRows);
    }
}
