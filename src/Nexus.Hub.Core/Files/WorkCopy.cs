using Nexus.Contracts;

namespace Nexus.Hub.Core.Files;

/// <summary>
/// A private copy of a file on disk that a program opens instead of the original, so the original is
/// never locked, changed or synced back (Desktop Connector), whatever the program does. Deleted when disposed.
/// </summary>
public sealed class WorkCopy : IDisposable
{
    private WorkCopy(string folder, string path)
    {
        Folder = folder;
        Path = path;
    }

    /// <summary>A folder of its own, for the copy and anything made next to it (job files, results).</summary>
    public string Folder { get; }
    public string Path { get; }

    public static async Task<WorkCopy> CreateAsync(string source, CancellationToken ct)
    {
        string folder = System.IO.Path.Combine(NexusPaths.OfflineWorkDir, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        string target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(source));
        try
        {
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true))
            await using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                await from.CopyToAsync(to, 1 << 20, ct).ConfigureAwait(false);
            // Programs may refuse read-only files; the copy is ours anyway.
            File.SetAttributes(target, FileAttributes.Normal);
            return new WorkCopy(folder, target);
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    public void Dispose() => TryDelete(Folder);

    /// <summary>Removes copies left behind (the hub was closed during a read). Called at start-up.</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(NexusPaths.OfflineWorkDir)) return;
            foreach (var dir in Directory.EnumerateDirectories(NexusPaths.OfflineWorkDir))
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-6)) TryDelete(dir);
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not clean up old working copies.", ex);
        }
    }

    private static void TryDelete(string folder)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                return;
            }
            catch (Exception) when (attempt < 2)
            {
                Thread.Sleep(300); // the program may still be letting go of the file
            }
            catch (Exception ex)
            {
                HubLog.Warn($"Could not delete the working copy {folder}.", ex);
            }
        }
    }
}
