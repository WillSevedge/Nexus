using System.Text.Json;
using AecHub.Contracts;

namespace AecHub.Agent;

/// <summary>Writes/removes %LOCALAPPDATA%\AecHub\agents\{pid}.json so the hub can find us.</summary>
public static class AgentRegistrationFile
{
    public static string Write(AgentRegistration registration)
    {
        Directory.CreateDirectory(AecHubPaths.AgentsDir);
        string path = AecHubPaths.RegistrationFile(registration.Host.ProcessId);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(registration, Json.Indented));
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public static void Delete(int processId)
    {
        try { File.Delete(AecHubPaths.RegistrationFile(processId)); } catch { /* ignored */ }
    }
}
