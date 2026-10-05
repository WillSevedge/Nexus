using System.Text;
using System.Text.Json;
using Nexus.Contracts;

namespace Nexus.Hub.Core.Files;

/// <summary>
/// Results read from files on disk, kept in %LOCALAPPDATA%\Nexus\offline\cache so a file is only opened
/// again when it changed (size or time) or other options are asked for.
/// </summary>
public static class ReadCache
{
    private const int Keep = 400;

    private static string Key(DiskFile file, ReadRequest request)
    {
        var sb = new StringBuilder(file.Path.ToUpperInvariant()).Append('|').Append(file.Signature).Append('|').Append(request.ReaderId);
        foreach (var (k, v) in request.Options.OrderBy(o => o.Key, StringComparer.Ordinal)) sb.Append('|').Append(k).Append('=').Append(v);
        return Hash.Short(sb.ToString());
    }

    private static string PathFor(string key) => Path.Combine(NexusPaths.OfflineCacheDir, key + ".json");

    public static ReadResult? Get(DiskFile file, ReadRequest request)
    {
        string path = PathFor(Key(file, request));
        try
        {
            if (!File.Exists(path)) return null;
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // recently used
            return JsonSerializer.Deserialize<ReadResult>(File.ReadAllText(path), Json.Options);
        }
        catch (Exception ex)
        {
            HubLog.Warn($"Cached result for {file.Name} could not be read; reading the file again.", ex);
            return null;
        }
    }

    public static void Put(DiskFile file, ReadRequest request, ReadResult result)
    {
        try
        {
            Directory.CreateDirectory(NexusPaths.OfflineCacheDir);
            File.WriteAllText(PathFor(Key(file, request)), JsonSerializer.Serialize(result, Json.Options));
            foreach (var old in new DirectoryInfo(NexusPaths.OfflineCacheDir).EnumerateFiles("*.json")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(Keep))
                old.Delete();
        }
        catch (Exception ex)
        {
            HubLog.Warn($"Could not keep the result for {file.Name}.", ex);
        }
    }

    public static void Clear()
    {
        try
        {
            if (Directory.Exists(NexusPaths.OfflineCacheDir)) Directory.Delete(NexusPaths.OfflineCacheDir, true);
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not clear the files-on-disk cache.", ex);
        }
    }
}
