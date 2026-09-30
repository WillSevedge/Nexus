using System.IO;
using System.Reflection;
using Microsoft.Win32;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub;

/// <summary>
/// Makes the installed hub a normal Windows program for the current user: a Start Menu
/// shortcut and an entry in Settings › Apps (with Uninstall). No administrator rights needed.
/// </summary>
internal static class ShellIntegration
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nexus";

    private static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Nexus.lnk");

    public static void Register()
    {
        string exe = NexusPaths.HubExe;
        try
        {
            CreateShortcut(StartMenuShortcut, exe, "Nexus hub: view and edit Revit, AutoCAD and Civil 3D data");
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not create the Start Menu shortcut.", ex);
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "Nexus");
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "Nexus");
            key.SetValue("InstallLocation", NexusPaths.HubInstallDir);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(DirectorySize(NexusPaths.HubInstallDir) / 1024), RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not register Nexus in Settings > Apps.", ex);
        }
    }

    public static void Unregister()
    {
        try { File.Delete(StartMenuShortcut); } catch { /* ignored */ }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { /* ignored */ }
    }

    private static string Version =>
        typeof(ShellIntegration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";

    private static void CreateShortcut(string path, string target, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.IconLocation = target + ",0";
        link.Description = description;
        link.Save();
    }

    private static long DirectorySize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }
}
