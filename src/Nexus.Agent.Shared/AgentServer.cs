using System.Diagnostics;
using System.IO.Pipes;
using Nexus.Contracts;

namespace Nexus.Agent;

public enum AgentState { Stopped, Listening, Connected, Faulted }

public sealed class AgentStatus
{
    public AgentState State { get; init; }
    public int Clients { get; init; }
    public long RequestsHandled { get; init; }
    public string? LastError { get; init; }
    public string PipeName { get; init; } = "";
}

public sealed class AgentServerOptions
{
    /// <summary>How long a request may wait for the host thread before failing with HostBusy.</summary>
    public TimeSpan HostStartTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Write the discovery file. Tests turn this off.</summary>
    public bool WriteRegistration { get; set; } = true;
}

/// <summary>
/// Named-pipe server: accepts hub connections, decodes requests, dispatches them
/// to the host thread through <see cref="IHostDispatcher"/>, and returns results
/// or structured errors. Nothing here may throw into the host.
/// </summary>
public sealed class AgentServer<TDoc> : IDisposable where TDoc : class
{
    private readonly IHostDispatcher _dispatcher;
    private readonly IDocumentProvider<TDoc> _documents;
    private readonly ReaderRegistry<TDoc> _readers;
    private readonly IHostDataWriter<TDoc>? _writer;
    private readonly IHostSelector<TDoc>? _selector;
    private readonly IPdfExporter<TDoc>? _pdf;
    private readonly AgentLog _log;
    private readonly AgentServerOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private HostInfo _host;
    private Task? _acceptLoop;
    private int _clients;
    private long _requests;
    private string? _lastError;
    private AgentState _state = AgentState.Stopped;

    public AgentServer(HostInfo host, IHostDispatcher dispatcher, IDocumentProvider<TDoc> documents,
        ReaderRegistry<TDoc> readers, AgentLog log, AgentServerOptions? options = null,
        IHostDataWriter<TDoc>? writer = null, IHostSelector<TDoc>? selector = null,
        IPdfExporter<TDoc>? pdf = null)
    {
        _writer = writer;
        _selector = selector;
        _pdf = pdf;
        _host = WithFeatures(host);
        _dispatcher = dispatcher;
        _documents = documents;
        _readers = readers;
        _log = log;
        _options = options ?? new AgentServerOptions();
        PipeName = Protocol.PipeName(host.HostKind, host.ProcessId);
    }

    public string PipeName { get; }

    /// <summary>Raised on a background thread whenever the status changes.</summary>
    public event Action<AgentStatus>? StatusChanged;

    public AgentStatus Status => new()
    {
        State = _state,
        Clients = Volatile.Read(ref _clients),
        RequestsHandled = Interlocked.Read(ref _requests),
        LastError = _lastError,
        PipeName = PipeName,
    };

    public void Start()
    {
        if (_acceptLoop is not null) return;
        _log.Info($"Agent starting: {_host.DisplayName}, pipe {PipeName}");
        WriteRegistration();
        SetState(AgentState.Listening);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Update host info (e.g. after optional modules load) and re-publish the registration.</summary>
    public void UpdateHost(HostInfo host)
    {
        _host = WithFeatures(host);
        WriteRegistration();
    }

    private HostInfo WithFeatures(HostInfo host)
    {
        if (_writer is not null && !host.Features.Contains(AgentFeatures.Write))
            host.Features.Add(AgentFeatures.Write);
        if (_selector is not null && !host.Features.Contains(AgentFeatures.Select))
            host.Features.Add(AgentFeatures.Select);
        if (_pdf is not null && !host.Features.Contains(AgentFeatures.ExportPdf))
            host.Features.Add(AgentFeatures.ExportPdf);
        return host;
    }

    private void WriteRegistration()
    {
        if (!_options.WriteRegistration) return;
        try
        {
            var path = AgentRegistrationFile.Write(new AgentRegistration
            {
                PipeName = PipeName,
                Host = _host,
                RegisteredUtc = DateTime.UtcNow,
            });
            _log.Info("Registration written: " + path);
        }
        catch (Exception ex)
        {
            _log.Error("Could not write registration file; the hub may not discover this agent.", ex);
        }
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = Compat.CreateUserOnlyServer(PipeName);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connected = server;
                server = null;
                _ = Task.Run(() => HandleConnectionAsync(connected, ct));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _log.Error("Pipe accept failed; retrying.", ex);
                SetState(AgentState.Faulted);
                try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { break; }
                SetState(Volatile.Read(ref _clients) > 0 ? AgentState.Connected : AgentState.Listening);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken serverCt)
    {
        Interlocked.Increment(ref _clients);
        SetState(AgentState.Connected);
        _log.Info("Hub connected.");

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        var writeLock = new SemaphoreSlim(1, 1);
        var inFlight = new List<Task>();

        try
        {
            while (!connectionCts.IsCancellationRequested)
            {
                Envelope? request;
                try
                {
                    request = await Frames.ReadAsync(pipe, connectionCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
                {
                    break;
                }
                if (request is null) break;

                // Handle requests concurrently so a long read does not block pings.
                inFlight.RemoveAll(t => t.IsCompleted);
                inFlight.Add(Task.Run(async () =>
                {
                    var response = await HandleRequestAsync(request, connectionCts.Token).ConfigureAwait(false);
                    await writeLock.WaitAsync(connectionCts.Token).ConfigureAwait(false);
                    try { await Frames.WriteAsync(pipe, response, connectionCts.Token).ConfigureAwait(false); }
                    finally { writeLock.Release(); }
                }).ContinueWith(t =>
                {
                    if (t.Exception is not null) _log.Warn("Could not send a response.", t.Exception.GetBaseException());
                }, TaskScheduler.Default));
            }
        }
        catch (Exception ex)
        {
            _log.Error("Connection handler failed.", ex);
        }
        finally
        {
            connectionCts.Cancel();
            try { await Task.WhenAll(inFlight).ConfigureAwait(false); } catch { /* logged above */ }
            try { pipe.Dispose(); } catch { /* ignored */ }
            int left = Interlocked.Decrement(ref _clients);
            _log.Info("Hub disconnected.");
            if (!_stop.IsCancellationRequested)
                SetState(left > 0 ? AgentState.Connected : AgentState.Listening);
        }
    }

    internal async Task<Envelope> HandleRequestAsync(Envelope request, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        try
        {
            if (request.V != Protocol.Version)
                throw new AgentException(ErrorCodes.ProtocolMismatch,
                    $"Hub speaks protocol {request.V}, agent speaks {Protocol.Version}. Update the hub and agents together.");

            object result = request.Type switch
            {
                MessageTypes.Hello => new HelloResponse { Host = _host },
                MessageTypes.Ping => Empty.Instance,
                MessageTypes.ListReaders => new ListReadersResponse { Readers = _readers.Descriptors() },
                MessageTypes.ListDocuments => new ListDocumentsResponse
                {
                    Documents = await _dispatcher.InvokeAsync(
                        () => _documents.ListDocuments().ToList(), _options.HostStartTimeout, ct).ConfigureAwait(false),
                },
                MessageTypes.Read => await ReadAsync(request.PayloadAs<ReadRequest>(), ct).ConfigureAwait(false),
                MessageTypes.Write => await WriteAsync(request.PayloadAs<WriteRequest>(), ct).ConfigureAwait(false),
                MessageTypes.Select => await SelectAsync(request.PayloadAs<SelectRequest>(), ct).ConfigureAwait(false),
                MessageTypes.ExportPdf => await ExportPdfAsync(request.PayloadAs<ExportPdfRequest>(), ct).ConfigureAwait(false),
                _ => throw new AgentException(ErrorCodes.UnknownMessage, $"Unknown message type '{request.Type}'."),
            };
            return Envelope.Result(request.Id, result);
        }
        catch (Exception ex)
        {
            var error = ToError(ex);
            if (error.Code == ErrorCodes.InternalError) _log.Error($"Request '{request.Type}' failed.", ex);
            else _log.Warn($"Request '{request.Type}' failed: {error}");
            return Envelope.Failure(request.Id, error);
        }
    }

    private async Task<ReadResult> ReadAsync(ReadRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ReaderId))
            throw new AgentException(ErrorCodes.BadRequest, "A read request needs a readerId.");

        var reader = _readers.Find(request.ReaderId)
                     ?? throw new AgentException(ErrorCodes.ReaderNotFound, $"No reader '{request.ReaderId}' in this agent.");

        if (!reader.Descriptor.IsImplemented)
            throw new AgentException(ErrorCodes.NotImplemented, $"Reader '{reader.Descriptor.Id}' is a placeholder and is not implemented yet.");

        string docId = string.IsNullOrWhiteSpace(request.DocumentId) ? "active" : request.DocumentId;
        _log.Info($"Read {reader.Descriptor.Id} on {docId}");

        return await _dispatcher.InvokeAsync(() =>
        {
            var doc = _documents.Find(docId) ?? throw (docId == "active"
                ? new AgentException(ErrorCodes.NoDocument, "No document is active.")
                : new AgentException(ErrorCodes.DocumentNotFound, $"Document '{docId}' is not open (it may have been closed)."));

            var info = _documents.Describe(doc);
            var context = new ReadContext(reader.Descriptor, request.Options, _log, ct);
            var sw = Stopwatch.StartNew();
            reader.Read(doc, context);
            sw.Stop();
            _log.Info($"Read {reader.Descriptor.Id} on '{info.Title}': {context.Items.Count} items, {context.Warnings.Count} warnings, {sw.ElapsedMilliseconds} ms");

            return new ReadResult
            {
                ReaderId = reader.Descriptor.Id,
                DocumentId = info.Id,
                DocumentTitle = info.Title,
                ReadUtc = DateTime.UtcNow,
                ElapsedMs = sw.ElapsedMilliseconds,
                Items = context.Items,
                Warnings = context.Warnings,
                Truncated = context.Truncated,
            };
        }, _options.HostStartTimeout, ct).ConfigureAwait(false);
    }

    private async Task<ExportPdfResult> ExportPdfAsync(ExportPdfRequest? request, CancellationToken ct)
    {
        if (_pdf is null)
            throw new AgentException(ErrorCodes.NotImplemented, $"{_host.Product} cannot create PDFs from Nexus.");
        if (request is null || string.IsNullOrWhiteSpace(request.DocumentId) || string.IsNullOrWhiteSpace(request.OutputFolder))
            throw new AgentException(ErrorCodes.BadRequest, "A PDF export needs an open document and an output folder.");
        Directory.CreateDirectory(request.OutputFolder);

        return await _dispatcher.InvokeAsync(() =>
        {
            var sw = Stopwatch.StartNew();
            var doc = _documents.Find(request.DocumentId!)
                      ?? throw new AgentException(ErrorCodes.DocumentNotFound, $"Document '{request.DocumentId}' is not open (it may have been closed).");
            var result = _pdf.Export(doc, request, _log, ct);
            result.ElapsedMs = sw.ElapsedMilliseconds;
            _log.Info($"PDF: {result.Sheets.Count(s => s.PdfPath is not null)} of {result.Sheets.Count} sheet(s) in {sw.ElapsedMilliseconds} ms");
            return result;
        }, _options.HostStartTimeout, ct).ConfigureAwait(false);
    }

    private async Task<WriteResult> WriteAsync(WriteRequest? request, CancellationToken ct)
    {
        if (_writer is null)
            throw new AgentException(ErrorCodes.NotImplemented, $"{_host.Product} does not support editing yet.");
        if (request is null || string.IsNullOrWhiteSpace(request.DocumentId) || request.Changes.Count == 0)
            throw new AgentException(ErrorCodes.BadRequest, "A write request needs a documentId and at least one change.");

        return await _dispatcher.InvokeAsync(() =>
        {
            var doc = _documents.Find(request.DocumentId)
                      ?? throw new AgentException(ErrorCodes.DocumentNotFound, $"Document '{request.DocumentId}' is not open (it may have been closed).");
            var info = _documents.Describe(doc);
            _log.Info($"Write {request.Changes.Count} change(s) to '{info.Title}'");

            var sw = Stopwatch.StartNew();
            var result = _writer.Write(doc, request, _log, ct);
            sw.Stop();
            result.DocumentId = info.Id;
            result.DocumentTitle = info.Title;
            result.ElapsedMs = sw.ElapsedMilliseconds;

            var counts = result.Results.GroupBy(r => r.Status).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}");
            _log.Info($"Write to '{info.Title}': {(result.Committed ? "committed" : "not committed")}, {string.Join(", ", counts)}, {sw.ElapsedMilliseconds} ms");
            return result;
        }, _options.HostStartTimeout, ct).ConfigureAwait(false);
    }

    private async Task<SelectResult> SelectAsync(SelectRequest? request, CancellationToken ct)
    {
        if (_selector is null)
            throw new AgentException(ErrorCodes.NotImplemented, $"{_host.Product} cannot show items yet.");
        if (request is null || string.IsNullOrWhiteSpace(request.DocumentId) || request.ItemIds.Count == 0)
            throw new AgentException(ErrorCodes.BadRequest, "A select request needs a documentId and at least one item id.");

        return await _dispatcher.InvokeAsync(() =>
        {
            var doc = _documents.Find(request.DocumentId)
                      ?? throw new AgentException(ErrorCodes.DocumentNotFound, $"Document '{request.DocumentId}' is not open (it may have been closed).");
            var result = _selector.Select(doc, request.ItemIds, _log);
            _log.Info($"Select {request.ItemIds.Count} item(s): {result.Message}");
            return result;
        }, _options.HostStartTimeout, ct).ConfigureAwait(false);
    }

    private static ErrorInfo ToError(Exception ex) => ex switch
    {
        AgentException a => a.ToErrorInfo(),
        NotImplementedException => new ErrorInfo(ErrorCodes.NotImplemented, ex.Message),
        OperationCanceledException => new ErrorInfo(ErrorCodes.Cancelled, "The request was cancelled."),
        System.Text.Json.JsonException => new ErrorInfo(ErrorCodes.BadRequest, "Malformed request: " + ex.Message),
        _ => new ErrorInfo(ErrorCodes.InternalError, $"{ex.GetType().Name}: {ex.Message}", ex.ToString()),
    };

    private void SetState(AgentState state)
    {
        _state = state;
        try { StatusChanged?.Invoke(Status); }
        catch (Exception ex) { _log.Warn("StatusChanged handler threw.", ex); }
    }

    public void Dispose()
    {
        try
        {
            _stop.Cancel();
            try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignored */ }
            if (_options.WriteRegistration) AgentRegistrationFile.Delete(_host.ProcessId);
            SetState(AgentState.Stopped);
            _log.Info("Agent stopped.");
        }
        catch (Exception ex)
        {
            _log.Error("Error while stopping agent.", ex);
        }
    }
}
