using System.Text;
using System.Text.RegularExpressions;
using Nexus.Contracts;

namespace Nexus.Hub.Core.Files;

/// <summary>
/// A Revit model or drawing on disk (a local folder, a network share, or Autodesk Docs / Forma through
/// Desktop Connector) that Nexus reads without it being open in a program. Always read-only.
/// </summary>
public sealed class DiskFile
{
    private static readonly Regex RevitBackup = new(@"\.\d{4}\.rvt$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private DiskFile(string path) => Path = System.IO.Path.GetFullPath(path);

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    /// <summary><see cref="HostKinds.Revit"/> or <see cref="HostKinds.AutoCAD"/>.</summary>
    public string HostKind { get; private init; } = "";
    public bool IsRevit => HostKind == HostKinds.Revit;
    public long Length { get; private set; }
    public DateTime LastWriteUtc { get; private set; }
    /// <summary>Revit: the release that saved the file (2024...); null when not known yet.</summary>
    public int? RevitYear { get; private set; }
    public bool IsWorkshared { get; private set; }
    /// <summary>"Revit 2025 model", "AutoCAD 2018 drawing format"...</summary>
    public string Format { get; private set; } = "";
    /// <summary>Stable id for results read from this file.</summary>
    public string DocumentId => "disk-" + Hash.Short(Path.ToUpperInvariant());

    /// <summary>Same file contents as when it was read (size and time).</summary>
    public string Signature => $"{Length}|{LastWriteUtc.Ticks}";

    public static bool IsSupported(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        if (ext.Equals(".dwg", StringComparison.OrdinalIgnoreCase)) return true;
        return ext.Equals(".rvt", StringComparison.OrdinalIgnoreCase) && !RevitBackup.IsMatch(path); // not Model.0001.rvt backups
    }

    /// <summary>Supported files in a folder and its subfolders (backup folders skipped).</summary>
    public static List<string> FindIn(string folder, int max = 2000)
    {
        var found = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var f in Directory.EnumerateFiles(folder, "*.*", options))
        {
            if (f.Contains("_backup", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsSupported(f)) continue;
            found.Add(f);
            if (found.Count >= max) break;
        }
        return found;
    }

    /// <summary>Kind from the extension; size, time and version are filled by <see cref="Inspect"/>.</summary>
    public static DiskFile Create(string path) => new(path)
    {
        HostKind = System.IO.Path.GetExtension(path).Equals(".rvt", StringComparison.OrdinalIgnoreCase) ? HostKinds.Revit : HostKinds.AutoCAD,
    };

    /// <summary>
    /// Reads size, time and version. For Desktop Connector files this can download the file
    /// (Revit keeps its version inside the file), so call it off the UI thread.
    /// </summary>
    public void Inspect()
    {
        var info = new FileInfo(Path);
        if (!info.Exists) throw new FileNotFoundException("The file is not there any more.", Path);
        Length = info.Length;
        LastWriteUtc = info.LastWriteTimeUtc;
        if (IsRevit)
        {
            var (year, workshared) = RevitFileVersion.Read(Path);
            RevitYear = year;
            IsWorkshared = workshared;
            Format = year is null ? "Revit model" : $"Revit {year} model" + (workshared ? " · workshared" : "");
        }
        else
        {
            Format = DwgVersion(Path);
        }
    }

    /// <summary>True when the file changed since <see cref="Inspect"/>.</summary>
    public bool Changed()
    {
        var info = new FileInfo(Path);
        return !info.Exists || info.Length != Length || info.LastWriteTimeUtc != LastWriteUtc;
    }

    private static string DwgVersion(string path)
    {
        try
        {
            var head = new byte[6];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Read(head, 0, 6) < 6) return "Drawing";
            string code = Encoding.ASCII.GetString(head);
            return code switch
            {
                "AC1032" => "AutoCAD 2018 drawing format",
                "AC1027" => "AutoCAD 2013 drawing format",
                "AC1024" => "AutoCAD 2010 drawing format",
                "AC1021" => "AutoCAD 2007 drawing format",
                "AC1018" => "AutoCAD 2004 drawing format",
                "AC1015" => "AutoCAD 2000 drawing format",
                _ when code.StartsWith("AC10", StringComparison.Ordinal) && string.CompareOrdinal(code, "AC1032") > 0 => "Newer AutoCAD drawing format",
                _ when code.StartsWith("AC", StringComparison.Ordinal) => "Old AutoCAD drawing format",
                _ => "Not a drawing file",
            };
        }
        catch (Exception ex)
        {
            return "Drawing (" + ex.Message + ")";
        }
    }
}

internal static class Hash
{
    public static string Short(string text)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}
