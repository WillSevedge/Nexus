using AecHub.Contracts;

namespace AecHub.Hub.Core;

/// <summary>A discovered agent plus its live connection and last-known state.</summary>
public sealed class AgentConnection : IAsyncDisposable
{
    public AgentConnection(AgentRegistration registration) => Registration = registration;

    public AgentRegistration Registration { get; private set; }
    public HostInfo Host => Registration.Host;
    public AgentClient? Client { get; private set; }
    public List<DocumentInfo> Documents { get; private set; } = new();
    public List<ReaderDescriptor> Readers { get; private set; } = new();
    public string? LastError { get; private set; }

    /// <summary>(Re)connects if needed and refreshes host info, documents and readers.</summary>
    public async Task RefreshAsync(AgentRegistration latest, CancellationToken ct = default)
    {
        Registration = latest;
        LastError = null;
        try
        {
            if (Client is null || !Client.IsConnected)
            {
                if (Client is not null) await Client.DisposeAsync().ConfigureAwait(false);
                Client = await AgentClient.ConnectAsync(latest.PipeName, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }

            var hello = await Client.HelloAsync(ct).ConfigureAwait(false);
            Registration.Host = hello.Host;
            Readers = await Client.ListReadersAsync(ct).ConfigureAwait(false);
            Documents = await Client.ListDocumentsAsync(ct).ConfigureAwait(false);
        }
        catch (AgentRequestException ex)
        {
            LastError = ex.Error.ToString();
            HubLog.Warn($"{Host.DisplayName}: {LastError}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            HubLog.Warn($"{Host.DisplayName}: could not connect.", ex);
            if (Client is not null)
            {
                await Client.DisposeAsync().ConfigureAwait(false);
                Client = null;
            }
        }
    }

    public async Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct = default)
    {
        if (Client is null || !Client.IsConnected)
        {
            if (Client is not null) await Client.DisposeAsync().ConfigureAwait(false);
            Client = await AgentClient.ConnectAsync(Registration.PipeName, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        return await Client.ReadAsync(request, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Client is not null) await Client.DisposeAsync().ConfigureAwait(false);
        Client = null;
    }
}
