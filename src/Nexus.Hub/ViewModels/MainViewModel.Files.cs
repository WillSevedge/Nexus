using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Files;

namespace Nexus.Hub.ViewModels;

/// <summary>
/// Files on disk: Revit models and drawings added from a folder (Autodesk Docs / Forma through Desktop
/// Connector, a network share...) and read without opening them in a program. Always read-only.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly DiskReader _disk = new();
    private IReadOnlyList<AgentConnection> _programs = Array.Empty<AgentConnection>();

    public ObservableCollection<DiskFileNode> DiskFiles { get; } = new();
    public bool HasDiskFiles => DiskFiles.Count > 0;

    private RelayCommand? _addDiskFiles, _addDiskFolder;
    public RelayCommand AddDiskFilesCommand => _addDiskFiles ??= new RelayCommand(() => { PickDiskFiles(); return Task.CompletedTask; });
    public RelayCommand AddDiskFolderCommand => _addDiskFolder ??= new RelayCommand(() => { PickDiskFolder(); return Task.CompletedTask; });

    /// <summary>Called once at start: the files added last time.</summary>
    private void RestoreDiskFiles()
    {
        _disk.Programs = () => _programs;
        WorkCopy.CleanUp();
        foreach (var saved in _settings.DiskFiles.Where(f => f.Path.Length > 0))
            AddDiskNode(saved.Path, saved.Checked);
        RaiseDiskFiles();
    }

    /// <summary>Where the file pickers start: last folder, else Desktop Connector's Autodesk Docs folder.</summary>
    private string StartFolder()
    {
        if (_settings.LastDiskFolder is { } last && Directory.Exists(last)) return last;
        string dc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DC");
        foreach (var candidate in new[] { Path.Combine(dc, "ACCDocs"), dc })
            if (Directory.Exists(candidate)) return candidate;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void PickDiskFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add files to read without opening them",
            Filter = "Revit models and drawings (*.rvt;*.dwg)|*.rvt;*.dwg|Revit models (*.rvt)|*.rvt|Drawings (*.dwg)|*.dwg",
            Multiselect = true,
            InitialDirectory = StartFolder(),
        };
        if (dialog.ShowDialog() != true) return;
        _settings.LastDiskFolder = Path.GetDirectoryName(dialog.FileNames[0]);
        _ = AddDiskFilesAsync(dialog.FileNames);
    }

    private void PickDiskFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Add every Revit model and drawing in a folder (and its subfolders)",
            InitialDirectory = StartFolder(),
        };
        if (dialog.ShowDialog() != true) return;
        string folder = dialog.FolderName;
        _settings.LastDiskFolder = folder;
        List<string> found;
        try { found = DiskFile.FindIn(folder); }
        catch (Exception ex)
        {
            Notify(NoticeKind.Error, "Could not look in that folder", ex.Message);
            return;
        }
        if (found.Count == 0)
        {
            Notify(NoticeKind.Info, "No Revit models or drawings there", folder);
            return;
        }
        if (found.Count > 40 && !Dialogs.Confirm(
                $"Add {found.Count} files?",
                $"{folder} and its subfolders have {found.Count} Revit models and drawings. Each one is read the first time it is ticked, which can take a while for large models.",
                "Add them"))
            return;
        _ = AddDiskFilesAsync(found);
    }

    /// <summary>Adds files (ticked) and reads them into the current view.</summary>
    public async Task AddDiskFilesAsync(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            if (!DiskFile.IsSupported(path)) continue;
            if (DiskFiles.Any(f => f.File.Path.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) continue;
            AddDiskNode(path, true);
            added++;
        }
        RaiseDiskFiles();
        SaveDiskFiles();
        if (added == 0) return;
        Status = $"Added {added} file{(added == 1 ? "" : "s")} on disk. They are read in the background and shown read-only.";
        await LoadAsync();
    }

    private void AddDiskNode(string path, bool isChecked)
    {
        DiskFileNode node;
        try { node = new DiskFileNode(DiskFile.Create(path), isChecked); }
        catch (Exception ex)
        {
            HubLog.Warn($"Cannot add {path}.", ex);
            return;
        }
        node.CheckedChanged += OnDiskChecked;
        DiskFiles.Add(node);
        // Version and size off the UI thread (a Desktop Connector file may download first).
        _ = Task.Run(() =>
        {
            try
            {
                node.File.Inspect();
                Application.Current?.Dispatcher.BeginInvoke(() => { node.Error = null; node.Inspected(); });
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => node.Error = ex is FileNotFoundException ? "File not found." : ex.Message);
            }
        });
    }

    private void OnDiskChecked(DiskFileNode node)
    {
        SaveDiskFiles();
        _reloadDelay?.Cancel();
        var cts = _reloadDelay = new CancellationTokenSource();
        _ = Task.Delay(350, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Application.Current.Dispatcher.BeginInvoke(() => _ = LoadAsync());
        }, TaskScheduler.Default);
    }

    public void RemoveDiskFile(DiskFileNode node)
    {
        node.CheckedChanged -= OnDiskChecked;
        DiskFiles.Remove(node);
        RaiseDiskFiles();
        SaveDiskFiles();
        if (node.IsChecked) _ = LoadAsync();
    }

    public void RemoveAllDiskFiles()
    {
        if (DiskFiles.Count == 0) return;
        bool reload = DiskFiles.Any(f => f.IsChecked);
        foreach (var node in DiskFiles) node.CheckedChanged -= OnDiskChecked;
        DiskFiles.Clear();
        RaiseDiskFiles();
        SaveDiskFiles();
        if (reload) _ = LoadAsync();
    }

    /// <summary>Reads the file again even if it did not change.</summary>
    public async Task RereadDiskFileAsync(DiskFileNode node)
    {
        _forceDiskRead.Add(node.File.Path);
        if (!node.IsChecked) node.IsChecked = true; // reloads
        else await LoadAsync();
    }

    public static void ShowInFolder(DiskFileNode node) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.File.Path}\"") { UseShellExecute = true });

    private readonly HashSet<string> _forceDiskRead = new(StringComparer.OrdinalIgnoreCase);

    private void SaveDiskFiles()
    {
        _settings.DiskFiles = DiskFiles.Select(f => new DiskFileSetting { Path = f.File.Path, Checked = f.IsChecked }).ToList();
        _settings.Save();
    }

    private void RaiseDiskFiles()
    {
        Raise(nameof(HasDiskFiles));
        Raise(nameof(HasPrograms));
        Raise(nameof(NoPrograms));
        Raise(nameof(EmptyTitle));
        Raise(nameof(EmptyText));
        Raise(nameof(IsEmpty));
    }

    // ------------------------------------------------------------------ Print & PDF

    /// <summary>Print &amp; PDF for the selected sheets (or every sheet shown), in the order shown.</summary>
    public PrintViewModel? CreatePrint(IReadOnlyList<System.Data.DataRowView> views, bool selection)
    {
        var sheets = new List<PrintSheet>();
        var seen = new HashSet<TableRow>(ReferenceEqualityComparer.Instance);
        foreach (var view in views)
        {
            if (RowOf(view) is not { } row || !seen.Add(row)) continue;
            if (row.Source.Tag is not ResultRun run || row.ItemType is not ("Sheet" or "Layout") || row.ItemId.Length == 0) continue;
            string number = CurrentValue(row, SheetFieldMap.NumberColumnId) ?? "";
            string name = CurrentValue(row, SheetFieldMap.ColumnId("Title")) is { Length: > 0 } t ? t : row.Item.Trim();
            sheets.Add(new PrintSheet { Row = row, Run = run, Number = number.Trim(), Name = name.Trim() });
        }
        if (sheets.Count == 0) return null;
        string scope = selection ? $"{sheets.Count} selected sheets" : $"All {sheets.Count} sheets shown";
        return new PrintViewModel(this, _disk, sheets, scope, _settings.PdfFolder, _settings.PdfNamePattern);
    }

    public void RememberPdfSettings(string folder, string pattern)
    {
        _settings.PdfFolder = folder;
        _settings.PdfNamePattern = pattern;
        _settings.Save();
    }

    /// <summary>The reader for a file on disk in a view; null when the view does not apply to the file.</summary>
    private static string? DiskReaderId(DatasetNode dataset, DiskFile file)
    {
        if (dataset.IsSheetIndex) return file.IsRevit ? "revit.sheets" : "acad.sheets";
        if (!dataset.Readers.TryGetValue(file.HostKind, out var reader)) return null;
        return DiskReader.Unsupported(file, reader.Descriptor.Id) is null ? reader.Descriptor.Id : null;
    }

    /// <summary>Reads the ticked files on disk for a view (several at once; results come from the cache when unchanged).</summary>
    /// <remarks>
    /// Revit models wait for <paramref name="openReads"/> (the open files' reads) so the running Revit is not
    /// asked to open a model in the background while it is reading the open ones. Drawings start straight away.
    /// </remarks>
    private async Task<List<ResultRun>> ReadDiskFilesAsync(DatasetNode dataset, IReadOnlyList<DiskFileNode> files, CancellationTokenSource work, Task openReads)
    {
        var tasks = files.Select(async node =>
        {
            if (node.File.IsRevit)
            {
                try { await openReads; } catch { /* reported with the open files */ }
            }
            string readerId = DiskReaderId(dataset, node.File)!;
            var options = dataset.Readers.TryGetValue(node.File.HostKind, out var r) ? r.OptionValues() : new Dictionary<string, string>();
            var request = new ReadRequest { DocumentId = node.File.DocumentId, ReaderId = readerId, Options = options };
            var run = await ReadDiskOneAsync(node, request, work.Token);
            StepWork(work);
            return run;
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    private async Task<ResultRun> ReadDiskOneAsync(DiskFileNode node, ReadRequest request, CancellationToken ct)
    {
        var file = node.File;
        node.IsBusy = true;
        try
        {
            bool force = _forceDiskRead.Remove(file.Path);
            if (force) await Task.Run(file.Inspect, ct); // a new signature: the cached result is not used
            var (result, cached) = force
                ? await _disk.ReadAsync(file, request, ct, ignoreCache: true)
                : await _disk.ReadAsync(file, request, ct);
            node.Error = null;
            node.Inspected();
            node.Status = (cached ? "Unchanged since read " : "Read ") + result.ReadUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
            HubLog.Info($"File on disk {file.Name}: {request.ReaderId}: {result.Items.Count} items{(cached ? " (unchanged, from the last read)" : "")}");
            return new ResultRun { Host = DiskHost(file), DocumentTitle = file.Name, ReaderId = request.ReaderId, Request = request, Result = result, Disk = file };
        }
        catch (AgentRequestException ex)
        {
            node.Error = ex.Error.Message;
            HubLog.Warn($"File on disk {file.Name}: {ex.Error}");
            return new ResultRun { Host = DiskHost(file), DocumentTitle = file.Name, ReaderId = request.ReaderId, Request = request, Error = ex.Error, Disk = file };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            string message = ex is FileNotFoundException ? "File not found." : ex.Message;
            node.Error = message;
            HubLog.Error($"File on disk {file.Name} could not be read.", ex);
            return new ResultRun
            {
                Host = DiskHost(file), DocumentTitle = file.Name, ReaderId = request.ReaderId, Request = request,
                Error = new ErrorInfo(ErrorCodes.InternalError, message), Disk = file,
            };
        }
        finally
        {
            node.IsBusy = false;
        }
    }

    /// <summary>Stands in for the program in results from a file on disk: read-only, cannot show items.</summary>
    private static HostInfo DiskHost(DiskFile file) => new()
    {
        HostKind = file.HostKind,
        Product = file.IsRevit ? "Revit" : "AutoCAD",
        Version = file.RevitYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        Features = { AgentFeatures.FileOnDisk },
    };
}
