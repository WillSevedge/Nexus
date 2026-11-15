using System.Diagnostics;
using System.IO.Pipes;

namespace Nexus.Agent;

/// <summary>
/// The few calls that differ between .NET Framework 4.8 (2024 programs) and .NET 8/10 (2025 and later).
/// </summary>
public static class Compat
{
    private static readonly int CurrentProcessId = GetProcessId();

    private static int GetProcessId()
    {
#if NETFRAMEWORK
        using var p = Process.GetCurrentProcess();
        return p.Id;
#else
        return Environment.ProcessId;
#endif
    }

    public static int ProcessId => CurrentProcessId;

    /// <summary>
    /// .NET Framework (2024 programs): the libraries Nexus ships next to its add-in (System.Text.Json and
    /// its helpers) may be a different version from one the program already loaded. When the runtime
    /// cannot bind one, load ours from the add-in folder. No effect on .NET 8/10.
    /// </summary>
    public static void ResolveDependenciesFrom(string addinFolder)
    {
#if NETFRAMEWORK
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            try
            {
                string name = new System.Reflection.AssemblyName(args.Name).Name ?? "";
                string path = Path.Combine(addinFolder, name + ".dll");
                return File.Exists(path) ? System.Reflection.Assembly.LoadFrom(path) : null;
            }
            catch
            {
                return null;
            }
        };
#endif
    }

    /// <summary>Enum.GetValues&lt;T&gt;() on every runtime.</summary>
    public static T[] EnumValues<T>() where T : struct, Enum => (T[])Enum.GetValues(typeof(T));

    /// <summary>SHA-1 of UTF-8 text as upper-case hex, first <paramref name="bytes"/> bytes (for stable ids).</summary>
    public static string Sha1Hex(string text, int bytes = 20)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        var sb = new System.Text.StringBuilder(bytes * 2);
        for (int i = 0; i < Math.Min(bytes, hash.Length); i++) sb.Append(hash[i].ToString("X2"));
        return sb.ToString();
    }

    /// <summary>Moves a file over an existing one.</summary>
    public static void MoveOverwrite(string from, string to)
    {
#if NETFRAMEWORK
        if (File.Exists(to)) File.Replace(from, to, null);
        else File.Move(from, to);
#else
        File.Move(from, to, overwrite: true);
#endif
    }

    /// <summary>
    /// A pipe server only the current Windows user can connect to (PipeOptions.CurrentUserOnly on .NET 8+;
    /// an access rule for the current user alone on .NET Framework).
    /// </summary>
    public static NamedPipeServerStream CreateUserOnlyServer(string name)
    {
#if !NETFRAMEWORK
        // Not Windows (the tests run anywhere): the ready-made option.
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#endif
        // Open to this Windows user and nobody else, named by the user's own id. (The ready-made "current user
        // only" option names the token's owner instead, which for a program run as administrator is the
        // Administrators group: the hub, running normally as the same user, would then be refused.)
        var security = new PipeSecurity();
        using var user = System.Security.Principal.WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(user.User!, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
#if NETFRAMEWORK
        return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#else
#pragma warning disable CA1416 // Windows only: the add-ins run in Revit and AutoCAD
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#pragma warning restore CA1416
#endif
    }

    /// <summary>A pipe client that (on .NET 8+) also checks the server belongs to the current user.</summary>
    public static NamedPipeClientStream CreateUserOnlyClient(string name, PipeDirection direction)
    {
#if NETFRAMEWORK
        return new NamedPipeClientStream(".", name, direction, PipeOptions.None);
#else
        return new NamedPipeClientStream(".", name, direction, PipeOptions.CurrentUserOnly);
#endif
    }
}
