using System.IO;
using System.Text.Json;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub;

/// <summary>Per-user hub preferences: last dataset, visible columns per dataset, active Excel link.</summary>
internal sealed class UiSettings
{
    private static string FilePath => Path.Combine(NexusPaths.Root, "hub-ui.json");

    public string? LastDataset { get; set; }
    public Dictionary<string, List<string>> VisibleColumns { get; set; } = new();
    public string? ExcelWorkbook { get; set; }

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Json.Options) ?? new UiSettings();
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not read the hub settings; using defaults.", ex);
        }
        return new UiSettings();
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
