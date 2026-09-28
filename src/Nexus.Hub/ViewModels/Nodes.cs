using System.Collections.ObjectModel;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub.ViewModels;

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
            var modules = h.Modules.Count > 0 ? $" + {string.Join(", ", h.Modules)}" : "";
            return $"{h.Product} {h.Version}{modules}  (pid {h.ProcessId})";
        }
    }

    public string? Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    public void Refreshed()
    {
        Raise(nameof(Title));
        Error = Connection.LastError;
    }
}

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

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public string Title
    {
        get
        {
            var flags = new List<string>();
            if (Info.IsActive) flags.Add("active");
            if (Info.IsReadOnly) flags.Add("read-only");
            foreach (var (k, v) in Info.Extra)
                if (v == "True") flags.Add(k.Replace("Is", "").ToLowerInvariant());
            return flags.Count == 0 ? Info.Title : $"{Info.Title}  [{string.Join(", ", flags)}]";
        }
    }

    public string ToolTip => Info.Path ?? Info.Title;
}

public sealed class ReaderNode : Observable
{
    private bool _isChecked;

    public ReaderNode(ReaderDescriptor descriptor, string hostKind)
    {
        Descriptor = descriptor;
        HostKind = hostKind;
        foreach (var o in descriptor.Options) Options.Add(new OptionNode(o));
    }

    public ReaderDescriptor Descriptor { get; }
    public string HostKind { get; }
    public ObservableCollection<OptionNode> Options { get; } = new();

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public string Title => Descriptor.IsImplemented ? Descriptor.DisplayName : Descriptor.DisplayName;
    public string Subtitle => $"{HostKind} · {Descriptor.Id}";
    public double Opacity => Descriptor.IsImplemented ? 1.0 : 0.55;

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

public sealed class ResultRun : Observable
{
    private bool _isIncluded = true;

    public required HostInfo Host { get; init; }
    public required string DocumentTitle { get; init; }
    public required string ReaderId { get; init; }
    /// <summary>Where the result came from, so it can be read again after edits.</summary>
    public AgentNode? Agent { get; init; }
    public ReadRequest? Request { get; init; }
    public ReadResult? Result { get; init; }
    public ErrorInfo? Error { get; init; }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => Set(ref _isIncluded, value);
    }

    public string Title => $"{DocumentTitle} · {ReaderId}";

    public string Summary => Error is not null
        ? $"{Error.Code}: {Error.Message}"
        : $"{Result!.Items.Count} items · {Result.ElapsedMs} ms" +
          (Result.Warnings.Count > 0 ? $" · {Result.Warnings.Count} warnings" : "") +
          (Result.Truncated ? " · truncated" : "");

    public bool Failed => Error is not null;
    public string HostTitle => Host.DisplayName;

    public ResultSource? ToSource() => Result is null ? null : new ResultSource
    {
        Host = Host,
        DocumentTitle = DocumentTitle,
        Result = Result,
        Tag = this,
    };
}

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

/// <summary>Item tree node in the "Items" tab.</summary>
public sealed class ItemNode
{
    public ItemNode(DataItem item)
    {
        Item = item;
        Children = item.Children.Select(c => new ItemNode(c)).ToList();
    }

    public DataItem Item { get; }
    public List<ItemNode> Children { get; }
    public string Title => $"{Item.ItemType}: {Item.Name}";

    public IEnumerable<PropertyRow> Properties =>
        Item.Groups.SelectMany(g => g.Properties.Select(p => new PropertyRow(g.Name, p)));
}

public sealed record PropertyRow(string Group, PropertyValue P)
{
    public string Name => P.Name;
    public string? Value => P.Value;
    public string ReadOnly => P.IsReadOnly ? "Yes" : "";
    public string? Reason => P.ReadOnlyReason;
    public string Source => P.Source.ToString();
    public string? Storage => P.StorageType;
    public string? DataType => P.DataType;
    public string? Units => P.Units;
    public string? Id => P.Id;
}
