using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Microsoft.Win32;

namespace Nexus.Hub.ViewModels;

public sealed class MainViewModel : Observable
{
    /// <summary>Above this many columns, new columns start unchecked to keep the grid fast.</summary>
    private const int AutoCheckColumnLimit = 300;
    private const int MaxGridColumns = 1500;

    private readonly Dictionary<int, AgentNode> _agents = new();
    private string _status = "Ready";
    private ResultRun? _selectedRun;
    private ItemNode? _selectedItem;
    private string _columnFilter = "";
    private bool _busy;

    public MainViewModel()
    {
        RefreshCommand = new RelayCommand(RefreshAsync);
        RunCommand = new RelayCommand(RunAsync, () => !_busy);
        ShowTableCommand = new RelayCommand(() => { RebuildColumns(); BuildTable(); return Task.CompletedTask; });
        ExportWideCommand = new RelayCommand(() => Export("wide"));
        ExportLongCommand = new RelayCommand(() => Export("long"));
        ExportJsonCommand = new RelayCommand(() => Export("json"));
        ClearResultsCommand = new RelayCommand(() =>
        {
            Results.Clear();
            Items.Clear();
            RebuildColumns();
            BuildTable();
            return Task.CompletedTask;
        });
        CheckAllColumnsCommand = new RelayCommand(() => SetAllColumns(true));
        UncheckAllColumnsCommand = new RelayCommand(() => SetAllColumns(false));
        OpenLogFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(NexusPaths.LogsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{NexusPaths.LogsDir}\"") { UseShellExecute = true });
            return Task.CompletedTask;
        });

        HubLog.Message += line => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Log.Add(line);
            if (Log.Count > 1000) Log.RemoveAt(0);
        });
    }

    public ObservableCollection<AgentNode> Agents { get; } = new();
    public ObservableCollection<ReaderNode> Readers { get; } = new();
    public ObservableCollection<ResultRun> Results { get; } = new();
    public ObservableCollection<ColumnGroupNode> ColumnGroups { get; } = new();
    public ObservableCollection<ItemNode> Items { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand ShowTableCommand { get; }
    public RelayCommand ExportWideCommand { get; }
    public RelayCommand ExportLongCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand ClearResultsCommand { get; }
    public RelayCommand CheckAllColumnsCommand { get; }
    public RelayCommand UncheckAllColumnsCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }

    /// <summary>Raised when the wide table is rebuilt; the window builds the grid columns.</summary>
    public event Action<DataTable, IReadOnlyList<(string Column, string Header)>>? TableReady;

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public ResultRun? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (!Set(ref _selectedRun, value)) return;
            Items.Clear();
            if (value?.Result is not null)
                foreach (var i in value.Result.Items) Items.Add(new ItemNode(i));
            Raise(nameof(SelectedRunWarnings));
        }
    }

    public string SelectedRunWarnings =>
        _selectedRun?.Error?.Detail ?? string.Join(Environment.NewLine, _selectedRun?.Result?.Warnings ?? new List<string>());

    public ItemNode? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (Set(ref _selectedItem, value)) Raise(nameof(SelectedProperties));
        }
    }

    public IEnumerable<PropertyRow> SelectedProperties => _selectedItem?.Properties ?? Enumerable.Empty<PropertyRow>();

    public string ColumnFilter
    {
        get => _columnFilter;
        set
        {
            if (Set(ref _columnFilter, value)) Raise(nameof(FilteredColumnGroups));
        }
    }

    public IEnumerable<ColumnGroupNode> FilteredColumnGroups =>
        string.IsNullOrWhiteSpace(_columnFilter)
            ? ColumnGroups
            : ColumnGroups.Where(g => g.Name.Contains(_columnFilter, StringComparison.OrdinalIgnoreCase)
                                      || g.Columns.Any(c => c.Name.Contains(_columnFilter, StringComparison.OrdinalIgnoreCase)));

    // ---------------------------------------------------------------- agents

    public async Task RefreshAsync()
    {
        Status = "Looking for running hosts…";
        var found = AgentDiscovery.Discover();

        foreach (var pid in _agents.Keys.Where(pid => found.All(f => f.Host.ProcessId != pid)).ToList())
        {
            var gone = _agents[pid];
            _agents.Remove(pid);
            Agents.Remove(gone);
            await gone.Connection.DisposeAsync();
        }

        var tasks = new List<Task>();
        foreach (var reg in found)
        {
            if (!_agents.TryGetValue(reg.Host.ProcessId, out var node))
            {
                node = new AgentNode(new AgentConnection(reg));
                _agents[reg.Host.ProcessId] = node;
                Agents.Add(node);
            }
            tasks.Add(RefreshAgentAsync(node, reg));
        }
        await Task.WhenAll(tasks);

        RebuildReaders();
        int docs = Agents.Sum(a => a.Documents.Count);
        Status = found.Count == 0
            ? "No hosts found. Start Revit/AutoCAD/Civil 3D with the Nexus add-in loaded, then Refresh."
            : $"{found.Count} host(s), {docs} document(s).";
    }

    private static async Task RefreshAgentAsync(AgentNode node, AgentRegistration reg)
    {
        await node.Connection.RefreshAsync(reg);
        var checkedIds = node.Documents.Where(d => d.IsChecked).Select(d => d.Info.Id).ToHashSet();
        bool first = node.Documents.Count == 0;
        node.Documents.Clear();
        foreach (var d in node.Connection.Documents)
        {
            node.Documents.Add(new DocumentNode(node, d)
            {
                // First time: pre-check the active document of each host.
                IsChecked = first ? d.IsActive : checkedIds.Contains(d.Id),
            });
        }
        node.Refreshed();
    }

    private void RebuildReaders()
    {
        var previous = Readers.ToDictionary(r => (r.HostKind, r.Descriptor.Id));
        Readers.Clear();
        var seen = new HashSet<(string, string)>();
        foreach (var agent in Agents)
        {
            string kind = agent.Connection.Host.HostKind;
            foreach (var d in agent.Connection.Readers)
            {
                if (!seen.Add((kind, d.Id))) continue;
                if (previous.TryGetValue((kind, d.Id), out var old))
                {
                    Readers.Add(old);
                    continue;
                }
                Readers.Add(new ReaderNode(d, kind) { IsChecked = d.IsImplemented });
            }
        }
    }

    // ---------------------------------------------------------------- reading

    private async Task RunAsync()
    {
        var docs = Agents.SelectMany(a => a.Documents).Where(d => d.IsChecked).ToList();
        var readers = Readers.Where(r => r.IsChecked).ToList();
        if (docs.Count == 0 || readers.Count == 0)
        {
            Status = "Check at least one document and one reader.";
            return;
        }

        _busy = true;
        try
        {
            var perAgent = docs.GroupBy(d => d.Agent).Select(async group =>
            {
                var agent = group.Key;
                foreach (var doc in group)
                {
                    foreach (var reader in readers.Where(r => r.HostKind == agent.Connection.Host.HostKind))
                    {
                        if (agent.Connection.Readers.All(r => r.Id != reader.Descriptor.Id)) continue;
                        Status = $"Reading {reader.Descriptor.DisplayName} from {doc.Info.Title}…";
                        Results.Add(await ReadOneAsync(agent, doc, reader));
                    }
                }
            });
            await Task.WhenAll(perAgent);
        }
        finally
        {
            _busy = false;
        }

        RebuildColumns();
        BuildTable();
        int failed = Results.Count(r => r.Failed);
        Status = $"{Results.Count} result(s){(failed > 0 ? $", {failed} failed (see the Results list)" : "")}.";
    }

    private static async Task<ResultRun> ReadOneAsync(AgentNode agent, DocumentNode doc, ReaderNode reader)
    {
        var host = agent.Connection.Host;
        try
        {
            var result = await agent.Connection.ReadAsync(new ReadRequest
            {
                DocumentId = doc.Info.Id,
                ReaderId = reader.Descriptor.Id,
                Options = reader.OptionValues(),
            });
            HubLog.Info($"{host.DisplayName}: {reader.Descriptor.Id} on {doc.Info.Title}: {result.Items.Count} items in {result.ElapsedMs} ms");
            return new ResultRun { Host = host, DocumentTitle = doc.Info.Title, ReaderId = reader.Descriptor.Id, Result = result };
        }
        catch (AgentRequestException ex)
        {
            HubLog.Warn($"{host.DisplayName}: {reader.Descriptor.Id} on {doc.Info.Title}: {ex.Error}");
            return new ResultRun { Host = host, DocumentTitle = doc.Info.Title, ReaderId = reader.Descriptor.Id, Error = ex.Error };
        }
        catch (Exception ex)
        {
            HubLog.Error($"{host.DisplayName}: {reader.Descriptor.Id} on {doc.Info.Title} failed", ex);
            return new ResultRun
            {
                Host = host, DocumentTitle = doc.Info.Title, ReaderId = reader.Descriptor.Id,
                Error = new ErrorInfo(ErrorCodes.Disconnected, ex.Message),
            };
        }
    }

    // ---------------------------------------------------------------- columns & table

    private IEnumerable<ResultSource> IncludedSources() =>
        Results.Where(r => r.IsIncluded).Select(r => r.ToSource()).OfType<ResultSource>();

    public void RebuildColumns()
    {
        var table = ResultTable.Build(IncludedSources());
        var previous = ColumnGroups.SelectMany(g => g.Columns).ToDictionary(c => c.Key.Id, c => c.IsChecked);
        bool autoCheck = table.Columns.Count <= AutoCheckColumnLimit;

        ColumnGroups.Clear();
        foreach (var group in table.Columns.GroupBy(c => c.Group))
        {
            var g = new ColumnGroupNode(group.Key);
            foreach (var key in group)
            {
                var node = new ColumnNode(g, key);
                node.IsChecked = previous.TryGetValue(key.Id, out var was) ? was : autoCheck;
                g.Columns.Add(node);
            }
            g.ChildChanged();
            ColumnGroups.Add(g);
        }
        Raise(nameof(FilteredColumnGroups));
        if (!autoCheck)
            Status = $"{table.Columns.Count} properties found. Pick the ones you want in the Columns tab, then Show table.";
    }

    private Task SetAllColumns(bool value)
    {
        foreach (var g in FilteredColumnGroups) g.IsChecked = value;
        return Task.CompletedTask;
    }

    private HashSet<string> CheckedColumnIds() =>
        ColumnGroups.SelectMany(g => g.Columns).Where(c => c.IsChecked).Select(c => c.Key.Id).ToHashSet(StringComparer.Ordinal);

    public void BuildTable()
    {
        var table = ResultTable.Build(IncludedSources());
        var selected = CheckedColumnIds();
        var cols = table.Columns.Where(c => selected.Contains(c.Id)).Take(MaxGridColumns).ToList();

        var dt = new DataTable();
        var headers = new List<(string, string)>();
        void AddCol(string header)
        {
            string name = "c" + dt.Columns.Count;
            dt.Columns.Add(name, typeof(string));
            headers.Add((name, header));
        }

        foreach (var h in new[] { "Host", "Document", "Reader", "Item Type", "Item", "Key" }) AddCol(h);
        foreach (var c in cols) AddCol(c.Id);

        foreach (var r in table.Rows)
        {
            var values = new List<object?> { r.Host, r.Document, r.Reader, r.ItemType, new string(' ', r.Depth * 3) + r.Item, r.Key };
            values.AddRange(cols.Select(c => r.Values.TryGetValue(c.Id, out var v) ? v.Value : null));
            dt.Rows.Add(values.ToArray());
        }

        TableReady?.Invoke(dt, headers);
        if (selected.Count > MaxGridColumns)
            Status = $"Showing the first {MaxGridColumns} of {selected.Count} checked columns (exports include all).";
    }

    // ---------------------------------------------------------------- export

    private Task Export(string kind)
    {
        var sources = IncludedSources().ToList();
        if (sources.Count == 0)
        {
            Status = "Nothing to export: run some readers first (and tick them in the Results list).";
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(NexusPaths.ExportsDir);
        var dialog = new SaveFileDialog
        {
            InitialDirectory = NexusPaths.ExportsDir,
            FileName = $"nexus-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}",
            Filter = kind == "json" ? "JSON (*.json)|*.json" : "CSV for Excel (*.csv)|*.csv",
        };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;

        try
        {
            var selected = CheckedColumnIds();
            switch (kind)
            {
                case "wide": Exporters.WriteWideCsv(ResultTable.Build(sources), selected, dialog.FileName); break;
                case "long": Exporters.WriteLongCsv(ResultTable.Build(sources), selected, dialog.FileName); break;
                default: Exporters.WriteJson(sources, dialog.FileName); break;
            }
            Status = "Exported " + dialog.FileName;
            HubLog.Info(Status);
        }
        catch (Exception ex)
        {
            HubLog.Error("Export failed", ex);
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return Task.CompletedTask;
    }
}
