namespace Nexus.Contracts;

/// <summary>Optional agent capabilities advertised in <see cref="HostInfo.Features"/>.</summary>
public static class AgentFeatures
{
    /// <summary>The agent accepts <see cref="MessageTypes.Write"/> requests.</summary>
    public const string Write = "write";
    /// <summary>The agent accepts <see cref="MessageTypes.Select"/> requests (show items in the host).</summary>
    public const string Select = "select";
    /// <summary>The agent accepts <see cref="MessageTypes.ReadFile"/> requests (files that are not open).</summary>
    public const string ReadFile = "readFile";
    /// <summary>The agent accepts <see cref="MessageTypes.ExportPdf"/> requests.</summary>
    public const string ExportPdf = "exportPdf";
    /// <summary>
    /// Set by the hub on results read from a file on disk (not open in a program): shown read-only.
    /// Never sent by an agent.
    /// </summary>
    public const string FileOnDisk = "fileOnDisk";
}

/// <summary>Show items in the host: open the sheet/layout, or select and zoom to objects.</summary>
public sealed class SelectRequest
{
    public string DocumentId { get; set; } = "";
    /// <summary><see cref="DataItem.Id"/> values (Revit UniqueId, AutoCAD handle).</summary>
    public List<string> ItemIds { get; set; } = new();
}

public sealed class SelectResult
{
    /// <summary>How many of the items were found and shown.</summary>
    public int Shown { get; set; }
    /// <summary>What the host did, for the status bar.</summary>
    public string Message { get; set; } = "";
}

/// <summary>A batch of property edits for one document. Applied as one undoable host operation.</summary>
public sealed class WriteRequest
{
    /// <summary>A <see cref="DocumentInfo.Id"/> (as returned in <see cref="ReadResult.DocumentId"/>).</summary>
    public string DocumentId { get; set; } = "";
    public List<PropertyChange> Changes { get; set; } = new();
}

/// <summary>Set one property of one object to a new value.</summary>
public sealed class PropertyChange
{
    /// <summary>Host id of the object that owns the property (<see cref="PropertyValue.OwnerId"/>, else the item's id).</summary>
    public string OwnerId { get; set; } = "";
    /// <summary><see cref="PropertyValue.Id"/> of the property.</summary>
    public string? PropertyId { get; set; }
    /// <summary><see cref="PropertyValue.Name"/>, used for messages and as a fallback lookup.</summary>
    public string PropertyName { get; set; } = "";
    /// <summary><see cref="PropertyValue.Source"/> as read.</summary>
    public PropertySource Source { get; set; }
    /// <summary>The new value as the user typed it (display units, e.g. 10' 6").</summary>
    public string Value { get; set; } = "";
    /// <summary>
    /// <see cref="PropertyValue.RawValue"/> when it was read. If the property has changed
    /// in the host since, the change is skipped rather than overwriting someone else's edit.
    /// </summary>
    public string? ExpectedRawValue { get; set; }
}

public enum ChangeStatus
{
    /// <summary>The value was set.</summary>
    Applied,
    /// <summary>The property already had this value.</summary>
    Unchanged,
    /// <summary>Not attempted (object not editable, changed since read, ...).</summary>
    Skipped,
    /// <summary>Attempted and rejected (bad value, host error, whole batch rolled back).</summary>
    Failed,
}

public sealed class ChangeResult
{
    /// <summary>Index into <see cref="WriteRequest.Changes"/>.</summary>
    public int Index { get; set; }
    public ChangeStatus Status { get; set; }
    public string? Message { get; set; }
    /// <summary>The value as the host now shows it.</summary>
    public string? NewValue { get; set; }
}

public sealed class WriteResult
{
    public string DocumentId { get; set; } = "";
    public string DocumentTitle { get; set; } = "";
    public long ElapsedMs { get; set; }
    /// <summary>False when the host rejected the batch and nothing was changed.</summary>
    public bool Committed { get; set; }
    /// <summary>Name of the undo entry in the host, when committed.</summary>
    public string? UndoName { get; set; }
    public List<ChangeResult> Results { get; set; } = new();
    /// <summary>Host warnings raised while committing (e.g. duplicate values).</summary>
    public List<string> Warnings { get; set; } = new();
}
