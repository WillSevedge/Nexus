using System.Collections.Concurrent;
using System.IO.Pipes;
using Nexus.Contracts;

namespace Nexus.Hub.Core;

public sealed class AgentRequestException : Exception
{
    public AgentRequestException(ErrorInfo error) : base(error.Message) => Error = error;
    public ErrorInfo Error { get; }
    public string Code => Error.Code;
}

/// <summary>
/// One connection to one agent. Requests may overlap; responses are matched by id.
/// </summary>
public sealed class AgentClient : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(15);

    private readonly NamedPipeClientStream _pipe;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Envelope>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;

    private AgentClient(string pipeName)
    {
        PipeName = pipeName;
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public string PipeName { get; }
    public bool IsConnected => _pipe.IsConnected && _readLoop is { IsCompleted: false };

    public static async Task<AgentClient> ConnectAsync(string pipeName, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var client = new AgentClient(pipeName);
        try
        {
            await client._pipe.ConnectAsync((int)(timeout ?? TimeSpan.FromSeconds(3)).TotalMilliseconds, ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        client._readLoop = Task.Run(client.ReadLoopAsync);
        return client;
    }

    public Task<HelloResponse> HelloAsync(CancellationToken ct = default) =>
        RequestAsync<HelloResponse>(MessageTypes.Hello, Empty.Instance, DefaultTimeout, ct);

    public Task PingAsync(CancellationToken ct = default) =>
        RequestAsync<Empty>(MessageTypes.Ping, Empty.Instance, TimeSpan.FromSeconds(10), ct);

    public async Task<List<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default) =>
        (await RequestAsync<ListDocumentsResponse>(MessageTypes.ListDocuments, Empty.Instance, DefaultTimeout, ct).ConfigureAwait(false)).Documents;

    public async Task<List<ReaderDescriptor>> ListReadersAsync(CancellationToken ct = default) =>
        (await RequestAsync<ListReadersResponse>(MessageTypes.ListReaders, Empty.Instance, DefaultTimeout, ct).ConfigureAwait(false)).Readers;

    public Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct = default) =>
        RequestAsync<ReadResult>(MessageTypes.Read, request, ReadTimeout, ct);

    public Task<WriteResult> WriteAsync(WriteRequest request, CancellationToken ct = default) =>
        RequestAsync<WriteResult>(MessageTypes.Write, request, ReadTimeout, ct);

    public Task<SelectResult> SelectAsync(SelectRequest request, CancellationToken ct = default) =>
        RequestAsync<SelectResult>(MessageTypes.Select, request, DefaultTimeout, ct);

    public async Task<TResponse> RequestAsync<TResponse>(string type, object payload, TimeSpan timeout, CancellationToken ct,
        int? protocolVersion = null)
    {
        var request = Envelope.Request(type, payload);
        if (protocolVersion is not null) request.V = protocolVersion.Value;
        var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = tcs;
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try { await Frames.WriteAsync(_pipe, request, ct).ConfigureAwait(false); }
            finally { _writeLock.Release(); }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            Envelope response;
            try
            {
                response = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AgentRequestException(new ErrorInfo(ErrorCodes.Timeout, $"No response to '{type}' within {timeout.TotalSeconds:0}s."));
            }

            if (response.Type == MessageTypes.Error || response.Error is not null)
                throw new AgentRequestException(response.Error ?? new ErrorInfo(ErrorCodes.InternalError, "Unknown agent error."));

            return response.PayloadAs<TResponse>()
                   ?? throw new AgentRequestException(new ErrorInfo(ErrorCodes.InternalError, "Empty response from agent."));
        }
        catch (IOException ex)
        {
            throw new AgentRequestException(new ErrorInfo(ErrorCodes.Disconnected, "Lost connection to the agent: " + ex.Message));
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var env = await Frames.ReadAsync(_pipe, _cts.Token).ConfigureAwait(false);
                if (env is null) break;
                if (_pending.TryGetValue(env.Id, out var tcs)) tcs.TrySetResult(env);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or EndOfStreamException)
        {
            // Connection closed.
        }
        catch (Exception ex)
        {
            HubLog.Error($"Reading from {PipeName} failed.", ex);
        }
        finally
        {
            var error = new AgentRequestException(new ErrorInfo(ErrorCodes.Disconnected, "The agent closed the connection (host closed or busy?)."));
            foreach (var tcs in _pending.Values) tcs.TrySetException(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _pipe.DisposeAsync().ConfigureAwait(false); } catch { /* ignored */ }
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* ignored */ }
        }
        _cts.Dispose();
    }
}
