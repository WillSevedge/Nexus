using System.Diagnostics;
using System.Globalization;

namespace Nexus.Hub.Core.Files;

/// <summary>Revit releases installed on this PC (to start one for reading models that are not open).</summary>
public static class RevitInstalls
{
    /// <summary>Installed releases, newest first: (year, Revit.exe).</summary>
    public static List<(int Year, string Exe)> Find()
    {
        var list = new List<(int, string)>();
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        for (int year = DateTime.Now.Year + 2; year >= 2024; year--)
        {
            string exe = Path.Combine(programs, "Autodesk", "Revit " + year.ToString(CultureInfo.InvariantCulture), "Revit.exe");
            if (File.Exists(exe)) list.Add((year, exe));
        }
        return list;
    }

    /// <summary>The release to start for models saved in <paramref name="newestModel"/>: the oldest that can open them.</summary>
    public static (int Year, string Exe)? For(int? newestModel)
    {
        var installed = Find();
        return installed.Where(i => newestModel is null || i.Year >= newestModel).OrderBy(i => i.Year).Cast<(int, string)?>().FirstOrDefault()
               ?? installed.Cast<(int, string)?>().FirstOrDefault();
    }

    /// <summary>Starts Revit (it shows its window; no project is opened).</summary>
    public static void Start(string exe) =>
        Process.Start(new ProcessStartInfo(exe, "/nosplash") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
}
