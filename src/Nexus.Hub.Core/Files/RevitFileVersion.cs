using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;

namespace Nexus.Hub.Core.Files;

/// <summary>
/// The Revit release that saved a model, read from the file itself (its BasicFileInfo stream) without Revit.
/// Used to pick a running Revit that can open the file.
/// </summary>
public static class RevitFileVersion
{
    private static readonly Regex Format = new(@"Format:\s*(\d{4})", RegexOptions.Compiled);
    private static readonly Regex Build = new(@"Autodesk Revit\s+(?:Architecture\s+|MEP\s+|Structure\s+)?(\d{4})", RegexOptions.Compiled);

    public static (int? Year, bool Workshared) Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var root = RootStorage.Open(stream, StorageModeFlags.LeaveOpen);
            using var info = root.OpenStream("BasicFileInfo");
            using var data = new MemoryStream();
            info.CopyTo(data);
            return Parse(data.ToArray());
        }
        catch (Exception ex)
        {
            HubLog.Warn($"Could not read the Revit version of {Path.GetFileName(path)}.", ex);
            return (null, false);
        }
    }

    /// <summary>The stream holds UTF-16 text ("Format: 2025", "Worksharing: Central"...) after a small binary header.</summary>
    public static (int? Year, bool Workshared) Parse(byte[] data)
    {
        string text = Encoding.Unicode.GetString(data) + "\n" + Encoding.UTF8.GetString(data);
        int? year = null;
        var m = Format.Match(text);
        if (!m.Success) m = Build.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int y)) year = y;
        bool workshared = Regex.IsMatch(text, @"Worksharing:\s*(Central|Local)", RegexOptions.IgnoreCase);
        return (year, workshared);
    }
}
