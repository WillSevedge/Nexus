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
    private bool _refreshing;

    // The table as shown: the flattened results, the grid's DataTable, and which
    // grid column ("c7") holds which property column ("Group › Name").
    private const string RowIndexColumn = "__row";
    private ResultTable? _table;
    private DataTable? _grid;
    private readonly Dictionary<string, string> _gridColumnIds = new(StringComparer.Ordinal);
    private int _pendingEdits;

    public MainViewModel()
    {
        RefreshCommand = new RelayCommand(RefreshAsync);
        RunCommand = new RelayCommand(RunAsync, () => !_busy);
        ShowTableCommand = new RelayCommand(() =>
        {
            if (!ConfirmDiscardEdits()) return Task.CompletedTask;
            RebuildColumns();
            BuildTable();
            return Task.CompletedTask;
        });
        ApplyChangesCommand = new RelayCommand(ReviewAndApplyAsync, () => _pendingEdits > 0 && !_busy);
        DiscardChangesCommand = new RelayCommand(() =>
        {
            DiscardEdits();
            return Task.CompletedTask;
        }, () => _pendingEdits > 0 && !_busy);
        ExportWideCommand = new RelayCommand(() => Export("wide"));
        ExportLongCommand = new RelayCommand(() => Export("long"));
        ExportJsonCommand = new RelayCommand(() => Export("json"));
        ClearResultsCommand = new RelayCommand(() =>
        {
            if (!ConfirmDiscardEdits()) return Task.CompletedTask;
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
    public RelayCommand ApplyChangesCommand { get; }
    public RelayCommand DiscardChangesCommand { get; }
    public RelayCommand ExportWideCommand { get; }
    public RelayCommand ExportLongCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand ClearResultsCommand { get; }
    public RelayCommand CheckAllColumnsCommand { get; }
    public RelayCommand UncheckAllColumnsCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }

    /// <summary>Raised after each refresh with a one-line summary (tray tooltip).</summary>
    public event Action<string>? HostsChanged;

    /// <summary>Raised before edits are collected; the window commits any cell still being edited.</summary>
    public event Action? CommitGridEdits;

    /// <summary>Raised when the wide table is rebuilt; the window builds the grid columns.</summary>
    public event Action<DataTable, IReadOnlyList<(string Column, string Header)>>? TableReady;

    /// <summary>Number of edited cells not yet sent to the hosts.</summary>
    public int PendingEdits
    {
        get => _pendingEdits;
        private set
        {
            if (Set(ref _pendingEdits, value)) Raise(nameof(ApplyLabel));
        }
    }

    public string ApplyLabel => _pendingEdits == 0 ? "Apply changes" : $"Apply {_pendingEdits} change(s)…";

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
            ? "No hosts found. Start Revit, AutoCAD or Civil 3D with the Nexus add-in loaded; they appear here automatically."
            : $"{found.Count} host(s), {docs} document(s).";
        HostsChanged?.Invoke(found.Count == 0
            ? "waiting for Revit, AutoCAD or Civil 3D"
            : string.Join(", ", Agents.Select(a => $"{a.Connection.Host.Product} {a.Connection.Host.Version}")));
    }

    /// <summary>Cheap poll (reads the registration files): refreshes only when a host started or stopped.</summary>
    public async Task RefreshIfHostsChangedAsync()
    {
        if (_busy || _refreshing) return;
        var pids = AgentDiscovery.Discover().Select(r => r.Host.ProcessId).ToHashSet();
        if (pids.SetEquals(_agents.Keys)) return;
        _refreshing = true;
        try { await RefreshAsync(); }
        finally { _refreshing = false; }
    }

    /// <summary>Asks before exiting with unapplied edits.</summary>
    public bool ConfirmExit()
    {
        CommitGridEdits?.Invoke();
        if (_pendingEdits == 0) return true;
        return MessageBox.Show($"You have {_pendingEdits} edit(s) that have not been applied. Exit Nexus anyway?",
            "Nexus", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
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
        if (!ConfirmDiscardEdits()) return;
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

    private static Task<ResultRun> ReadOneAsync(AgentNode agent, DocumentNode doc, ReaderNode reader) =>
        ReadOneAsync(agent, doc.Info.Title, new ReadRequest
        {
            DocumentId = doc.Info.Id,
            ReaderId = reader.Descriptor.Id,
            Options = reader.OptionValues(),
        });

    private static async Task<ResultRun> ReadOneAsync(AgentNode agent, string documentTitle, ReadRequest request)
    {
        var host = agent.Connection.Host;
        try
        {
            var result = await agent.Connection.ReadAsync(request);
            HubLog.Info($"{host.DisplayName}: {request.ReaderId} on {documentTitle}: {result.Items.Count} items in {result.ElapsedMs} ms");
            return new ResultRun
            {
                Host = host, DocumentTitle = documentTitle, ReaderId = request.ReaderId,
                Agent = agent, Request = request, Result = result,
            };
        }
        catch (AgentRequestException ex)
        {
            HubLog.Warn($"{host.DisplayName}: {request.ReaderId} on {documentTitle}: {ex.Error}");
            return new ResultRun
            {
                Host = host, DocumentTitle = documentTitle, ReaderId = request.ReaderId,
                Agent = agent, Request = request, Error = ex.Error,
            };
        }
        catch (Exception ex)
        {
            HubLog.Error($"{host.DisplayName}: {request.ReaderId} on {documentTitle} failed", ex);
            return new ResultRun
            {
                Host = host, DocumentTitle = documentTitle, ReaderId = request.ReaderId,
                Agent = agent, Request = request, Error = new ErrorInfo(ErrorCodes.Disconnected, ex.Message),
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

        if (_grid is not null) _grid.ColumnChanged -= OnGridValueChanged;
        _gridColumnIds.Clear();

        var dt = new DataTable();
        dt.Columns.Add(RowIndexColumn, typeof(int));
        var headers = new List<(string, string)>();
        string AddCol(string header)
        {
            string name = "c" + (dt.Columns.Count - 1);
            dt.Columns.Add(name, typeof(string));
            headers.Add((name, header));
            return name;
        }

        foreach (var h in new[] { "Host", "Document", "Reader", "Item Type", "Item", "Key" }) AddCol(h);
        foreach (var c in cols) _gridColumnIds[AddCol(c.Id)] = c.Id;

        for (int i = 0; i < table.Rows.Count; i++)
        {
            var r = table.Rows[i];
            var values = new List<object?> { i, r.Host, r.Document, r.Reader, r.ItemType, new string(' ', r.Depth * 3) + r.Item, r.Key };
            values.AddRange(cols.Select(c => r.Values.TryGetValue(c.Id, out var v) ? v.Value : null));
            dt.Rows.Add(values.ToArray());
        }
        dt.AcceptChanges();
        dt.ColumnChanged += OnGridValueChanged;

        _table = table;
        _grid = dt;
        PendingEdits = 0;
        TableReady?.Invoke(dt, headers);
        if (selected.Count > MaxGridColumns)
            Status = $"Showing the first {MaxGridColumns} of {selected.Count} checked columns (exports include all).";
    }

    // ---------------------------------------------------------------- editing

    /// <summary>True for grid columns that hold a property (not Host, Document, Item...).</summary>
    public bool IsPropertyColumn(string gridColumn) => _gridColumnIds.ContainsKey(gridColumn);

    private TableRow? RowOf(DataRowView view) =>
        _table is not null && view.Row.Table == _grid && view.Row[RowIndexColumn] is int i && i < _table.Rows.Count
            ? _table.Rows[i]
            : null;

    /// <summary>Why this cell cannot be edited, or null if it can.</summary>
    public string? EditBlocker(DataRowView view, string gridColumn)
    {
        if (!_gridColumnIds.TryGetValue(gridColumn, out var columnId)) return "Only property values can be edited.";
        var row = RowOf(view);
        return row is null ? "This row is out of date: show the table again." : Editing.Blocker(row, columnId);
    }

    /// <summary>The value the cell had when the table was built, if the user has changed it.</summary>
    public bool IsEdited(DataRowView view, string gridColumn, out string? original)
    {
        original = null;
        var row = view.Row;
        if (row.RowState != DataRowState.Modified || !_gridColumnIds.ContainsKey(gridColumn)) return false;
        original = row[gridColumn, DataRowVersion.Original] as string;
        return !Editing.SameValue(original, row[gridColumn, DataRowVersion.Current] as string);
    }

    private void OnGridValueChanged(object sender, DataColumnChangeEventArgs e) => PendingEdits = CollectEdits().Count;

    private List<CellEdit> CollectEdits()
    {
        var edits = new List<CellEdit>();
        if (_grid is null || _table is null) return edits;
        foreach (DataRow row in _grid.Rows)
        {
            if (row.RowState != DataRowState.Modified) continue;
            var tableRow = _table.Rows[(int)row[RowIndexColumn]];
            foreach (var (gridColumn, columnId) in _gridColumnIds)
            {
                string? now = row[gridColumn, DataRowVersion.Current] as string;
                if (Editing.SameValue(row[gridColumn, DataRowVersion.Original] as string, now)) continue;
                if (!tableRow.Values.ContainsKey(columnId)) continue;
                edits.Add(new CellEdit(tableRow, columnId, now ?? ""));
            }
        }
        return edits;
    }

    private void DiscardEdits()
    {
        _grid?.RejectChanges();
        PendingEdits = 0;
        Status = "Edits discarded.";
    }

    /// <summary>Asks before throwing away unapplied edits. True when there are none or the user agrees.</summary>
    private bool ConfirmDiscardEdits()
    {
        CommitGridEdits?.Invoke();
        if (_pendingEdits == 0) return true;
        var answer = MessageBox.Show(
            $"You have {_pendingEdits} edit(s) that have not been applied. Discard them?",
            "Nexus", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return false;
        DiscardEdits();
        return true;
    }

    private async Task ReviewAndApplyAsync()
    {
        CommitGridEdits?.Invoke();
        var edits = CollectEdits();
        PendingEdits = edits.Count;
        if (edits.Count == 0) return;

        var review = new ApplyChangesWindow(new ApplyChangesViewModel(edits, ApplyAsync))
        {
            Owner = Application.Current.MainWindow,
        };
        review.ShowDialog();

        var changed = review.ViewModel.ChangedRuns;
        if (changed.Count > 0) await RereadAsync(changed);
    }

    /// <summary>Sends the edits, one request per host document, and fills in each row's outcome.</summary>
    private async Task<List<ResultRun>> ApplyAsync(IReadOnlyList<ChangeRow> rows)
    {
        _busy = true;
        var changedRuns = new List<ResultRun>();
        try
        {
            var byEdit = rows.ToDictionary(r => r.Edit);
            foreach (var (pid, request, edits) in Editing.Plan(rows.Select(r => r.Edit)))
            {
                var planned = edits.Select(e => byEdit[e]).ToList();
                foreach (var r in rows.Where(r => r.Status == "" && r.Edit.ProcessId == pid && r.Edit.DocumentId == request.DocumentId && !planned.Contains(r)))
                    r.SetOutcome("Skipped", "The same property is edited in another row; that edit is used.", null);

                var run = edits[0].Row.Source.Tag as ResultRun;
                var agent = run?.Agent;
                if (agent is null)
                {
                    foreach (var r in planned) r.SetOutcome("Failed", "The host for this result is no longer connected.", null);
                    continue;
                }

                Status = $"Applying {request.Changes.Count} change(s) to {edits[0].Row.Document}…";
                try
                {
                    var result = await agent.Connection.WriteAsync(request);
                    foreach (var cr in result.Results)
                    {
                        if (cr.Index < 0 || cr.Index >= planned.Count) continue;
                        planned[cr.Index].SetOutcome(cr.Status.ToString(), cr.Message, cr.NewValue);
                    }
                    foreach (var w in result.Warnings) HubLog.Warn($"{result.DocumentTitle}: {w}");
                    HubLog.Info($"{result.DocumentTitle}: {(result.Committed ? "committed" : "nothing committed")} " +
                                $"({result.Results.Count(c => c.Status == ChangeStatus.Applied)} applied) in {result.ElapsedMs} ms");
                    if (result.Committed)
                        changedRuns.AddRange(Results.Where(x => x.Agent == agent && x.Result?.DocumentId == request.DocumentId));
                    if (result.Warnings.Count > 0)
                        foreach (var r in planned.Where(r => r.Status == "Applied"))
                            r.Message = "Revit warning: " + string.Join("; ", result.Warnings);
                }
                catch (AgentRequestException ex)
                {
                    foreach (var r in planned) r.SetOutcome("Failed", ex.Error.Message, null);
                    HubLog.Warn($"Write to {edits[0].Row.Document} failed: {ex.Error}");
                }
            }
        }
        finally
        {
            _busy = false;
        }
        int applied = rows.Count(r => r.Status == "Applied");
        Status = $"{applied} of {rows.Count} change(s) applied.";
        return changedRuns.Distinct().ToList();
    }

    /// <summary>Reads changed results again so the table shows what the host now has.</summary>
    private async Task RereadAsync(IReadOnlyList<ResultRun> runs)
    {
        _busy = true;
        try
        {
            foreach (var old in runs)
            {
                if (old.Agent is null || old.Request is null) continue;
                Status = $"Refreshing {old.Title}…";
                var fresh = await ReadOneAsync(old.Agent, old.DocumentTitle, old.Request);
                fresh.IsIncluded = old.IsIncluded;
                int index = Results.IndexOf(old);
                if (index >= 0) Results[index] = fresh;
                if (SelectedRun == old) SelectedRun = fresh;
            }
        }
        finally
        {
            _busy = false;
        }
        RebuildColumns();
        BuildTable();
        Status = $"Applied. Refreshed {runs.Count} result(s) from the host. Use Undo in Revit to revert.";
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
