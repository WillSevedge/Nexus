using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub.ViewModels;

/// <summary>A connected program (one Revit/AutoCAD/Civil 3D process) in the left panel.</summary>
public sealed class AgentNode : Observable
{
    private string? _error;

    public AgentNode(AgentConnection connection) => Connection = connection;

    public AgentConnection Connection { get; }
    public ObservableCollection<DocumentNode> Documents { get; } = new();

    public string Title
    {
        get
        {
            var h = Connection.Host;
            return $"{h.Product} {h.Version}";
        }
    }

    public string Subtitle => $"pid {Connection.Host.ProcessId}" + (Connection.Host.Modules.Count > 0 ? " · " + string.Join(", ", Connection.Host.Modules) : "");

    public string? Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    public void Refreshed()
    {
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
        Error = Connection.LastError;
    }
}

/// <summary>An open file; ticked files are loaded into the grid.</summary>
public sealed class DocumentNode : Observable
{
    private bool _isChecked;

    public DocumentNode(AgentNode agent, DocumentInfo info)
    {
        Agent = agent;
        Info = info;
    }

    public AgentNode Agent { get; }
    public DocumentInfo Info { get; set; }

    /// <summary>Raised when the user ticks or unticks the file.</summary>
    public event Action<DocumentNode>? CheckedChanged;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value)) CheckedChanged?.Invoke(this);
        }
    }

    /// <summary>Set without raising <see cref="CheckedChanged"/> (when restoring state).</summary>
    public void Restore(bool isChecked) => Set(ref _isChecked, isChecked, nameof(IsChecked));

    public string Title => Info.Title;

    public string Flags
    {
        get
        {
            var flags = new List<string>();
            if (Info.IsActive) flags.Add("active");
            if (Info.IsReadOnly) flags.Add("read-only");
            if (Info.IsModified) flags.Add("unsaved");
            foreach (var (k, v) in Info.Extra)
                if (v == "True") flags.Add(k.Replace("Is", "").ToLowerInvariant());
            return string.Join(" · ", flags);
        }
    }

    public string ToolTip => Info.Path ?? Info.Title;
}

/// <summary>One reader of one program, with its options (shown under Options for the dataset).</summary>
public sealed class ReaderNode
{
    public ReaderNode(ReaderDescriptor descriptor, string hostKind, string hostLabel)
    {
        Descriptor = descriptor;
        HostKind = hostKind;
        HostLabel = hostLabel;
        foreach (var o in descriptor.Options) Options.Add(new OptionNode(o));
    }

    public ReaderDescriptor Descriptor { get; }
    public string HostKind { get; }
    public string HostLabel { get; }
    public ObservableCollection<OptionNode> Options { get; } = new();
    public string Title => $"{HostLabel}: {Descriptor.DisplayName}";

    public Dictionary<string, string> OptionValues() =>
        Options.Where(o => o.Value is not null).ToDictionary(o => o.Option.Name, o => o.Value!);
}

public sealed class OptionNode : Observable
{
    private string? _value;

    public OptionNode(ReaderOption option)
    {
        Option = option;
        _value = option.Default;
    }

    public ReaderOption Option { get; }
    public bool IsBool => Option.Type == "bool";
    public bool IsText => !IsBool;

    public string? Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value)) Raise(nameof(BoolValue));
        }
    }

    public bool BoolValue
    {
        get => string.Equals(_value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }
}

/// <summary>Something to look at: the cross-program sheet index, or one reader's data.</summary>
public sealed class DatasetNode
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    /// <summary>"Across programs", "Revit", "AutoCAD"... (grouping in the picker).</summary>
    public string Category { get; init; } = "";
    /// <summary>Adds the standard Sheet › … columns.</summary>
    public bool IsSheetIndex { get; init; }
    /// <summary>Host kind → the reader that supplies this dataset there.</summary>
    public Dictionary<string, ReaderNode> Readers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Programs => string.Join(", ", Readers.Values.Select(r => r.HostLabel).Distinct());
}

/// <summary>One read of one file (kept for re-reading after edits and for warnings).</summary>
public sealed class ResultRun
{
    public required HostInfo Host { get; init; }
    public required string DocumentTitle { get; init; }
    public required string ReaderId { get; init; }
    public AgentNode? Agent { get; init; }
    public ReadRequest? Request { get; init; }
    public ReadResult? Result { get; init; }
    public ErrorInfo? Error { get; init; }

    public string Title => $"{DocumentTitle} · {ReaderId}";
    public bool Failed => Error is not null;

    public ResultSource? ToSource() => Result is null ? null : new ResultSource
    {
        Host = Host,
        DocumentTitle = DocumentTitle,
        Result = Result,
        Tag = this,
    };
}

/// <summary>Column chooser: a group of columns.</summary>
public sealed class ColumnGroupNode : Observable
{
    private bool? _isChecked = false;
    private bool _updating;

    public ColumnGroupNode(string name) => Name = name;

    public string Name { get; }
    public ObservableCollection<ColumnNode> Columns { get; } = new();
    public string Title => $"{Name}  ({Columns.Count})";

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (!Set(ref _isChecked, value) || value is null) return;
            _updating = true;
            foreach (var c in Columns) c.IsChecked = value.Value;
            _updating = false;
        }
    }

    internal void ChildChanged()
    {
        if (_updating) return;
        bool all = Columns.All(c => c.IsChecked), none = Columns.All(c => !c.IsChecked);
        Set(ref _isChecked, all ? true : none ? false : null, nameof(IsChecked));
    }
}

public sealed class ColumnNode : Observable
{
    private bool _isChecked;

    public ColumnNode(ColumnGroupNode group, ColumnKey key)
    {
        Group = group;
        Key = key;
    }

    public ColumnGroupNode Group { get; }
    public ColumnKey Key { get; }
    public string Name => Key.Name;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value)) Group.ChildChanged();
        }
    }
}

/// <summary>Details pane: one property of the selected row, editable in place.</summary>
public sealed class DetailRow : Observable, IDisposable
{
    private readonly DataRowView _view;
    private readonly Func<DataRowView, string, string?> _blocker;

    public DetailRow(DataRowView view, string gridColumn, string group, string name, PropertyValue property,
        Func<DataRowView, string, string?> blocker)
    {
        _view = view;
        _blocker = blocker;
        GridColumn = gridColumn;
        Group = group;
        Name = name;
        Property = property;
        ((INotifyPropertyChanged)_view).PropertyChanged += OnViewChanged;
    }

    public string GridColumn { get; }
    public string Group { get; }
    public string Name { get; }
    public PropertyValue Property { get; }

    public string? Reason => _blocker(_view, GridColumn);
    public bool IsEditable => Reason is null;
    public bool IsReadOnly => !IsEditable;

    public bool IsEdited
    {
        get
        {
            var row = _view.Row;
            return row.RowState == DataRowState.Modified &&
                   !Editing.SameValue(row[GridColumn, DataRowVersion.Original] as string, row[GridColumn] as string);
        }
    }

    public string ToolTip => IsEdited
        ? $"Edited. Was: {_view.Row[GridColumn, DataRowVersion.Original]}"
        : Reason is { } r ? "Not editable: " + r
        : $"{Property.Source}{(Property.DataType is null ? "" : " · " + Property.DataType)}{(Property.Units is null ? "" : " · " + Property.Units)}";

    public string? Value
    {
        get => _view[GridColumn] as string;
        set
        {
            if (!IsEditable || Editing.SameValue(value, Value)) return;
            _view.BeginEdit();
            _view[GridColumn] = value ?? "";
            _view.EndEdit();
        }
    }

    private void OnViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != GridColumn && !string.IsNullOrEmpty(e.PropertyName)) return;
        Raise(nameof(Value));
        Raise(nameof(IsEdited));
        Raise(nameof(ToolTip));
    }

    public void Dispose() => ((INotifyPropertyChanged)_view).PropertyChanged -= OnViewChanged;
}

/// <summary>A titled list of <see cref="DetailRow"/>s.</summary>
public sealed class DetailGroup
{
    public DetailGroup(string name) => Name = name;
    public string Name { get; }
    public List<DetailRow> Rows { get; } = new();
}
