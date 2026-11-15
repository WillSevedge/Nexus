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

    private AgentClient(string pipeName, bool currentUserOnly = true)
    {
        PipeName = pipeName;
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | (currentUserOnly ? PipeOptions.CurrentUserOnly : PipeOptions.None));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    /// <summary>The process id at the end of an agent's pipe name ("Nexus.Agent.Revit.1234" → 1234), or -1.</summary>
    public static int ProcessIdOf(string pipeName)
    {
        int dot = pipeName.LastIndexOf('.');
        return dot >= 0 && int.TryParse(pipeName[(dot + 1)..], out int pid) ? pid : -1;
    }

    public string PipeName { get; }
    public bool IsConnected => _pipe.IsConnected && _readLoop is { IsCompleted: false };

    public static async Task<AgentClient> ConnectAsync(string pipeName, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        int wait = (int)(timeout ?? TimeSpan.FromSeconds(3)).TotalMilliseconds;
        var client = new AgentClient(pipeName);
        try
        {
            await client._pipe.ConnectAsync(wait, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            // A program run "as administrator" owns its pipe as the Administrators group, not as the user, so the
            // check that the pipe belongs to this user refuses it. Connect without that check, and make sure instead
            // that the pipe is served by the very process its name says (the program the hub found).
            await client.DisposeAsync().ConfigureAwait(false);
            client = new AgentClient(pipeName, currentUserOnly: false);
            try
            {
                await client._pipe.ConnectAsync(wait, ct).ConfigureAwait(false);
                if (!GetNamedPipeServerProcessId(client._pipe.SafePipeHandle, out uint server) || server != (uint)ProcessIdOf(pipeName))
                    throw new UnauthorizedAccessException($"The pipe {pipeName} is not served by the program it is named after.");
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
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

    public async Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct = default)
    {
        request.Compact = true;
        var result = await RequestAsync<ReadResult>(MessageTypes.Read, request, ReadTimeout, ct).ConfigureAwait(false);
        Compaction.Expand(result);
        return result;
    }

    public Task<WriteResult> WriteAsync(WriteRequest request, CancellationToken ct = default) =>
        RequestAsync<WriteResult>(MessageTypes.Write, request, ReadTimeout, ct);

    /// <summary>Exporting a large set of sheets can take a while.</summary>
    public static readonly TimeSpan PdfTimeout = TimeSpan.FromMinutes(60);

    public Task<ExportPdfResult> ExportPdfAsync(ExportPdfRequest request, CancellationToken ct = default) =>
        RequestAsync<ExportPdfResult>(MessageTypes.ExportPdf, request, PdfTimeout, ct);

    public Task<CreateSheetsResult> CreateSheetsAsync(CreateSheetsRequest request, CancellationToken ct = default) =>
        RequestAsync<CreateSheetsResult>(MessageTypes.CreateSheets, request, ReadTimeout, ct);

    public async Task<List<ToolDescriptor>> ListToolsAsync(CancellationToken ct = default) =>
        (await RequestAsync<ListToolsResponse>(MessageTypes.ListTools, Empty.Instance, DefaultTimeout, ct).ConfigureAwait(false)).Tools;

    /// <summary>Drawing many entities or reading a large drawing can take a while.</summary>
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(5);

    public Task<CallToolResult> CallToolAsync(CallToolRequest request, CancellationToken ct = default) =>
        RequestAsync<CallToolResult>(MessageTypes.CallTool, request, ToolTimeout, ct);

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
            // Never cancel half-way through a frame: that would leave the pipe out of step. Cancelling only
            // stops waiting for the reply (a late reply is dropped, it is matched by request id).
            try { await Frames.WriteAsync(_pipe, request, CancellationToken.None).ConfigureAwait(false); }
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
            var token = _cts.Token; // taken once: the source may be disposed while we wait
            while (!token.IsCancellationRequested)
            {
                var env = await Frames.ReadAsync(_pipe, token).ConfigureAwait(false);
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

    private int _disposed;

    /// <summary>Safe to call more than once and from several callers at the same time.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
        try { await _pipe.DisposeAsync().ConfigureAwait(false); } catch { /* ignored */ }
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* ignored */ }
        }
        _cts.Dispose();
    }
}
