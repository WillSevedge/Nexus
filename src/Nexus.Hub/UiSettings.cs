using System.IO;
using System.Text.Json;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub;

internal sealed class DiskFileSetting
{
    public string Path { get; set; } = "";
    public bool Checked { get; set; } = true;
}

/// <summary>Per-user hub preferences: last dataset, visible columns per dataset, active Excel link.</summary>
internal sealed class UiSettings
{
    private static string FilePath => Path.Combine(NexusPaths.Root, "hub-ui.json");

    public string? LastDataset { get; set; }
    public Dictionary<string, List<string>> VisibleColumns { get; set; } = new();
    public string? ExcelWorkbook { get; set; }
    /// <summary>Files on disk added to Files (read without opening them), with their tick.</summary>
    public List<DiskFileSetting> DiskFiles { get; set; } = new();
    /// <summary>Print &amp; PDF: last output folder and file name pattern.</summary>
    public string? PdfFolder { get; set; }
    public string? PdfNamePattern { get; set; }
    public string? LastDiskFolder { get; set; }
    /// <summary>Files on disk: let a running Revit open a copy of a Revit model to read it. Off unless turned on.</summary>
    public bool AllowRevitToOpenModels { get; set; }

    /// <summary>
    /// Version of the default Sheets columns. When the defaults change, the saved column choice for
    /// Sheets is dropped once so the new defaults show.
    /// </summary>
    public int SheetColumnsVersion { get; set; }
    private const int CurrentSheetColumnsVersion = 2;

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Json.Options) ?? new UiSettings();
                if (settings.SheetColumnsVersion < CurrentSheetColumnsVersion)
                {
                    // v2: Revit sheets open on the Properties palette parameters, not a column per revision.
                    settings.VisibleColumns.Remove(ViewModels.MainViewModel.SheetsDatasetId);
                    settings.SheetColumnsVersion = CurrentSheetColumnsVersion;
                }
                return settings;
            }
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not read the hub settings; using defaults.", ex);
        }
        return new UiSettings { SheetColumnsVersion = CurrentSheetColumnsVersion };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(NexusPaths.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Options));
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not save the hub settings.", ex);
        }
    }
}
