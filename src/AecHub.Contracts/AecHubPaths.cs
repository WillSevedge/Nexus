namespace AecHub.Contracts;

/// <summary>
/// Well-known folders. Everything lives under %LOCALAPPDATA%\AecHub unless the
/// AECHUB_HOME environment variable overrides it (used by tests).
/// </summary>
public static class AecHubPaths
{
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("AECHUB_HOME");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AecHub");
        }
    }

    public static string AgentsDir => Path.Combine(Root, "agents");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string ExportsDir => Path.Combine(Root, "exports");

    public static string RegistrationFile(int processId) => Path.Combine(AgentsDir, $"{processId}.json");
}
