using System.Text.Json;
using Nexus.Contracts;

namespace Nexus.Hub.Core.Excel;

/// <summary>
/// How a workbook lines up with Nexus data: which worksheet, which row holds the headers,
/// which header is the key (normally the sheet number), and which Excel column holds which
/// Nexus column. Saved per workbook in %LOCALAPPDATA%\Nexus\excel-links.json.
/// </summary>
public sealed class ExcelLink
{
    public string WorkbookPath { get; set; } = "";
    public string Worksheet { get; set; } = "";
    /// <summary>1-based row number of the header row.</summary>
    public int HeaderRow { get; set; } = 1;
    /// <summary>Nexus column the rows are matched on.</summary>
    public string KeyColumnId { get; set; } = SheetFieldMap.NumberColumnId;
    public List<ExcelColumnMap> Columns { get; set; } = new();
    public DateTime SavedUtc { get; set; }

    public string? KeyHeader => Columns.FirstOrDefault(c => c.ColumnId == KeyColumnId)?.Header;
    public string Title => $"{Path.GetFileName(WorkbookPath)} › {Worksheet}";

    private static string StoreFile => Path.Combine(NexusPaths.Root, "excel-links.json");

    /// <summary>Saved links, most recent first.</summary>
    public static List<ExcelLink> LoadAll()
    {
        try
        {
            if (File.Exists(StoreFile))
                return (JsonSerializer.Deserialize<List<ExcelLink>>(File.ReadAllText(StoreFile), Json.Options) ?? new())
                    .OrderByDescending(l => l.SavedUtc).ToList();
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not read the saved Excel links.", ex);
        }
        return new List<ExcelLink>();
    }

    public static ExcelLink? LoadFor(string workbookPath) =>
        LoadAll().FirstOrDefault(l => string.Equals(l.WorkbookPath, workbookPath, StringComparison.OrdinalIgnoreCase));

    public void Save()
    {
        SavedUtc = DateTime.UtcNow;
        var all = LoadAll().Where(l => !string.Equals(l.WorkbookPath, WorkbookPath, StringComparison.OrdinalIgnoreCase)).ToList();
        all.Insert(0, this);
        Directory.CreateDirectory(NexusPaths.Root);
        File.WriteAllText(StoreFile, JsonSerializer.Serialize(all.Take(50).ToList(), Json.Options));
    }
}

/// <summary>One Excel column ↔ one Nexus column.</summary>
public sealed class ExcelColumnMap
{
    public string Header { get; set; } = "";
    /// <summary>A Nexus column id ("Sheet › Title", "Title Block › DWGNO", ...); empty = not linked.</summary>
    public string ColumnId { get; set; } = "";
}
