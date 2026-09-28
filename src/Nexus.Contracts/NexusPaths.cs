namespace Nexus.Contracts;

/// <summary>
/// Well-known folders. Everything lives under %LOCALAPPDATA%\Nexus unless the
/// NEXUS_HOME environment variable overrides it (used by tests).
/// </summary>
public static class NexusPaths
{
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("NEXUS_HOME");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus");
        }
    }

    public static string AgentsDir => Path.Combine(Root, "agents");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string ExportsDir => Path.Combine(Root, "exports");

    public static string RegistrationFile(int processId) => Path.Combine(AgentsDir, $"{processId}.json");
}
