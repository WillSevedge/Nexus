using Nexus.Contracts;

namespace Nexus.Agent;

/// <summary>
/// Implemented once per host. Runs work on the host's main (UI/API) thread.
/// The pipe server never touches the host API directly: everything goes through here.
/// </summary>
public interface IHostDispatcher
{
    /// <summary>
    /// Queues <paramref name="work"/> to run on the host thread and completes with its result.
    /// If the host does not start the work within <paramref name="startTimeout"/>
    /// (e.g. a modal dialog is open) the task fails with an AgentException(HostBusy)
    /// and the work is never run.
    /// </summary>
    Task<T> InvokeAsync<T>(Func<T> work, TimeSpan startTimeout, CancellationToken ct);
}

/// <summary>Lists and resolves open documents. Called on the host thread only.</summary>
public interface IDocumentProvider<TDoc> where TDoc : class
{
    IReadOnlyList<DocumentInfo> ListDocuments();

    /// <summary>Resolve an id from <see cref="ListDocuments"/> or "active". Null if not open.</summary>
    TDoc? Find(string documentId);

    /// <summary>Returns id and title for a resolved document.</summary>
    DocumentInfo Describe(TDoc document);
}

/// <summary>
/// A pluggable reader for one data domain in one host. Add a new domain by
/// implementing this and registering it: the hub, IPC and export code do not change.
/// </summary>
public interface IHostDataReader<in TDoc> where TDoc : class
{
    ReaderDescriptor Descriptor { get; }

    /// <summary>Always called on the host thread.</summary>
    void Read(TDoc document, ReadContext context);
}
