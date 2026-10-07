using Nexus.Contracts;

namespace Nexus.Hub.Core.Files;

/// <summary>
/// Reads files on disk and makes PDFs of their sheets, without the files being open:
/// Revit models through a running Revit (opened in the background, never shown), drawings through
/// AutoCAD's Core Console (no AutoCAD window at all). Always on a private copy; never written.
/// </summary>
public sealed class DiskReader
{
    /// <summary>What can be read from drawings without AutoCAD open.</summary>
    public static readonly string[] DrawingReaders = { "acad.sheets" };

    // Several drawings at once (each is its own Core Console process).
    private readonly SemaphoreSlim _consoles = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    // One background job per Revit at a time: Revit works on one thing at a time, and a request that waits
    // longer than the agent's start timeout behind a large model would fail as "busy".
    private readonly Dictionary<int, SemaphoreSlim> _revitQueues = new();

    /// <summary>Runs <paramref name="work"/> when that Revit has finished the previous background job.</summary>
    public async Task<T> OnRevitAsync<T>(AgentConnection revit, Func<Task<T>> work, CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (_revitQueues)
        {
            if (!_revitQueues.TryGetValue(revit.Host.ProcessId, out gate!))
                _revitQueues[revit.Host.ProcessId] = gate = new SemaphoreSlim(1, 1);
        }
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Retries while Revit is busy: a Revit that has just started (Start Revit) takes a while before it takes
    /// requests, and so does one showing a dialog.
    /// </summary>
    private static async Task<T> WhenNotBusyAsync<T>(Func<Task<T>> request, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await request().ConfigureAwait(false); }
            catch (AgentRequestException ex) when (ex.Code == ErrorCodes.HostBusy && attempt < 6)
            {
                HubLog.Info($"Revit is busy; trying again ({attempt}).");
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Error code when a Revit model would have to be opened by Revit and the user has not allowed it.</summary>
    public const string RevitNotAllowed = "RevitNotAllowed";

    /// <summary>
    /// Whether a running Revit may open a copy of a Revit model to read it or make PDFs. Off unless the user
    /// turns it on: without it, only results kept from earlier reads are shown.
    /// </summary>
    public Func<bool> MayOpenRevit { get; set; } = () => false;

    private const string NotAllowedText =
        "Not read: Revit models can only be read by Revit opening them, and that is turned off (⋯ › Files on disk).";

    /// <summary>The running programs (refreshed by the hub).</summary>
    public Func<IReadOnlyList<AgentConnection>> Programs { get; set; } = () => Array.Empty<AgentConnection>();

    /// <summary>Why a reader cannot read this file (null when it can).</summary>
    public static string? Unsupported(DiskFile file, string readerId) =>
        !file.IsRevit && !DrawingReaders.Contains(readerId, StringComparer.OrdinalIgnoreCase)
            ? "Only Sheets can be read from drawings that are not open. Open the drawing in AutoCAD for this view."
            : null;

    /// <summary>The Revit that opens <paramref name="file"/>: same release first, else the closest newer one.</summary>
    public AgentConnection? RevitFor(DiskFile file, string feature) =>
        Programs()
            .Where(a => a.Host.HostKind == HostKinds.Revit && a.Host.Features.Contains(feature))
            .Select(a => (Agent: a, Year: int.TryParse(a.Host.Version, out int y) ? y : 0))
            .Where(x => file.RevitYear is null || x.Year >= file.RevitYear)
            .OrderBy(x => file.RevitYear is null ? -x.Year : x.Year - file.RevitYear.Value)
            .Select(x => x.Agent)
            .FirstOrDefault();

    private string NoRevit(DiskFile file)
    {
        string year = file.RevitYear is { } y ? $"Revit {y} or later" : "Revit 2024 or later";
        bool older = Programs().Any(a => a.Host.HostKind == HostKinds.Revit);
        return older && file.RevitYear is not null
            ? $"{file.Name} is a Revit {file.RevitYear} model; the Revit running here is older. Start {year} (no project needs to be open)."
            : $"Reading Revit models that are not open needs {year} running with the Nexus add-in (no project needs to be open). " +
              "The model is opened in the background and closed again; nothing is shown or saved.";
    }

    /// <summary>Reads one file (from the cache when the file has not changed).</summary>
    public async Task<(ReadResult Result, bool FromCache)> ReadAsync(DiskFile file, ReadRequest request, CancellationToken ct, bool ignoreCache = false)
    {
        if (Unsupported(file, request.ReaderId) is { } why) throw Fail(ErrorCodes.NotImplemented, why);
        if (file.Changed()) await Task.Run(file.Inspect, ct).ConfigureAwait(false);
        if (!ignoreCache && ReadCache.Get(file, request) is { } cached) return (Stamp(cached, file), true);

        ReadResult result;
        if (file.IsRevit)
        {
            if (!MayOpenRevit()) throw Fail(RevitNotAllowed, NotAllowedText);
            var revit = RevitFor(file, AgentFeatures.ReadFile) ?? throw Fail(ErrorCodes.NotImplemented, NoRevit(file));
            result = await OnRevitAsync(revit, async () =>
            {
                using var copy = await WorkCopy.CreateAsync(file.Path, ct).ConfigureAwait(false);
                var read = new ReadFileRequest { Path = copy.Path, ReaderId = request.ReaderId, Options = request.Options };
                return await WhenNotBusyAsync(() => revit.ReadFileAsync(read, ct), ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        else
        {
            var install = CoreConsole.Find().FirstOrDefault() ?? throw Fail(ErrorCodes.NotImplemented, CoreConsole.NotInstalled);
            await _consoles.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var copy = await WorkCopy.CreateAsync(file.Path, ct).ConfigureAwait(false);
                var job = new HeadlessJob { Mode = HeadlessJob.ModeRead, Reads = { request } };
                var done = await CoreConsole.RunAsync(install, copy.Path, job, copy.Folder, loadCivil3D: false, ct).ConfigureAwait(false);
                result = done.Reads.FirstOrDefault() ?? throw Fail(ErrorCodes.InternalError, "The add-in read nothing.");
            }
            finally
            {
                _consoles.Release();
            }
        }
        Stamp(result, file);
        ReadCache.Put(file, request, result);
        return (result, false);
    }

    /// <summary>
    /// PDFs of sheets of a file on disk (all sheets when <see cref="ExportPdfRequest.ItemIds"/> is empty).
    /// <paramref name="request"/>'s Path is replaced by the working copy's.
    /// </summary>
    public async Task<ExportPdfResult> ExportPdfAsync(DiskFile file, ExportPdfRequest request, CancellationToken ct)
    {
        if (file.Changed()) await Task.Run(file.Inspect, ct).ConfigureAwait(false);
        if (file.IsRevit)
        {
            if (!MayOpenRevit()) throw Fail(RevitNotAllowed, "PDFs of Revit models on disk need Revit to open the model, and that is turned off (⋯ › Files on disk).");
            var revit = RevitFor(file, AgentFeatures.ExportPdf) ?? throw Fail(ErrorCodes.NotImplemented, NoRevit(file));
            return await OnRevitAsync(revit, async () =>
            {
                using var copy = await WorkCopy.CreateAsync(file.Path, ct).ConfigureAwait(false);
                request.Path = copy.Path;
                request.DocumentId = null;
                return await WhenNotBusyAsync(() => revit.ExportPdfAsync(request, ct), ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        var install = CoreConsole.Find().FirstOrDefault() ?? throw Fail(ErrorCodes.NotImplemented, CoreConsole.NotInstalled);
        await _consoles.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var copy = await WorkCopy.CreateAsync(file.Path, ct).ConfigureAwait(false);
            request.Path = copy.Path;
            var job = new HeadlessJob { Mode = HeadlessJob.ModePdf, Pdf = request };
            var done = await CoreConsole.RunAsync(install, copy.Path, job, copy.Folder, loadCivil3D: true, ct).ConfigureAwait(false);
            return done.Pdf ?? throw Fail(ErrorCodes.InternalError, "The add-in made no PDFs.");
        }
        finally
        {
            _consoles.Release();
        }
    }

    /// <summary>Results name the original file, not the copy that was read.</summary>
    private static ReadResult Stamp(ReadResult result, DiskFile file)
    {
        result.DocumentId = file.DocumentId;
        result.DocumentTitle = file.Name;
        return result;
    }

    private static AgentRequestException Fail(string code, string message) => new(new ErrorInfo(code, message));
}
